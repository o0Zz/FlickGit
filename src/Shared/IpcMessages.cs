using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace FlickGit.Ipc;

/// <summary>
/// The named-pipe protocol between <c>flick.exe</c> and the resident <c>FlickGit.exe</c>.
///
/// This file is <b>compiled into both projects</b> rather than shared through an assembly. The CLI
/// stub deliberately has no reference to anything — CLAUDE.md: "No project reference to
/// FlickGit.Core… If this file ever needs one, that is a sign the logic has drifted into the wrong
/// process." But two hand-maintained copies of a wire format is exactly the thing that drifts, so
/// the source is shared and each assembly compiles its own.
///
/// Framing is a 4-byte little-endian length followed by UTF-8 JSON. One request, one response, then
/// the connection closes: there is no session, no ordering to get wrong, and nothing to resynchronise
/// after a malformed message.
/// </summary>
public static class IpcProtocol
{
    /// <summary>
    /// The pipe name: per user, per session.
    ///
    /// Both parts matter. Per user because the pipe can trigger process execution, so two accounts
    /// must never share one. Per session because a second logged-on session is a different desktop —
    /// activating a window from another session would put it somewhere nobody is looking.
    /// </summary>
    public static string LocalPipeName()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? "unknown";
        int session = Process.GetCurrentProcess().SessionId;

        return $"flickgit.{sid}.{session}";
    }

    /// <summary>The same name with the <c>\\.\pipe\</c> prefix, for logging and for an existence check.</summary>
    public static string LocalPipePath() => $@"\\.\pipe\{LocalPipeName()}";

    /// <summary>
    /// How long the client waits for the pipe before giving up and launching the app itself.
    ///
    /// CLAUDE.md: 250 ms. The resident service is an optimisation, never a dependency — a service
    /// that is wedged must cost the user a slower right-click, not a broken one.
    /// </summary>
    public const int ClientTimeoutMilliseconds = 250;

    /// <summary>
    /// Refuses a frame larger than this.
    ///
    /// The peer is trusted (same user, same session), so this is not a security boundary — it is a
    /// guard against a bug on either side turning into a multi-gigabyte allocation.
    /// </summary>
    private const int MaxFrameBytes = 1024 * 1024;

    public static async Task WriteAsync<T>(
        Stream stream,
        T message,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);

        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one frame, or returns null when the peer closed without sending one.
    /// </summary>
    public static async Task<T?> ReadAsync<T>(
        Stream stream,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        byte[] header = new byte[4];

        if (!await FillAsync(stream, header, cancellationToken).ConfigureAwait(false))
            return null;

        int length = BinaryPrimitives.ReadInt32LittleEndian(header);

        if (length <= 0 || length > MaxFrameBytes)
            return null;

        byte[] payload = new byte[length];

        if (!await FillAsync(stream, payload, cancellationToken).ConfigureAwait(false))
            return null;

        try
        {
            return JsonSerializer.Deserialize(payload, typeInfo);
        }
        catch (JsonException)
        {
            //A malformed frame is not worth recovering from: the connection carries one message and
            //is about to close anyway.
            return null;
        }
    }

    /// <summary>
    /// Reads until the buffer is full.
    ///
    /// A pipe read can return fewer bytes than asked for, so a single ReadAsync is not enough — and
    /// treating a short read as a complete message is how a length prefix ends up parsed as payload.
    /// </summary>
    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;

        while (offset < buffer.Length)
        {
            int read = await stream
                .ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
                return false;

            offset += read;
        }

        return true;
    }
}

/// <summary>
/// A command line, forwarded verbatim.
/// </summary>
/// <param name="Arguments">
/// Exactly what the stub was given. The resident parses it with the same parser the stub's fallback
/// launch would have used, so the two paths cannot disagree about what a verb means.
/// </param>
/// <param name="WorkingDirectory">
/// The stub's working directory, because <c>&lt;path&gt;</c> defaults to it — and the resident's own
/// working directory is wherever it was started at logon.
/// </param>
/// <param name="HasConsole">
/// Whether the <i>stub</i> has somewhere to print. Only it can know: the resident service has no
/// console of its own, and without this it could not tell a terminal invocation from a right-click
/// and would answer both the same way.
/// </param>
public sealed record IpcRequest(string[] Arguments, string WorkingDirectory, bool HasConsole);

/// <param name="ExitCode">What the stub should exit with.</param>
/// <param name="Output">Text for stdout. Empty for a verb that opened a window.</param>
/// <param name="Error">Text for stderr.</param>
public sealed record IpcResponse(int ExitCode, string Output, string Error);

/// <summary>
/// Source-generated serialisation, so the stub stays Native AOT and pays no reflection at startup.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
internal sealed partial class IpcJson : JsonSerializerContext;
