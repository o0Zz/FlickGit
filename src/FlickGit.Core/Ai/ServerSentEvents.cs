using System.Runtime.CompilerServices;
using System.Text;

namespace FlickGit.Ai;

/// <summary>
/// Reads a <c>text/event-stream</c> body and yields one string per event's data.
///
/// A parser, so it lives in Core beside the Git parsers and for the same reason: a wrong byte here
/// becomes a wrong commit message, which is the same failure mode as a wrong byte in
/// <c>--porcelain=v2 -z</c>. Both providers use SSE, so this is shared; what each one <i>means</i>
/// by an event is not, and stays in the generator.
///
/// Deliberately minimal. Only <c>data:</c> is read: <c>event:</c>, <c>id:</c> and <c>retry:</c> are
/// not used by either provider — both put the event type inside the JSON — and a field neither
/// sends is a field that cannot be tested.
/// </summary>
internal static class ServerSentEvents
{
    /// <summary>The sentinel OpenAI ends a stream with. Not JSON, so it must not reach a parser.</summary>
    public const string Done = "[DONE]";

    public static async IAsyncEnumerable<string> ReadAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);

        //One event can carry several data: lines, concatenated with newlines. Neither provider does
        //that today, but the format says so and honouring it costs one StringBuilder.
        var data = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                //A blank line ends the event.
                if (data.Length > 0)
                {
                    yield return data.ToString();
                    data.Clear();
                }

                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            //"data: {...}" and "data:{...}" are both legal.
            string payload = line[5..];

            if (payload.StartsWith(' '))
                payload = payload[1..];

            if (data.Length > 0)
                data.Append('\n');

            data.Append(payload);
        }

        //A stream that ended without a trailing blank line. Both providers send one, but a socket
        //that closed cleanly mid-event should not silently drop the last token.
        if (data.Length > 0)
            yield return data.ToString();
    }
}
