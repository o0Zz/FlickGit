using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FlickGit.Cli;

/// <summary>
/// The CLI stub. Native AOT, no Git logic, no UI.
///
/// Its entire job is to get out of the way fast: hand the verb to the resident FlickGit process
/// and exit, inside a 30 ms budget -- which is why there is no dependency in the csproj and no
/// framework startup to pay for.
///
/// It launches the app itself when there is no resident service: that is an optimisation, never a
/// dependency.
/// </summary>
internal static partial class Program
{
    private const int AttachParentProcess = -1;

    private const int ExitConfigurationError = 4;

    /// <summary>
    /// The verbs that open a window -- the only ones the stub does <b>not</b> wait for. Waiting would
    /// block the terminal until the user closed the commit window, and leave Explorer holding a
    /// process per right-click.
    ///
    /// The list is the window verbs rather than the text ones, so anything unrecognised falls into
    /// the waiting path and the user gets the help text and a real exit code instead of silence.
    /// </summary>
    private static readonly string[] WindowVerbs =
    [
        "commit", "pull-rebase", "log", "blame", "repo",
        "switch", "clone", "palette", "terminal", "tray",
    ];

    //`settings` is deliberately absent: it prints where the files are, and a verb whose whole output
    //is text has to be waited for or the console gets nothing.

    //`push` is deliberately absent from that list. It can refuse for safety -- a diverged branch is
    //exit code 5 -- and an exit code nobody can observe is not a contract.

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private const int StdOutputHandle = -11;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int handle);

    private static int Main(string[] args)
    {
        //Borrows the terminal's console when there is one, so this WinExe can still write to it. Run
        //from Explorer the call fails and every write below is a no-op, which is exactly right.
        if (AttachConsole(AttachParentProcess))
        {
            //The app writes UTF-8. A console left on the ANSI code page renders the help text as mojibake,
            //and would mangle any non-ASCII branch name or repository path that came back with it.
            try
            {
                Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            }
            catch (IOException)
            {
                //A console that refuses the code page change is still worth writing to.
            }
        }

        string? appPath = ResolveAppPath();

        if (appPath is null)
        {
            //Named explicitly rather than "something went wrong": the only way this happens is a broken
            //install, and the fix is knowing which file is missing.
            WriteError("FlickGit.exe was not found beside flick.exe. The installation is incomplete.");
            return ExitConfigurationError;
        }

        //The fast path: hand the whole command line to the resident service, print whatever it says, and
        //exit. `tray` is excluded -- it *is* the resident service, so forwarding it to an existing one
        //would be asking it to start itself.
        if (args.Length > 0 && !args[0].Equals("tray", StringComparison.OrdinalIgnoreCase))
        {
            //Whether this process can print is something only it knows, so it is part of the request: the
            //resident has no console and would otherwise answer a right-click as if it were a terminal.
            PipeResult forwarded = PipeClient.Send(args, hasConsole: HasConsole());

            if (forwarded is { Handled: true, Response: { } response })
            {
                Write(Console.Out, response.Output);
                Write(Console.Error, response.Error);
                return response.ExitCode;
            }
        }

        //No resident service, or it did not answer inside the budget.
        bool waitForExit = args.Length > 0
                           && !WindowVerbs.Contains(args[0], StringComparer.OrdinalIgnoreCase);

        //`switch <path> <branch>` is a text command even though bare `switch` opens a picker: it can
        //refuse for safety, and an exit code nobody can observe is not a contract.
        if (!waitForExit
            && args.Length >= 3
            && args[0].Equals("switch", StringComparison.OrdinalIgnoreCase))
        {
            waitForExit = true;
        }

        return Launch(appPath, args, waitForExit);
    }

    /// <summary>
    /// Starts the resident app with the same arguments.
    ///
    /// On the waiting path the child's stdout and stderr are redirected and copied through. That copy
    /// is not redundant: .NET only asks CreateProcess to inherit handles when at least one stream is
    /// redirected, so a child started with none inherits nothing -- and `flick status &gt; out.txt`
    /// would write to a handle the app cannot see, producing an empty file and no error.
    /// </summary>
    private static int Launch(string appPath, string[] args, bool waitForExit)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = appPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
            RedirectStandardOutput = waitForExit,
            RedirectStandardError = waitForExit,

            //The app writes UTF-8. Without this the pipe is decoded with the console's code page and every
            //non-ASCII character arrives mangled -- including in a repository path.
            StandardOutputEncoding = waitForExit ? Utf8NoBom : null,
            StandardErrorEncoding = waitForExit ? Utf8NoBom : null,
        };

        //ArgumentList, never a joined string. A repository path with a space or a quote would otherwise
        //be re-split by the child's argv parser -- and this stub exists precisely to pass a path that
        //came from Explorer's %V.
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        try
        {
            using Process? process = Process.Start(startInfo);

            if (process is null)
            {
                WriteError($"Could not start:\n{appPath}");
                return ExitConfigurationError;
            }

            if (!waitForExit)
            {
                //The fast path. The child was started by a process holding foreground rights, so it inherits
                //them and its window comes up in front of Explorer. The pipe path does need
                //AllowSetForegroundWindow, which is why PipeClient grants it there.
                return 0;
            }

            //Both pipes drained concurrently, then the exit awaited. Draining one and then the other would
            //deadlock the moment the un-drained pipe filled.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();

            process.WaitForExit();

            Write(Console.Out, stdout.GetAwaiter().GetResult());
            Write(Console.Error, stderr.GetAwaiter().GetResult());

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            WriteError($"Could not start FlickGit:\n{ex.Message}");
            return ExitConfigurationError;
        }
    }

    /// <summary>
    /// FlickGit.exe, beside this executable. Resolved from the real module path rather than the
    /// working directory: Explorer sets that to the clicked folder, so anything relative would look
    /// for the app inside the user's repository.
    /// </summary>
    private static string? ResolveAppPath()
    {
        string? directory = Path.GetDirectoryName(Environment.ProcessPath ?? AppContext.BaseDirectory);
        if (directory is null)
            return null;

        string candidate = Path.Combine(directory, "FlickGit.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static void Write(TextWriter writer, string text)
    {
        if (text.Length == 0)
            return;

        try
        {
            writer.Write(text);
            writer.Flush();
        }
        catch (IOException)
        {
            //Launched from Explorer, so there is no console and no redirection.
        }
    }

    /// <summary>
    /// Whether anything will see a write: a borrowed console, or a redirected standard handle. The
    /// answer travels with the request.
    /// </summary>
    private static bool HasConsole()
    {
        try
        {
            nint handle = GetStdHandle(StdOutputHandle);
            return handle != 0 && handle != -1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void WriteError(string message)
    {
        try
        {
            Console.Error.WriteLine(message);
            Console.Error.Flush();
        }
        catch (IOException)
        {
            //No console attached. There is no window to show it in either -- that is the app's job, and the
            //app is what could not be started.
        }
    }
}
