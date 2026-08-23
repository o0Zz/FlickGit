using System.Runtime.InteropServices;

namespace FlickGit.Shell;

/// <summary>
/// Starting <c>flick.exe</c>, which is the only thing this DLL is allowed to do to the world.
///
/// <c>ShellExecuteExW</c> rather than <c>System.Diagnostics.Process</c>: the managed class brings a
/// process-tracking apparatus this needs none of — no exit code is read, no output is captured,
/// nothing is waited on — and every type it drags in is a type linked into a DLL that loads on the
/// desktop's critical path.
///
/// <b>The command line is assembled as a string here</b>, which CLAUDE.md's "Coding Guidelines"
/// otherwise forbids. It is the same string the static registry verb already holds
/// (<c>"…\flick.exe" commit "%V"</c>): Win32 takes one command line, so this boundary has no
/// argument-vector form to use instead. The forbidden thing is composing a *Git* command this way,
/// where a path could become an option; here the only interpolated value is a folder path in quotes,
/// and it is Explorer that produced it.
/// </summary>
internal static unsafe partial class Launcher
{
    private const int SwShowNormal = 1;
    private const uint SeeMaskNoAsync = 0x00000100;
    private const uint SeeMaskFlagNoUi = 0x00000400;

    /// <summary>
    /// Runs <c>&lt;exe&gt; &lt;verb&gt; "&lt;folder&gt;"</c>.
    /// </summary>
    /// <param name="folder">
    /// Omitted from the command line when null. The CLI then defaults to its working directory, which
    /// is the documented behaviour of every verb — and a better answer than passing an empty quoted
    /// argument that would resolve to nothing.
    /// </param>
    public static bool Start(string exe, string verb, string? folder)
    {
        string arguments = folder is { Length: > 0 }
            ? $"{verb} \"{folder.TrimEnd('\\')}\""
            : verb;

        var info = new ShellExecuteInfo
        {
            Size = (uint)sizeof(ShellExecuteInfo),

            //NOASYNC because this DLL is a shell extension: the call has to be complete before
            //Explorer is free to tear the handler down underneath it. NO_UI because an error dialog
            //parented to nothing, on Explorer's thread, is worse than the failure it reports -- the
            //CLI is the thing that reports failures, and it is not running yet.
            Mask = SeeMaskNoAsync | SeeMaskFlagNoUi,
            Show = SwShowNormal,
        };

        fixed (char* file = exe)
        fixed (char* parameters = arguments)
        fixed (char* verbName = "open")
        {
            info.File = file;
            info.Parameters = parameters;
            info.Verb = verbName;

            return ShellExecuteExW(&info);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellExecuteInfo
    {
        public uint Size;
        public uint Mask;
        public nint Parent;
        public char* Verb;
        public char* File;
        public char* Parameters;
        public char* Directory;
        public int Show;
        public nint InstApp;
        public nint IdList;
        public char* Class;
        public nint KeyClass;
        public uint HotKey;
        public nint IconOrMonitor;
        public nint Process;
    }

    [LibraryImport("shell32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellExecuteExW(ShellExecuteInfo* info);
}
