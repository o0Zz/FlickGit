using System.Net;
using System.Net.Sockets;
using FlickGit.Ipc;
using FlickGit.Logging;

namespace FlickGit.App.Mac;

/// <summary>
/// The Unix socket the resident service listens on, and the client that talks to it.
///
/// <b>Why not a named pipe.</b> .NET maps <c>NamedPipeServerStream</c> onto a Unix socket off
/// Windows, so the framing would have worked untouched — but <c>PipeSecurity</c> is Windows-only and
/// silently absent there, leaving the socket's permissions to the process umask, and
/// <c>WaitForPipeDrain</c> throws outright. CLAUDE.md is explicit that this endpoint is a security
/// boundary because a message on it becomes a process start, so it is built from a socket where the
/// directory mode, the file mode and the peer's user id are all things this code sets and checks.
///
/// <b>Three guards, deliberately overlapping.</b> The directory is created <c>0700</c> before the
/// socket exists, so nobody else can reach the path at all; the socket file is set <c>0600</c> after
/// <c>bind</c>, because bind creates it with the umask and connect permission on the file is honoured
/// on macOS; and every accepted connection is asked who it belongs to, which is the only one of the
/// three that still holds if the first two were somehow wrong.
/// </summary>
public sealed class LocalEndpoint(ILog log)
{
    /// <summary>
    /// Serves requests until the token is cancelled.
    ///
    /// One connection at a time, matching the Windows pipe's <c>maxNumberOfServerInstances: 1</c>:
    /// a request either answers in text or opens a window, and two at once would race for the
    /// foreground. The backlog absorbs the burst instead.
    /// </summary>
    public async Task ServeAsync(
        Func<IpcRequest, Task<IpcResponse>> handler,
        CancellationToken cancellationToken)
    {
        string directory = IpcProtocol.LocalSocketDirectory();
        string path = IpcProtocol.LocalSocketPath();

        Directory.CreateDirectory(directory);
        Restrict(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        //A socket file left behind by a killed process makes bind fail with EADDRINUSE, and there is
        //nothing to inherit from it: the listener is gone with the process that owned it. Deleting a
        //live service's socket is not a risk worth guarding, because the single-instance check has
        //already refused to start a second one.
        if (File.Exists(path))
            File.Delete(path);

        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        listener.Bind(new UnixDomainSocketEndPoint(path));
        Restrict(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(backlog: 16);

        log.Info($"Listening on {path}");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket accepted;

                try
                {
                    accepted = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                using (accepted)
                {
                    await HandleAsync(accepted, handler, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            //The socket file outlives the socket, so it is removed on the way out rather than left
            //for the next start to clean up.
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Sends one request, or reports that nobody is listening.
    ///
    /// Returns null on every failure rather than throwing, because the caller's answer to all of
    /// them is the same and is not an error: run the verb here instead. CLAUDE.md — the resident
    /// service is an optimisation, never a dependency.
    /// </summary>
    public static async Task<IpcResponse?> SendAsync(
        IpcRequest request,
        CancellationToken cancellationToken)
    {
        string path = IpcProtocol.LocalSocketPath();

        if (!File.Exists(path))
            return null;

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(IpcProtocol.ClientTimeoutMilliseconds);

            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), connect.Token).ConfigureAwait(false);

            await using var stream = new NetworkStream(socket, ownsSocket: false);

            await IpcProtocol.WriteAsync(stream, request, IpcJson.Default.IpcRequest, cancellationToken)
                .ConfigureAwait(false);

            return await IpcProtocol.ReadAsync(stream, IpcJson.Default.IpcResponse, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            //Wedged, half-started, or gone since File.Exists said otherwise.
            return null;
        }
    }

    private async Task HandleAsync(
        Socket accepted,
        Func<IpcRequest, Task<IpcResponse>> handler,
        CancellationToken cancellationToken)
    {
        if (!IsSameUser(accepted))
            return;

        await using var stream = new NetworkStream(accepted, ownsSocket: false);

        IpcRequest? request = await IpcProtocol
            .ReadAsync(stream, IpcJson.Default.IpcRequest, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            log.Debug("A client connected without sending a request.");
            return;
        }

        IpcResponse response = await handler(request).ConfigureAwait(false);

        await IpcProtocol
            .WriteAsync(stream, response, IpcJson.Default.IpcResponse, cancellationToken)
            .ConfigureAwait(false);

        //The Windows server calls WaitForPipeDrain here, which throws off Windows and has no
        //equivalent. Shutting the write side down is the honest substitute: it flushes what is
        //queued and gives the peer an end-of-stream, so the client has the whole response before
        //this returns and closes.
        try
        {
            accepted.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
            //The client left first. Its problem, and it already has the bytes or it does not.
        }
    }

    /// <summary>
    /// Whether the peer is this same user. A refusal is logged and the connection dropped without a
    /// reply: there is nothing to negotiate, and answering would confirm the endpoint exists.
    /// </summary>
    private bool IsSameUser(Socket accepted)
    {
        if (!OperatingSystem.IsMacOS())
        {
            //Windows has AF_UNIX, which is what lets this transport be exercised on a development
            //machine, but no getpeereid to ask. The Windows product does not use this endpoint --
            //it has the pipe and a real ACL -- so this is a test path rather than a shipping one,
            //and it says so instead of pretending to have checked.
            log.Debug("Peer identity not checked: getpeereid is macOS-only.");
            return true;
        }

        if (Libc.getpeereid((int)accepted.Handle, out uint peer, out _) != 0)
        {
            log.Warn("Refused a client whose user id could not be read.");
            return false;
        }

        if (peer == Libc.getuid())
            return true;

        log.Warn($"Refused a client belonging to uid {peer}.");
        return false;
    }

    /// <summary>
    /// Sets the permission bits, where the platform has any.
    ///
    /// A no-op on Windows, which is the only reason this is a method: the mode is the point of the
    /// design off Windows, and silently skipping it there would make the development machine look
    /// like it had verified something it cannot.
    /// </summary>
    private static void Restrict(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return;

        File.SetUnixFileMode(path, mode);
    }
}
