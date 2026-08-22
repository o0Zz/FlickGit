using System.IO;
using System.Runtime.InteropServices;

namespace FlickGit.App.Infrastructure;

/// <summary>
/// Lets this WinExe write text to whatever is listening, when anything is.
///
/// FlickGit.exe is a <c>WinExe</c> on purpose: a console-subsystem app flashes a black window
/// every time Explorer runs it from a context menu, which is exactly the experience the whole
/// product is trying to avoid. But `flick status` and `flick diag timings` are text commands,
/// so the output has to reach the caller somehow.
///
/// There are two ways it can, and both have to be handled:
///
/// <list type="bullet">
/// <item><description><b>A console to borrow.</b> <c>AttachConsole(ATTACH_PARENT_PROCESS)</c>
/// takes the terminal's console when the parent has one — which is the case when the CLI stub
/// was run from a shell.</description></item>
/// <item><description><b>An already-valid standard handle.</b> When the launcher redirected
/// stdout to a pipe or a file — `flick status &gt; out.txt`, or a script capturing the
/// output — the handle is inherited and usable even though there is no console at all, and
/// <c>AttachConsole</c> fails. Gating writes on AttachConsole alone silently discards
/// everything in that case.</description></item>
/// </list>
///
/// Run from Explorer neither holds, every write becomes a no-op, and callers fall back to a
/// window instead.
///
/// <b>Why this is still a static.</b> Hard Requirement 3 turns behaviour-bearing statics into
/// injected services, and this one writes to a handle. It is the named exception: the thinnest
/// possible wrapper over the process's own console, which <c>System.Console</c> already exposes as a
/// static and of which there is exactly one, forever. There is nothing to substitute, and
/// <see cref="CommandLine.VerbOutput"/> is already the seam that decides whether text goes here, to
/// a buffer, or to a window.
/// </summary>
public static partial class ConsoleOutput
{
    private const int AttachParentProcess = -1;
    private const int StdOutputHandle = -11;

    private static bool _usable;
    private static bool _attempted;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int handle);

    /// <summary>True when a write would reach somebody, so a caller can choose a window instead.</summary>
    public static bool IsAvailable
    {
        get
        {
            Attach();
            return _usable;
        }
    }

    public static void WriteLine(string text = "")
    {
        if (!IsAvailable)
            return;

        try
        {
            Console.Out.WriteLine(text);
            Console.Out.Flush();
        }
        catch (IOException)
        {
            //The parent exited and took the handle with it. Nothing to recover, and nothing
            //worth failing an operation over.
        }
    }

    public static void WriteError(string text)
    {
        if (!IsAvailable)
            return;

        try
        {
            Console.Error.WriteLine(text);
            Console.Error.Flush();
        }
        catch (IOException)
        {
        }
    }

    private static void Attach()
    {
        if (_attempted)
            return;

        _attempted = true;

        //Borrow the parent's console if it has one. Attempted first: when it succeeds, it is
        //also what populates this process's standard handles.
        bool attached = AttachConsole(AttachParentProcess);

        _usable = attached || HasRealStandardOutput();
    }

    /// <summary>
    /// Whether stdout is a handle something is actually reading.
    ///
    /// A process started with no console and no redirection gets NULL here; a redirected one
    /// gets a pipe or file handle. INVALID_HANDLE_VALUE is checked too, because that is what
    /// GetStdHandle returns on failure rather than NULL.
    /// </summary>
    private static bool HasRealStandardOutput()
    {
        IntPtr handle = GetStdHandle(StdOutputHandle);
        return handle != IntPtr.Zero && handle != new IntPtr(-1);
    }
}
