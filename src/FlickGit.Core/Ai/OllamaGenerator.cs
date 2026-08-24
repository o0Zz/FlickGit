using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FlickGit.Logging;
using FlickGit.Secrets;

namespace FlickGit.Ai;

/// <summary>
/// Ollama, on this machine — the local provider, and the only one where the diff does not leave it.
///
/// It differs from the other three in four ways, and every one of them is a consequence of being
/// local rather than a matter of taste:
///
/// <list type="bullet">
/// <item><description><b>No credential.</b> There is nobody to authenticate to. The
/// <c>authorise</c> callback does nothing, which is why <see cref="AiEndpoint"/> takes it as a
/// delegate rather than as a key.</description></item>
/// <item><description><b>Newline-delimited JSON, not SSE.</b> See
/// <see cref="LineDelimitedJson"/> for why the native API is used rather than the
/// OpenAI-compatible shim beside it.</description></item>
/// <item><description><b>No default model.</b> The other three have a catalogue; this one has
/// whatever the user has pulled onto their disk, so an empty <c>aiModel</c> is refused by name
/// instead of guessed at.</description></item>
/// <item><description><b>The warm-up loads the model</b>, where the hosted three only open a
/// socket. That is the expensive part here, and paying it at logon is the difference between the
/// first commit message of the day arriving in half a second and in half a minute.</description>
/// </item>
/// </list>
/// </summary>
public sealed class OllamaGenerator(HttpClient http, AiOptions options, ILog log) : IAiGenerator
{
    /// <summary>Ollama's native chat endpoint, relative to the configured base.</summary>
    private const string ChatPath = "api/chat";

    /// <summary>
    /// How long the warm-up asks Ollama to keep the model resident.
    ///
    /// Long enough that a preload at logon is still paid off by a commit some minutes later, and
    /// short enough that a machine the user never commits on gets its memory back. Sent only by the
    /// warm-up — a real generation leaves the user's own setting alone.
    /// </summary>
    private const string WarmKeepAlive = "10m";

    public async IAsyncEnumerable<string> GenerateAsync(
        AiPrompt prompt,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string model = Model();

        string json = JsonSerializer.Serialize(
            new OllamaRequest(
                model,
                [
                    new OllamaMessage("system", prompt.System),
                    new OllamaMessage("user", prompt.User),
                ],
                new OllamaGenerationOptions(prompt.MaxTokens)),
            AiJson.Default.OllamaRequest);

        //An async iterator rather than a straight delegation, for the same reason CopilotGenerator
        //is one: the endpoint has to be composed and the model checked before the request exists,
        //and both can throw AiUnavailableException, which a plain expression body would raise at
        //enumeration time rather than at the call.
        await foreach (string chunk in AiEndpoint
            .StreamAsync(
                http,
                "Ollama",
                Endpoint(),
                json,

                //Nothing to authorise. Ollama listens on loopback with no auth of its own, and a
                //header invented here would be a header nothing reads.
                _ => { },
                AiFraming.LineDelimitedJson,
                options.Silence,
                Read,
                cancellationToken)
            .ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    /// <summary>
    /// Loads the model, rather than merely opening a socket.
    ///
    /// A chat request with an empty message list is Ollama's documented "load and stop", and it is
    /// the right warm-up here because the handshake the other three pay for costs nothing on
    /// loopback while the model load costs tens of seconds. CLAUDE.md's "one cheap warm-up request
    /// at service start is fine" — this one is not cheap in wall clock, but it is paid once at
    /// logon in the background instead of inside the user's first commit.
    ///
    /// A refused or unreachable Ollama comes back as an unreachable provider rather than throwing,
    /// so `flick ai` and `diag doctor` can say "Ollama is not running" at startup instead of the
    /// first commit failing with it.
    /// </summary>
    public async Task<AiProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();

        string model;

        try
        {
            model = Model();
        }
        catch (AiUnavailableException ex)
        {
            return new AiProbe(false, clock.Elapsed, ex.Message);
        }

        string json = JsonSerializer.Serialize(
            new OllamaRequest(model, [], null) { Stream = false, KeepAlive = WarmKeepAlive },
            AiJson.Default.OllamaRequest);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint())
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                log.Debug($"Ollama preloaded {model} in {clock.Elapsed.TotalSeconds:F1} s.");
                return new AiProbe(true, clock.Elapsed, null);
            }

            //Reached, but it would not load the model -- almost always because it is not pulled.
            //Reported as unreachable so the reason is surfaced rather than swallowed.
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return new AiProbe(false, clock.Elapsed, Describe(response.StatusCode, body, model));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            //Connection refused is the ordinary answer on a machine where Ollama is not running,
            //which is worth naming precisely: it is the one failure the user fixes in one command.
            return new AiProbe(
                false,
                clock.Elapsed,
                $"{options.OllamaUrl} did not answer. Is Ollama running? {SecretDetector.Redact(ex.Message)}");
        }
    }

    /// <summary>
    /// The model to ask for, or a refusal naming the fix.
    ///
    /// The one configuration error this provider can have, and it is worth its own sentence: with no
    /// default to fall back on, an empty <c>aiModel</c> would otherwise reach Ollama as a request for
    /// a model called "" and come back as a 404 about a name the user never typed.
    /// </summary>
    private string Model() =>
        options.ResolvedModel is { Length: > 0 } model
            ? model
            : throw new AiUnavailableException(
                "No Ollama model is set. Run `ollama list`, then put one in aiModel in settings.json — "
                + "for example \"qwen2.5-coder:7b\".");

    /// <summary>
    /// <c>{aiOllamaUrl}/api/chat</c>.
    ///
    /// The base is normalised to end in a slash before the path is appended, because
    /// <c>new Uri(base, relative)</c> replaces the last segment otherwise — which turns a base of
    /// <c>http://host/ollama</c>, the shape a reverse proxy produces, into <c>http://host/api/chat</c>.
    /// </summary>
    private string Endpoint()
    {
        string root = options.OllamaUrl.Trim();

        if (root.Length == 0)
            root = AiOptions.DefaultOllamaUrl;

        if (!root.EndsWith('/'))
            root += "/";

        return new Uri(new Uri(root), ChatPath).ToString();
    }

    /// <summary>
    /// The text out of one line, and the one error shape.
    ///
    /// <c>done: true</c> arrives with an empty content and the timing statistics, which need no
    /// special case: an empty string is dropped by the endpoint like any other frame carrying no
    /// text.
    /// </summary>
    private string? Read(string frame)
    {
        try
        {
            OllamaEvent? parsed = JsonSerializer.Deserialize(frame, AiJson.Default.OllamaEvent);

            if (parsed?.Error is { Length: > 0 } error)
                throw new AiUnavailableException(SecretDetector.Redact(error));

            return parsed?.Message?.Content;
        }
        catch (JsonException ex)
        {
            log.Debug($"Unparseable Ollama frame ignored: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// A failed warm-up, in a sentence naming the next action.
    ///
    /// 404 is the case worth catching: it means the model is not pulled, and `ollama pull` is the
    /// whole of the fix.
    /// </summary>
    private static string Describe(System.Net.HttpStatusCode status, string body, string model)
    {
        string detail = SecretDetector.Redact(body).Replace('\n', ' ').Trim();

        if (detail.Length > 200)
            detail = detail[..200] + "…";

        return status == System.Net.HttpStatusCode.NotFound
            ? $"Ollama does not have {model}. Pull it with:  ollama pull {model}"
            : $"Ollama returned {(int)status}. {detail}".TrimEnd();
    }
}
