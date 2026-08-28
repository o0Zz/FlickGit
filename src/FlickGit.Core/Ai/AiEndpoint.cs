using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using FlickGit.Secrets;

namespace FlickGit.Ai;

/// <summary>
/// Everything the providers do identically: make the streaming request, warm the connection, and
/// explain a refusal. What differs -- the URL, the headers, the request shape and which frame
/// carries text -- arrives as arguments, which is why there is still no base class.
/// </summary>
/// <summary>
/// How a streamed body is cut into frames. Two, because two exist: the hosted three send
/// <c>text/event-stream</c> and Ollama's native API sends one JSON object per line.
/// </summary>
internal enum AiFraming
{
    ServerSentEvents,
    LineDelimitedJson,
}

internal static class AiEndpoint
{
    /// <summary>
    /// Posts <paramref name="json"/> and yields the text out of each frame that carries any.
    ///
    /// Four details are load-bearing:
    ///
    /// <list type="bullet">
    /// <item><description><b><c>ResponseHeadersRead</c>.</b> The default buffers the whole response
    /// before returning, so the first token would arrive with the last and the streaming this feature
    /// exists for would silently not happen.</description></item>
    /// <item><description><b>The timeout lives here</b>, on a linked source, so no caller can forget
    /// it. <b>It measures silence, not total duration</b> -- every frame restarts it. Covering the
    /// whole stream made it a guillotine on the generation rather than a guard against one that had
    /// stopped answering: a long message was cut off mid-word at exactly eight seconds, however
    /// healthily it was arriving.</description></item>
    /// <item><description><b>A timeout is told apart from a cancellation</b> by asking whether the
    /// caller's own token fired. Conflating them reports "the provider was slow" when the user simply
    /// pressed Esc.</description></item>
    /// <item><description><b>Every message is redacted.</b> An exception from an API that echoes the
    /// request back can otherwise carry the key into a log.</description></item>
    /// </list>
    /// </summary>
    /// <param name="authorise">Adds the provider's key header. The one thing that must not be logged.</param>
    /// <param name="silence">
    /// How long the provider may say nothing before it is treated as gone. An argument rather than a
    /// constant, because a local model's first silence is a multi-gigabyte read off disk and a hosted
    /// one's is a fault.
    /// </param>
    /// <param name="readFrame">Pulls the text out of one frame, or returns null for a frame to ignore.</param>
    public static async IAsyncEnumerable<string> StreamAsync(
        HttpClient http,
        string provider,
        string endpoint,
        string json,
        Action<HttpRequestMessage> authorise,
        AiFraming framing,
        TimeSpan silence,
        Func<string, string?> readFrame,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(silence);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            //Serialised by the caller, never interpolated. A diff is full of quotes, backslashes and newlines.
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        authorise(request);

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            framing == AiFraming.LineDelimitedJson ? "application/x-ndjson" : "text/event-stream"));

        HttpResponseMessage response;

        try
        {
            response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiUnavailableException(
                $"{provider} did not answer within {silence.TotalSeconds:F0} s.");
        }
        catch (HttpRequestException ex)
        {
            throw new AiUnavailableException(SecretDetector.Redact(ex.Message));
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                //The user's token, not the silence deadline. The response has arrived, so there is no
                //silence left to guard -- and a deadline that fired while the error body was being read
                //would throw OperationCanceledException out of here, which is indistinguishable from the
                //user pressing Esc: no notice, and nothing added to the failure counter. A rate-limited
                //provider is the case, because 429 is exactly what arrives late.
                throw new AiUnavailableException(
                    await DescribeAsync(provider, response, cancellationToken).ConfigureAwait(false));
            }

            //The headers arrived, so the budget starts again for the body -- the same restart every
            //frame does below. It measures silence, not total duration, and a provider that spent most
            //of it on time-to-first-byte would otherwise have almost none left for its first frame.
            deadline.CancelAfter(silence);

            Stream body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);

            //Enumerated by hand rather than with `await foreach`, because C# forbids a catch around a
            //`yield return` and telling a stall apart from an Esc is the whole point below.
            IAsyncEnumerable<string> source = framing == AiFraming.LineDelimitedJson
                ? LineDelimitedJson.ReadAsync(body, deadline.Token)
                : ServerSentEvents.ReadAsync(body, deadline.Token);

            await using IAsyncEnumerator<string> frames = source.GetAsyncEnumerator(deadline.Token);

            while (true)
            {
                string frame;

                try
                {
                    if (!await frames.MoveNextAsync().ConfigureAwait(false))
                        break;

                    frame = frames.Current;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    //A stall, not a cancel. Uncaught, this reached the caller as an OperationCanceledException
                    //indistinguishable from the user pressing Esc -- so a provider that went quiet truncated the
                    //message in silence, with no notice and no failure counted.
                    throw new AiUnavailableException(
                        $"{provider} stopped sending after {silence.TotalSeconds:F0} s of silence. "
                        + "The message above is unfinished.");
                }

                //A frame arrived, so the request is not hung and the clock starts again.
                deadline.CancelAfter(silence);

                //The one sentinel that is not JSON. OpenAI sends it and Anthropic does not; handing it to a
                //parser would be a spurious warning on every request that gets one.
                if (frame == ServerSentEvents.Done)
                    break;

                if (readFrame(frame) is { Length: > 0 } text)
                    yield return text;
            }
        }
    }

    /// <summary>
    /// One cheap request, purely to pay the TLS and HTTP/2 handshake. A <c>HEAD</c> with no key and no
    /// body: the answer is irrelevant, and a 401 or a 405 is a reachable provider. A cold handshake
    /// costs 100-300 ms, a third of the first-token budget.
    /// </summary>
    public static async Task<AiProbe> ProbeAsync(HttpClient http, string endpoint, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, endpoint);
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            return new AiProbe(true, clock.Elapsed, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return new AiProbe(false, clock.Elapsed, SecretDetector.Redact(ex.Message));
        }
    }

    /// <summary>
    /// Turns a failed response into a sentence the user can act on. A provider's raw JSON error body
    /// is not a one-line notice -- it is the thing a user pastes into a search engine because the tool
    /// would not tell them what to do, and for an API that echoes requests back it can add a leaked
    /// key.
    /// </summary>
    public static async Task<string> DescribeAsync(
        string provider,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        //Read only for the cases below that quote a fragment of it.
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                $"{provider} rejected the API key. Store a new one with `flick ai key set`.",

            HttpStatusCode.TooManyRequests =>
                $"{provider} is rate limiting this key. Try again in a moment.",

            HttpStatusCode.NotFound =>
                $"{provider} does not know that model. Check `aiModel` in settings.json.",

            //The one case where a fragment of the body earns its place: a 400 is almost always a model name
            //or a parameter this build got wrong, and the provider says which.
            HttpStatusCode.BadRequest => $"{provider} refused the request. {Summarise(body)}",

            >= HttpStatusCode.InternalServerError =>
                $"{provider} is having trouble ({(int)response.StatusCode}). Try again in a moment.",

            _ => $"{provider} returned {(int)response.StatusCode}. {Summarise(body)}".TrimEnd(),
        };
    }

    /// <summary>
    /// A short, redacted fragment of a response body. Redacted first and truncated second: a key
    /// echoed back must not survive by being past the cut, or before it.
    /// </summary>
    private static string Summarise(string body)
    {
        string clean = SecretDetector.Redact(body).Replace('\n', ' ').Replace('\r', ' ').Trim();

        return clean.Length <= 160 ? clean : clean[..160] + "…";
    }
}
