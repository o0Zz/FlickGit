using System.Diagnostics;
using System.Runtime.InteropServices;
using FlickGit.App.Localization;
using FlickGit.Logging;

namespace FlickGit.App.Terminal;

/// <summary>
/// One PowerShell, running in a real console window that has been reparented into a WPF pane.
///
/// <b>Why a reparented console rather than a terminal emulator.</b> The pane exists so the user can run
/// <c>claude</c>, <c>vim</c> or <c>less</c> without leaving the commit window, and those are full-screen
/// TUIs: alternate screen buffer, cursor addressing, 24-bit colour, bracketed paste. A pane that piped a
/// shell's redirected stdout into a text box could not host them -- with redirected stdio the shell is
/// not attached to a console at all. The two mechanisms that can are ConPTY, where we would own a VT
/// parser and a cell renderer forever, and this: let Windows draw a real console and put it inside our
/// window. This is the second, and everything awkward below is the price of not writing the first.
///
/// <b>Three facts were measured on Windows 11 build 26200 rather than assumed, and each one is load
/// bearing.</b>
///
/// <b>1. The launch must name conhost explicitly.</b> The default terminal on Windows 11 is Windows
/// Terminal, so an ordinary <c>powershell.exe</c> hands its console off and the window that appears
/// belongs to a <c>WindowsTerminal.exe</c> holding the user's other tabs. Reparenting that would drag
/// their terminals into the commit window. <c>conhost.exe powershell.exe</c> launched as a command
/// parses a normal command line and creates its own server handle, so it never reaches the handoff.
/// Measured: the bypass yields <c>ConsoleWindowClass</c>; a bare shell opens a Windows Terminal tab.
///
/// <b>2. The window cannot be found by process id.</b> Its owning pid is sometimes conhost's and
/// sometimes the shell's, so no equality test works -- and <c>Win32_Process.ParentProcessId</c> must not
/// be used to walk to it either, being unvalidated: a reused pid pointed the first version of this at an
/// unrelated console window belonging to something else entirely. The authoritative question is
/// <see cref="IsProcessInJob"/>, which is why the job object below is the <i>discovery</i> mechanism
/// and not only the lifetime one.
///
/// <b>3. Therefore the process starts suspended.</b> A shell that spawned before
/// <c>AssignProcessToJobObject</c> would be outside the job, which makes it both unkillable by closing
/// the job and invisible to the finder. <c>CREATE_SUSPENDED</c> closes that window; nothing runs until
/// the job owns it.
///
/// <b>The job object is also the only honest lifetime guarantee.</b> <c>FlickGit.Setup</c> runs
/// <c>taskkill /F /IM FlickGit.exe</c> on every install and upgrade, so no orderly shutdown path can be
/// relied on. <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> makes the kernel do it: the handle closes when
/// this process dies however it dies, and the shell dies with it. Without that, an invisible
/// <c>powershell.exe</c> would be left holding a directory handle on the user's repository, which is
/// enough to make a later <c>git switch</c> fail for reasons nobody could see.
/// </summary>
internal sealed partial class ConsoleSession(ILog log)
{
    /// <summary>
    /// The shell, as a constant rather than a setting -- Hard Requirement 2, and nobody has asked for a
    /// second one yet. Deliberately not <c>wt.exe</c>, which <c>flick terminal</c> prefers: Windows
    /// Terminal is the one thing here that cannot be reparented.
    /// </summary>
    private const string ShellCommandLine = "conhost.exe powershell.exe";

    /// <summary>Classic conhost. Windows Terminal's top-level class is CASCADIA_HOSTING_WINDOW_CLASS.</summary>
    private const string ConsoleWindowClass = "ConsoleWindowClass";

    /// <summary>How long to wait for conhost to create its window. Measured at 188 ms; this is slack.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);

    private nint _job;
    private nint _process;
    private nint _thread;

    /// <summary>The console host, so its threads can be enumerated. 0 when nothing is running.</summary>
    private int _hostProcessId;

    /// <summary>
    /// The thread that really owns the console window, once <see cref="Focus"/> has found it, or 0.
    /// Not what <c>GetWindowThreadProcessId</c> answers -- see <see cref="Focus"/>.
    /// </summary>
    private uint _windowThread;

