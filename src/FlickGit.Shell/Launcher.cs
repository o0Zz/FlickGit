using System.Runtime.InteropServices;
using System.Text;

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
/// where a path could become an option; here the interpolated values are paths in quotes, and it is
/// Explorer that produced them.
///
/// <b>Quoting a path is sound because a Windows file name cannot contain a double quote.</b> No
/// path can close its own quote, so <c>CommandLineToArgvW</c> hands the child back exactly the
/// strings that went in — which is what makes it safe for this to be a list rather than one value.
/// </summary>
internal static unsafe partial class Launcher
{
    private const int SwShowNormal = 1;
    private const uint SeeMaskNoAsync = 0x00000100;
    private const uint SeeMaskFlagNoUi = 0x00000400;

    /// <summary>
    /// The most characters a command line may carry here.
    ///
    /// <c>CreateProcess</c> and <c>ShellExecuteExW</c> both stop at 32,767, and the margin covers the
    /// quoted executable path Windows puts in front of these arguments. <see cref="Selection"/> bounds
    /// its own walk by the same number, so the budget is one value with one owner rather than two that
    /// can disagree about where the line stops.
    /// </summary>
    public const int CommandLineBudget = 30_000;

    /// <summary>
    /// Runs <c>&lt;exe&gt; &lt;verb&gt; "&lt;p1&gt;" "&lt;p2&gt;" …</c> — or
    /// <c>&lt;exe&gt; &lt;verb&gt; --too-many &lt;count&gt;</c> when the selection will not fit on one.
    /// </summary>
    /// <param name="paths">
    /// What the verb acts on. Empty omits them entirely, and the CLI then defaults to its working
    /// directory, which is the documented behaviour of every verb -- and a better answer than passing
    /// an empty quoted argument that would resolve to nothing.
    /// </param>
    /// <param name="selected">
    /// How many items were selected. Greater than <paramref name="paths"/>'s length when Explorer had
    /// more than could be read, and that is the case this parameter exists for: <b>never a partial
    /// list.</b> A command carrying the first four hundred of five hundred selected files is a command
    /// acting on files the user never chose to leave out, and on <c>rm</c> it is one that deletes them.
    /// </param>
    public static bool Start(string exe, string verb, ReadOnlySpan<string> paths, int selected)
    {
        string arguments = Assemble(verb, paths, selected);

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

    /// <summary>
    /// The argument string, or the refusal that replaces it.
    ///
    /// <b>Measured rather than counted.</b> Paths vary in length, so how many of them there are is
    /// not the question -- the length of the line they make is. Over the budget, or with items
    /// Explorer had that were never read, the count goes instead and the App says so.
    /// </summary>
    private static string Assemble(string verb, ReadOnlySpan<string> paths, int selected)
    {
        if (paths.Length == 0)
            return verb;

        var text = new StringBuilder(verb, CommandLineBudget);

        foreach (string path in paths)
        {
            //TrimEnd because Explorer's own `%V` for a drive root arrives as `C:\`, and a trailing
            //backslash before a closing quote would escape it -- reaching the CLI as `C:"`.
            text.Append(" \"").Append(path.TrimEnd('\\')).Append('\"');
        }

        return selected > paths.Length || text.Length > CommandLineBudget
            ? $"{verb} --too-many {selected}"
            : text.ToString();
    }

    [LibraryImport("shell32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellExecuteExW(ShellExecuteInfo* info);
}
