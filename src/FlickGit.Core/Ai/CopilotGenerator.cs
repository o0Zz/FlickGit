using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FlickGit.Logging;
using FlickGit.Secrets;

namespace FlickGit.Ai;

/// <summary>
/// GitHub Copilot's chat endpoint, streamed, on the user's existing subscription.
///
/// The wire format is OpenAI's Chat Completions — <c>choices[0].delta.content</c>, not the Responses
/// API's <c>response.output_text.delta</c> — so this is a third frame reader rather than a reuse of
/// <see cref="OpenAiGenerator"/>'s. That is also why there is no shared base class here
/// either: what these three providers have in common is <see cref="AiEndpoint.StreamAsync"/>, and what
/// differs is exactly the four arguments it takes.
///
/// The one thing unique to this provider is that <b>the stored credential is not what gets sent</b>.
/// The GitHub token buys a short-lived Copilot token from <see cref="CopilotToken"/>, and only that
/// one ever reaches the completion endpoint.
/// </summary>
public sealed class CopilotGenerator(
    HttpClient http,
    AiOptions options,
    CopilotToken tokens,
    ILog log) : IAiGenerator
{
    private const string Endpoint = "https://api.githubcopilot.com/chat/completions";

    /// <summary>
    /// An async iterator rather than a straight delegation, unlike the other two generators: the token
    /// has to be awaited before the request can be authorised, and
    /// <see cref="AiEndpoint.StreamAsync"/>'s <c>authorise</c> callback is synchronous — deliberately,
    /// because for the other two there is nothing to await.
    /// </summary>
    public async IAsyncEnumerable<string> GenerateAsync(
        AiPrompt prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string token = await tokens.ReadAsync(cancellationToken).ConfigureAwait(false);

        string json = JsonSerializer.Serialize(
            new CopilotRequest(
                options.ResolvedModel,
                [
                    new CopilotMessage("system", prompt.System),
                    new CopilotMessage("user", prompt.User),
                ],
                prompt.MaxTokens),
            AiJson.Default.CopilotRequest);

        bool completed = false;

        try
        {
            await foreach (string chunk in AiEndpoint
                .StreamAsync(
                    http,
                    "Copilot",
                    Endpoint,
                    json,
                    request => Authorise(request, token),
                    AiFraming.ServerSentEvents,
                    options.Silence,
                    Read,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                yield return chunk;
            }

            completed = true;
        }
        finally
        {
            //A token the endpoint refused stays in the cache for its whole nominal life otherwise, so
            //every commit until then fails for a reason the user cannot see or fix. Dropping it makes
            //the failure self-healing: the next generation exchanges a fresh one.
            //
            //There is no retry inside this call, on purpose. The margin in CopilotToken makes an
            //expiry mid-request rare, and a retry would need the enumerator driven by hand -- C#
            //forbids a catch around a yield -- for a case the next keystroke already fixes.
            //
            //Cancellation is excluded: closing the window is the ordinary way this ends, and it says
            //nothing about the token. Without that, every Esc would cost an exchange.
            if (!completed && !cancellationToken.IsCancellationRequested)
            {
                log.Debug("A Copilot request failed; dropping the cached token so the next one is fresh.");
                tokens.Invalidate();
            }
        }
    }

    private static void Authorise(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        CopilotHeaders.Identify(request);
    }

    /// <summary>
    /// Chat Completions: the text is <c>choices[0].delta.content</c>, and the last frame carries a
    /// <c>finish_reason</c> with an empty delta. <c>[DONE]</c> never reaches here — the endpoint
    /// recognises it, the same as for OpenAI.
    /// </summary>
    private string? Read(string frame)
    {
        try
        {
            CopilotEvent? parsed = JsonSerializer.Deserialize(frame, AiJson.Default.CopilotEvent);

            if (parsed?.Error is { } error)
                throw new AiUnavailableException(SecretDetector.Redact(error.Message ?? "Copilot returned an error."));

            //An empty `choices` is ordinary rather than a fault: the first frame of a Copilot stream
            //usually carries only content-filter results.
            return parsed?.Choices is [{ Delta.Content: { Length: > 0 } text }, ..] ? text : null;
        }
        catch (JsonException ex)
        {
            log.Debug($"Unparseable Copilot frame ignored: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Warms <b>two</b> things, where the other providers warm one.
    ///
    /// The handshake to the completion endpoint is what they all pay for here. Copilot also has the
    /// token exchange, and that is a second round trip -- measured at ~450 ms -- which would
    /// otherwise land inside the first generation's first-token budget, on top of the second this
    /// endpoint already takes to begin answering. Spending it at service start is exactly CLAUDE.md's
    /// "one cheap warm-up request at service start is fine", and <see cref="CopilotToken"/> then
    /// holds it until two minutes before it expires.
    ///
    /// A refused exchange is reported as an unreachable provider rather than thrown, so a stale
    /// stored token is named by `flick ai` at startup instead of failing the first commit of the day.
    /// </summary>
    public async Task<AiProbe> ProbeAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();

        try
        {
            await tokens.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AiUnavailableException ex)
        {
            return new AiProbe(false, clock.Elapsed, ex.Message);
        }

        AiProbe probe = await AiEndpoint.ProbeAsync(http, Endpoint, cancellationToken).ConfigureAwait(false);

        //The caller's number is "how long before this provider can answer", which is both round
        //trips rather than only the second one.
        return probe with { Elapsed = clock.Elapsed };
    }
}