    /// <summary><see cref="_windowThread"/> while our input queue is joined to it, or 0.</summary>
    private uint _attachedThread;

    /// <summary>The reparented console window, or 0 when nothing is running.</summary>
    private nint WindowHandle { get; set; }

    public bool IsRunning => WindowHandle != 0 && IsWindow(WindowHandle);

    /// <summary>
    /// Starts a shell in <paramref name="workingDirectory"/> and parents its window into
    /// <paramref name="container"/>.
    /// </summary>
    /// <returns>Null on success, or a sentence saying what went wrong.</returns>
    public async Task<string?> StartAsync(
        nint container,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (container == 0)
            return Strings.Get("console.failed");

        Stop();

        _job = CreateKillOnCloseJob();
        if (_job == 0)
        {
            // Not recoverable, and not worth half-running: without the job there is no way to identify
            // our own window and no guarantee the shell dies with us.
            int error = Marshal.GetLastWin32Error();
            log.Error($"Console pane: could not create the job object (Windows error {error}).");
            return Strings.Get("console.failed");
        }

        if (!Launch(workingDirectory, out int pid))
        {
            int error = Marshal.GetLastWin32Error();
            log.Error($"Console pane: CreateProcessW failed for '{ShellCommandLine}' (Windows error {error}).");
            Stop();
            return Strings.Get("console.failed");
        }

        log.Debug($"Console pane: started {ShellCommandLine} as pid {pid} in {workingDirectory}");

        nint window = await FindMyWindowAsync(cancellationToken).ConfigureAwait(true);
        if (window == 0)
        {
            log.Error("Console pane: no console window appeared. The Windows console host may be unavailable.");
            Stop();
            return Strings.Get("console.failed");
        }

        WindowHandle = window;

        if (!Reparent(container))
        {
            Stop();
            return Strings.Get("console.failed");
        }

        return null;
    }

    /// <summary>
    /// Creates the process suspended, puts it in the job, and only then lets it run.
    ///
    /// Not <c>Process.Start</c>, which can express neither <c>CREATE_SUSPENDED</c> nor "give me the
    /// handle before anything executes" -- and both are what keep the shell inside the job.
    /// </summary>
    private unsafe bool Launch(string workingDirectory, out int pid)
    {
        pid = 0;

        // CreateProcessW may write into its command line, so it gets a private, writable,
        // null-terminated copy rather than the literal.
        Span<char> command = stackalloc char[ShellCommandLine.Length + 1];
        ShellCommandLine.CopyTo(command);
        command[ShellCommandLine.Length] = '\0';

        var startup = new StartupInfo
        {
            Size = sizeof(StartupInfo),

            // Born hidden, so there is no top-level console flashing on screen for the few hundred
            // milliseconds before it is reparented. Verified: conhost honours this as its own nCmdShow.
            Flags = StartfUseShowWindow,
            ShowWindow = SwHide,
        };

        var information = default(ProcessInformation);

        nint environment = UserEnvironment();

        try
        {
            fixed (char* commandLine = command)
            fixed (char* directory = workingDirectory)
            {
                if (!CreateProcessW(
                        null, commandLine, 0, 0, false,
                        CreateNewConsole | CreateSuspended
                            | (environment == 0 ? 0 : CreateUnicodeEnvironment),
                        environment, directory, &startup, &information))
                {
                    return false;
                }
            }
        }
        finally
        {
            if (environment != 0)
                DestroyEnvironmentBlock(environment);
        }

        _process = information.Process;
        _thread = information.Thread;
        pid = information.ProcessId;
        _hostProcessId = pid;

        if (!AssignProcessToJobObject(_job, _process))
            log.Error($"Console pane: AssignProcessToJobObject failed (Windows error {Marshal.GetLastWin32Error()}).");

        ResumeThread(_thread);
        return true;
    }

