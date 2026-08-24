using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using FlickGit.Ipc;
using FlickGit.Logging;

namespace FlickGit.App.Resident;

/// <summary>
/// The resident service's request listener.
///
/// One connection at a time, one request per connection. That is not a simplification: a right-click
/// produces exactly one request, and a server that accepted several in flight would need to decide
/// what "two commit windows for the same repository" means.
///
/// <b>The ACL is the security boundary.</b> CLAUDE.md: "<c>PipeSecurity</c> grants access to the
/// current user SID only. This pipe can trigger process execution through user-defined actions; a
/// world-readable pipe would be a local privilege escalation vector." Everything else here is
/// plumbing; that part is not.
/// </summary>
public sealed class PipeServer(ILog log) : IDisposable
{
    private readonly CancellationTokenSource _stopping = new();

    private Task? _loop;
    private bool _disposed;

    private Func<IpcRequest, Task<IpcResponse>>? _handler;

    /// <summary>The pipe this process is listening on, for diagnostics.</summary>
    public string PipeName { get; } = IpcProtocol.LocalPipeName();

    /// <summary>
    /// Starts listening. Failure is logged and swallowed: a resident service that cannot open its
    /// pipe is still a working tray icon, and every CLI invocation falls back to a direct launch.
    /// </summary>
    /// <param name="handler">
    /// Handles one request and returns what the client should print and exit with. Invoked on the UI
    /// thread, because most verbs open windows.
    ///
    /// A parameter of <c>Start</c> rather than a settable property, because there is no state in
    /// which listening without a handler is wanted: the two were always assigned on consecutive
    /// lines, and only the order made it correct.
    /// </param>
    public void Start(Func<IpcRequest, Task<IpcResponse>> handler)
    {
        _handler = handler;
        _loop = Task.Run(() => ListenAsync(_stopping.Token));
        log.Info($"Listening on {IpcProtocol.LocalPipePath()}");
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;

            //Creating the pipe and serving a connection fail for entirely different reasons, and
            //the difference decides whether to carry on. Failing to *create* it means the name is
            //taken or the ACL was refused, and neither is something the next attempt can fix --
            //retrying spins a core and writes five log lines a second for as long as the process
            //lives.
            try
            {
                //A fresh server instance per connection. Reusing one across connections means a
                //client that dies mid-request leaves the pipe in a state the next one inherits.
                pipe = Create();
            }
            catch (Exception ex)
            {
                log.Warn(
                    $"Could not open {IpcProtocol.LocalPipePath()}: {ex.Message}. " +
                    "Every flick.exe call will launch the app directly instead.");

                return;
            }

            try
            {
                using (pipe)
                {
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    await ServeAsync(pipe, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                //One bad connection must not end the listener: the next right-click has to work.
                log.Warn($"Pipe request failed: {ex.Message}");
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        IpcRequest? request = await IpcProtocol
            .ReadAsync(pipe, IpcJson.Default.IpcRequest, cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            log.Debug("Pipe client disconnected without sending a request.");
            return;
        }

        IpcResponse response = _handler is null
            ? new IpcResponse(1, string.Empty, "The resident service is not ready.")
            : await _handler(request).ConfigureAwait(false);

        await IpcProtocol
            .WriteAsync(pipe, response, IpcJson.Default.IpcResponse, cancellationToken)
            .ConfigureAwait(false);

        //Flushed and drained before the connection drops, so the client is guaranteed to have the
        //response before it exits -- which is also what keeps its foreground rights alive until the
        //window has been activated.
        pipe.WaitForPipeDrain();
    }

    /// <summary>
    /// Creates the pipe with an ACL naming only the current user.
    /// </summary>
    private NamedPipeServerStream Create()
    {
        var security = new PipeSecurity();

        //The current user, and nobody else. Not Administrators, not SYSTEM, not Everyone: a
        //message on this pipe becomes a process start, so anyone who can write to it can run code
        //as this user.
        var self = new SecurityIdentifier(WindowsIdentity.GetCurrent().User!.Value);

        security.AddAccessRule(new PipeAccessRule(
            self,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,

            //One at a time. See the class remarks.
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    /// <summary>
    /// Stops listening. Safe to call twice.
    ///
    /// Twice is the normal case, not a defensive flourish: the composition root closes the pipe
    /// first so the listener stops before anything it uses goes away, and then disposes the
    /// container, which disposes this singleton again. Without the guard the second call cancels an
    /// already-disposed <see cref="CancellationTokenSource"/> and throws on the way out of the
    /// process the user just asked to quit.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _stopping.Cancel();

        try
        {
            //Bounded: a listener blocked on WaitForConnectionAsync is cancelled by the token, and
            //waiting forever on shutdown would hang the process the user just asked to quit.
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            //Shutting down; the OS reclaims the pipe either way.
        }

        _stopping.Dispose();
    }
}
