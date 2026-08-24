using System.Runtime.CompilerServices;
using System.Text;

namespace FlickGit.Ai;

/// <summary>
/// Reads an <c>application/x-ndjson</c> body and yields one string per line.
///
/// The second framing in the product, beside <see cref="ServerSentEvents"/>, and it exists because
/// Ollama's native API does not speak the first: every chunk is a complete JSON object on its own
/// line, with no <c>data:</c> prefix, no blank-line separator and no <c>[DONE]</c> sentinel.
///
/// Ollama does also expose an OpenAI-compatible endpoint that speaks SSE, and using it would have
/// avoided this file. It was not taken: that endpoint is a translation layer over the native one,
/// it cannot carry <c>options.num_predict</c> or <c>keep_alive</c> — which are how the output guard
/// and the model preload are expressed — and a shim is a second thing that can be wrong between us
/// and the model. Twenty lines is a cheaper price than either.
///
/// A parser, so it lives in Core beside the Git parsers and for the same reason: a wrong byte here
/// becomes a wrong commit message.
/// </summary>
internal static class LineDelimitedJson
{
    public static async IAsyncEnumerable<string> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        //StreamReader, rather than splitting the bytes by hand, because a token can be split across
        //two socket reads in the middle of a UTF-8 sequence -- and an emoji in a commit message
        //arriving as two halves of a code point is exactly the kind of bug that only shows up
        //against a real model.
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            //A blank line is not an event boundary here, as it is in SSE -- it is nothing at all.
            if (line.Trim() is { Length: > 0 } payload)
                yield return payload;
        }
    }
}
