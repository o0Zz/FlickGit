using System.Text;
using System.Windows;
using FlickGit.App.Infrastructure;
using FlickGit.App.Views;
using FlickGit.Cli;

namespace FlickGit.App.CommandLine;

/// <summary>
/// Says something on whichever surface the user is actually looking at.
///
/// Every verb faces the same question, and there are now three answers rather than two:
///
/// <list type="bullet">
/// <item><description><b>This process's console</b>, when it was launched from a terminal.</description></item>
/// <item><description><b>A window</b>, when it was launched from Explorer and there is no console
/// anywhere.</description></item>
/// <item><description><b>A buffer</b>, when the work arrived over the pipe and the console belongs
/// to the <i>client</i> process. The text goes back in the response for the stub to print.</description></item>
/// </list>
///
/// Deciding that at each call site is how the three drift apart, so it is decided here, once.
/// </summary>
public sealed class VerbOutput
{
    private readonly StringBuilder _output = new();
    private readonly StringBuilder _error = new();
    private readonly bool _capture;

    private VerbOutput(bool capture, bool hasConsole)
    {
        _capture = capture;
        HasConsole = hasConsole;
    }

    /// <summary>For a verb this process was launched to run. Text goes to its own console.</summary>
    public static VerbOutput Direct() => new(capture: false, hasConsole: ConsoleOutput.IsAvailable);

    /// <summary>
    /// For a verb arriving over the pipe. Text is collected for the response instead of printed.
    /// </summary>
    /// <param name="clientHasConsole">
    /// Whether the <i>stub</i> has somewhere to print. It decides between text and a window exactly
    /// as a direct launch would, because the user cannot tell the two apart and should not have to.
    /// </param>
    public static VerbOutput ForClient(bool clientHasConsole) =>
        new(capture: true, hasConsole: clientHasConsole);

    /// <summary>True when a write will reach somebody: a terminal, a pipe, a redirected file.</summary>
    public bool HasConsole { get; }

    /// <summary>What to put on the client's stdout. Empty unless capturing.</summary>
    public string Output => _output.ToString();

    /// <summary>What to put on the client's stderr. Empty unless capturing.</summary>
    public string Error => _error.ToString();

    /// <summary>Plain output. Only ever text — a table has no window form worth building.</summary>
    public void Line(string text = "")
    {
        if (_capture)
            _output.AppendLine(text);
        else
            ConsoleOutput.WriteLine(text);
    }

    /// <summary>An ordinary outcome: printed, or shown as a compact notice.</summary>
    public void Say(string title, string message)
    {
        if (HasConsole)
            Line(message);
        else
            Notice(title, message, compact: true);
    }

    /// <summary>A failure or a refusal: sent to stderr, or shown as a full notice.</summary>
    public void Fail(string title, string message)
    {
        if (!HasConsole)
        {
            Notice(title, message, compact: false);
            return;
        }

        if (_capture)
            _error.AppendLine(message);
        else
            ConsoleOutput.WriteError(message);
    }

    /// <summary>
    /// An outcome that carries its own success flag, and the exit code that goes with it.
    ///
    /// Install, uninstall and autostart all answer in exactly this shape — a bool and a sentence —
    /// and each was deciding between <see cref="Say"/> and <see cref="Fail"/>, then mapping the
    /// same bool to the same two exit codes, on its own.
    /// </summary>
    public VerbResult Report(string title, bool succeeded, string message)
    {
        if (succeeded)
            Say(title, message);
        else
            Fail(title, message);

        return VerbResult.Exit(succeeded ? ExitCodes.Success : ExitCodes.ConfigurationError);
    }

    /// <summary>
    /// A window, unconditionally — for the cases worth showing even with a console open.
    ///
    /// A window rather than a MessageBox: a MessageBox is modal to a thread that in resident mode
    /// also runs the tray icon and the pipe listener.
    /// </summary>
    public void Notice(string title, string message, bool compact) =>
        //The one notice in the product with no owner -- it answers a verb that may have no window at
        //all -- so it is also the one that has to say where to put itself. NoticeWindow defaults to
        //CenterOwner for the owned case, which is every other caller.
        new NoticeWindow(title, message, compact)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        }.Show();
}
