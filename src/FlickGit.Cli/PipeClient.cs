using System.IO.Pipes;
using System.Runtime.InteropServices;
using FlickGit.Ipc;

namespace FlickGit.Cli;

/// <summary>
/// Asks the resident service to do the work, or reports that it could not be reached.
///
/// Two things here are the whole point of the resident service existing:
///
/// <list type="number">
/// <item><description><b>A 250 ms budget, then give up.</b> The service is an optimisation, never a
/// dependency. A wedged one must cost the user a slower right-click, not a broken one — so every
/// failure path returns <see cref="PipeResult.Unavailable"/> and the caller launches the app
/// directly.</description></item>
/// <item><description><b>The foreground handover.</b> A background process cannot raise a window; a
/// process launched from user input can, and can lend that right away. This stub was started by a
/// click, so it calls <c>AllowSetForegroundWindow</c> on the resident's process before sending the
/// request, and does not exit until the response arrives. Without it the commit window opens
/// <i>behind</i> Explorer.</description></item>
/// </list>
/// </summary>
internal static partial class PipeClient
{
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(nint pipe, out uint processId);

    /// <summary>
    /// Sends <paramref name="args"/> to the resident service.
    /// </summary>
    /// <returns>
    /// The response, or <see cref="PipeResult.Unavailable"/> when there is no service to talk to —
    /// which is a normal outcome, not an error.
    /// </returns>
    public static PipeResult Send(string[] args, bool hasConsole)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                IpcProtocol.LocalPipeName(),
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            //The whole budget, spent on connecting. A pipe that exists answers immediately; one that
            //does not fails immediately. The timeout is for the case in between: a service that is
            //alive but stuck.
            pipe.Connect(IpcProtocol.ClientTimeoutMilliseconds);

            //Before the request, not after. The service calls SetForegroundWindow while handling it,
            //and by then this process must already have granted the right.
            GrantForegroundRights(pipe);

            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            IpcProtocol
                .WriteAsync(pipe, new IpcRequest(args, Environment.CurrentDirectory, hasConsole), IpcJson.Default.IpcRequest, deadline.Token)
                .GetAwaiter()
                .GetResult();

            //No short timeout on the read. The connect timeout is what protects against a dead
            //service; a request that has been accepted may legitimately take as long as the user
            //takes, because a verb can wait on a dialog.
            IpcResponse? response = IpcProtocol
                .ReadAsync(pipe, IpcJson.Default.IpcResponse, deadline.Token)
                .GetAwaiter()
                .GetResult();

            return response is null
                ? PipeResult.Unavailable
                : new PipeResult(true, response);
        }
        catch (TimeoutException)
        {
            //No service, or one too busy to answer inside the budget.
            return PipeResult.Unavailable;
        }
        catch (Exception)
        {
            //Anything at all -- a missing pipe, a closed one, a malformed frame. The fallback is
            //always available and always correct, so there is nothing here worth distinguishing.
            return PipeResult.Unavailable;
        }
    }

    /// <summary>
    /// Lends this process's foreground rights to whoever is on the other end of the pipe.
    ///
    /// The PID comes from <c>GetNamedPipeServerProcessId</c> rather than from the protocol: asking
    /// Windows who owns the handle cannot be spoofed by the message, and needs no handshake round
    /// trip inside the latency budget.
    /// </summary>
    private static void GrantForegroundRights(PipeStream pipe)
    {
        try
        {
            if (GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint serverPid))
                AllowSetForegroundWindow(serverPid);
        }
        catch (Exception)
        {
            //Worst case the window opens behind Explorer. Not a reason to fail the request.
        }
    }
}

/// <param name="Handled">True when the resident service answered.</param>
/// <param name="Response">The answer. Meaningless unless <paramref name="Handled"/>.</param>
internal readonly record struct PipeResult(bool Handled, IpcResponse? Response)
{
    public static PipeResult Unavailable => new(false, null);
}