    /// <summary>
    /// The environment the shell is given, built here rather than inherited from this process.
    ///
    /// <b>Passing NULL to <c>CreateProcessW</c> hands the child whatever block this process was
    /// started with, and that is decided by whoever launched the resident service.</b> Started by
    /// the MSI's custom action it is msiexec's block, which carries the <i>machine</i> PATH and
    /// none of <c>HKCU\Environment</c>'s — measured on a real install as 51 entries against the
    /// 76 a shell gets, with every user-scoped tool directory missing. So `claude`, anything under
    /// <c>%APPDATA%\npm</c>, cargo, pyenv, scoop and the dotnet tools were all "not recognized" in
    /// the pane while working in every other terminal on the machine.
    ///
    /// <c>CreateEnvironmentBlock</c> with <c>bInherit: false</c> builds the block from the
    /// registry the way a fresh logon does — system and user merged and expanded — so the pane
    /// answers like the user's own shell. Doing it per launch rather than once also settles the
    /// second half of the problem: a resident service holds a PATH snapshot, so a tool installed
    /// after login would otherwise stay invisible until FlickGit was restarted.
    ///
    /// Returns 0 when the block cannot be built, and the caller then passes NULL as before. A pane
    /// with the old environment is worth having; no pane is not.
    /// </summary>
    private nint UserEnvironment()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out nint token))
        {
            log.Warn($"Console pane: OpenProcessToken failed (Windows error {Marshal.GetLastWin32Error()}); "
                + "the shell will inherit this process's environment.");

            return 0;
        }

        try
        {
            if (CreateEnvironmentBlock(out nint block, token, inherit: false))
                return block;

            log.Warn($"Console pane: CreateEnvironmentBlock failed (Windows error {Marshal.GetLastWin32Error()}); "
                + "the shell will inherit this process's environment.");

            return 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// The first top-level <c>ConsoleWindowClass</c> window whose process is in our job.
    ///
    /// Never by title, never by <c>GetForegroundWindow</c>, never by walking parent process ids. The
    /// job membership test is the only one that cannot be fooled by pid reuse, and it is what stops a
    /// stranger's console -- there are routinely a dozen on a desktop -- being adopted into our window.
    /// </summary>
    private async Task<nint> FindMyWindowAsync(CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();

        while (clock.Elapsed < StartTimeout)
        {
            if (HasExited())
            {
                log.Error("Console pane: the console host exited before it made a window.");
                return 0;
            }

            nint window = 0;
            while ((window = FindWindowExW(0, window, ConsoleWindowClass, null)) != 0)
            {
                // A real top-level, not a tooltip or an owned dialog.
                if (GetAncestor(window, GaRoot) != window || GetWindow(window, GwOwner) != 0)
                    continue;

                GetWindowThreadProcessId(window, out uint owner);
                if (IsMine(owner))
                    return window;
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(true);
        }

        return 0;
    }

    private bool IsMine(uint pid)
    {
        nint handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == 0)
            return false;

        try
        {
            return IsProcessInJob(handle, _job, out bool inJob) && inJob;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Parent first, styles second. MSDN is explicit that <c>SetParent</c> does not touch
    /// <c>WS_CHILD</c> or <c>WS_POPUP</c>, so a window parented without the style fix is a top-level
    /// window that happens to have a parent, and it draws its own frame inside our pane.
    /// </summary>
    private bool Reparent(nint container)
    {
        if (SetParent(WindowHandle, container) == 0)
        {
            int error = Marshal.GetLastWin32Error();

            // 5 is ERROR_ACCESS_DENIED, and here it means the container was created without
            // DPI_HOSTING_BEHAVIOR_MIXED -- see ConsoleHost.BuildWindowCore, which is the only place
            // that can be wrong about this.
            log.Error($"Console pane: SetParent failed (Windows error {error}).");
            return false;
        }

        // Computed as long rather than nint: WS_POPUP alone sets the top bit, so the mask does not fit
        // a 32-bit nint as a compile-time constant even though this only ever runs on x64.
        long style = GetWindowLongPtrW(WindowHandle, GwlStyle);

        // WS_VSCROLL is deliberately not in the mask. conhost's scrollback bar survives losing the
        // frame and is the only way back up through the buffer.
        long wanted = (style & ~TopLevelStyles) | WsChild | WsVisible;
        SetWindowLongPtrW(WindowHandle, GwlStyle, (nint)wanted);

        // WS_EX_APPWINDOW would keep a ghost of the console in Alt+Tab after it stopped being a window
        // of its own.
        long extended = GetWindowLongPtrW(WindowHandle, GwlExStyle);
        SetWindowLongPtrW(WindowHandle, GwlExStyle, (nint)(extended & ~WsExAppWindow));

        return true;
    }

    /// <summary>
    /// Resizes the console to a pixel rectangle.
    ///
    /// One <c>SetWindowPos</c> and nothing else: measured on build 26200, conhost re-lays-out its rows
    /// and columns from the window size exactly, with no leftover slack (1000x600 px gave 84x21 cells,
    /// 640x300 gave 59x12). The <c>AttachConsole</c> plus <c>CONOUT$</c> route that would otherwise be
    /// needed -- with its process-global attach state and its shrink-then-grow ordering rules -- is
    /// therefore not here, because it is not needed.
    /// </summary>
    public void Resize(int widthPixels, int heightPixels)
    {
        // A collapsed pane measures zero, and a zero-sized console is one conhost refuses and the user
        // cannot read.
        if (!IsRunning || widthPixels <= 0 || heightPixels <= 0)
            return;

        SetWindowPos(WindowHandle, 0, 0, 0, widthPixels, heightPixels,
            SwpNoZOrder | SwpNoActivate | SwpFrameChanged | SwpShowWindow);
    }

    /// <summary>
    /// Moves keyboard focus into the console.
    ///
    /// The console belongs to another process with its own input queue, so a plain <c>SetFocus</c> from
    /// here is refused: a thread may only focus a window on its own queue, and the way onto the
    /// console's queue is <c>AttachThreadInput</c>.
    ///
    /// <b>Which thread to attach to is the whole difficulty, because Windows lies about it.</b> For a
    /// console window <c>GetWindowThreadProcessId</c> reports the console's <i>client</i> -- here the
    /// shell -- rather than conhost, which created the window and whose thread actually owns it. That
    /// is a deliberate compatibility fiction for code that expects <c>GetConsoleWindow</c> to belong to
    /// the console application. Attaching to the reported thread succeeds, since the thread exists,
    /// and <c>SetFocus</c> then fails with <c>ERROR_ACCESS_DENIED</c> every time, since it is still not
    /// the window's queue. Measured on build 26200: the reported thread owned no windows at all, and
    /// focus took the moment the join was moved to conhost's window thread.
    ///
    /// There is no call that answers honestly, so the owner is found by trying: each thread of the
    /// conhost we launched is joined in turn and <c>SetFocus</c> is asked to prove it, and the first
    /// thread it takes on is kept for the life of the shell. A few syscalls, once.
    ///
    /// <b>The attach is held, not wrapped around the call.</b> Detaching splits the queues again and
    /// each thread's focus reverts to its own window, so a detach in a <c>finally</c> undoes the very
    /// <c>SetFocus</c> it looks like it is protecting. It stays attached until <see cref="Blur"/>.
    /// </summary>
    public void Focus()
    {
        if (!IsRunning)
            return;

        if (_attachedThread == 0 && !Join())
            return;

        if (GetFocus() == WindowHandle)
            return;

        SetFocus(WindowHandle);

        if (GetFocus() != WindowHandle)
            log.Debug($"Console pane: SetFocus was refused (Windows error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>
    /// Joins our input queue to the thread that owns the console window, finding that thread the first
    /// time. Returns false, with the queues left as they were, when no thread of the host will take
    /// the focus -- which means the console is not one we can drive, and typing into it is not on.
    /// </summary>
    private bool Join()
    {
        uint self = GetCurrentThreadId();

        if (_windowThread != 0)
        {
            if (!AttachThreadInput(self, _windowThread, true))
                return false;

            _attachedThread = _windowThread;
            return true;
        }

        foreach (uint thread in HostThreads())
        {
            if (thread == self || !AttachThreadInput(self, thread, true))
                continue;

            SetFocus(WindowHandle);
            if (GetFocus() == WindowHandle)
            {
                _windowThread = thread;
                _attachedThread = thread;
                return true;
            }

            AttachThreadInput(self, thread, false);
        }

        log.Error("Console pane: no thread of the console host would accept focus for its window.");
        return false;
    }

    /// <summary>The threads of the console host we launched, or nothing if it has already gone.</summary>
    private IEnumerable<uint> HostThreads()
    {
        Process host;
        try
        {
            host = Process.GetProcessById(_hostProcessId);
        }
        catch (ArgumentException)
        {
            yield break;
        }

        using (host)
        {
            foreach (ProcessThread thread in host.Threads)
                yield return (uint)thread.Id;
        }
    }

    /// <summary>
    /// Gives the keyboard back and splits the input queues again.
    ///
    /// The queues are not left joined for the life of the window on purpose: while they are, a wedged
    /// conhost can wedge the WPF UI thread with it, and this process's whole premise is a window that
    /// paints in 120 ms.
    /// </summary>
    public void Blur()
    {
        if (_attachedThread == 0)
            return;

        AttachThreadInput(GetCurrentThreadId(), _attachedThread, false);
        _attachedThread = 0;
    }

    /// <summary>
    /// Ends the session. Closing the job handle is the kill: everything in it goes, including whatever
    /// the shell was running, which is what a bare <c>Kill</c> on the shell would leave orphaned.
    /// </summary>
    public void Stop()
    {
        //Before the window goes away, or our thread stays joined to a queue that no longer exists.
        Blur();

        if (_job != 0)
        {
            CloseHandle(_job);
            _job = 0;
        }

        if (_thread != 0)
        {
            CloseHandle(_thread);
            _thread = 0;
        }

        if (_process != 0)
        {
            CloseHandle(_process);
            _process = 0;
        }

        WindowHandle = 0;
        _windowThread = 0;
        _hostProcessId = 0;
    }

    private bool HasExited() =>
        _process == 0 || (GetExitCodeProcess(_process, out uint code) && code != StillActive);

    private static nint CreateKillOnCloseJob()
    {
        nint job = CreateJobObjectW(0, null);
        if (job == 0)
            return 0;

        var limits = default(JobExtendedLimitInformation);
        limits.Basic.LimitFlags = JobLimitKillOnJobClose;

        if (SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JobExtendedLimitInformation>()))
            return job;

        CloseHandle(job);
        return 0;
    }

    // ---- Win32 ----------------------------------------------------------------------------------

    private const uint CreateNewConsole = 0x00000010;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint TokenQuery = 0x0008;
    private const uint CreateSuspended = 0x00000004;
    private const int StartfUseShowWindow = 0x00000001;
    private const short SwHide = 0;
    private const uint StillActive = 259;

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint JobLimitKillOnJobClose = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;

    private const long WsChild = 0x40000000;
    private const long WsVisible = 0x10000000;
    private const long WsPopup = 0x80000000;
    private const long WsCaption = 0x00C00000;
    private const long WsThickFrame = 0x00040000;
    private const long WsSysMenu = 0x00080000;
    private const long WsMinimizeBox = 0x00020000;
    private const long WsMaximizeBox = 0x00010000;
    private const long WsExAppWindow = 0x00040000;

    /// <summary>Everything that makes the console a window in its own right.</summary>
    private const long TopLevelStyles =
        WsPopup | WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox;

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;

    private const uint GaRoot = 2;
    private const uint GwOwner = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;

        // Declared as handles rather than strings so the struct stays blittable, which is what the
        // LibraryImport generator needs. All three are null here.
        public nint Reserved;
        public nint Desktop;
        public nint Title;

        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Length;
        public nint Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobBasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobExtendedLimitInformation
    {
        public JobBasicLimitInformation Basic;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessW(
        char* applicationName,
        char* commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        char* currentDirectory,
        StartupInfo* startupInfo,
        ProcessInformation* processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref JobExtendedLimitInformation info, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    /// <param name="inherit">
    /// False, always. True would start from this process's block and merge the user's variables
    /// into it, which keeps the stale PATH this exists to replace.
    /// </param>
    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateEnvironmentBlock(
        out nint environment,
        nint token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyEnvironmentBlock(nint environment);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint parent, nint childAfter, string className, string? windowName);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint handle, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint handle, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint handle, uint command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint handle);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetParent(nint child, nint newParent);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetWindowLongPtrW(nint handle, int index);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowLongPtrW(nint handle, int index, nint value);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint thread, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetFocus(nint handle);

    [LibraryImport("user32.dll")]
    private static partial nint GetFocus();
}
