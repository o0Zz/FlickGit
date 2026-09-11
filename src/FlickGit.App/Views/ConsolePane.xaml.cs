using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FlickGit.App.Localization;
using FlickGit.App.Terminal;
using FlickGit.Logging;

namespace FlickGit.App.Views;

/// <summary>
/// A real PowerShell at the bottom of the commit window, for the things Git cannot do for the user —
/// running <c>claude</c>, a one-off rebase, a build — without the trip out to another window and back.
///
/// This half is presentation and lifetime: the strip, the debounce, and which of the two focus
/// directions is which. <see cref="ConsoleSession"/> owns the process and the Win32.
///
/// <b>Nothing here starts a shell until the user expands the pane.</b> The resident service pre-warms
/// the commit window at logon, measuring and arranging it without ever showing it, so a pane that
/// launched anything during layout would cost every user a PowerShell at every login. The only path to
/// <see cref="StartAsync"/> is the toggle.
/// </summary>
public partial class ConsolePane : UserControl
{
    /// <summary>
    /// Matches the diff pane's re-diff debounce. WPF raises SizeChanged on every layout pass, so a
    /// splitter drag is a continuous stream of them, and a cross-process SetWindowPos per frame is
    /// visible as a stutter.
    /// </summary>
    private static readonly TimeSpan ResizeDelay = TimeSpan.FromMilliseconds(100);

    private readonly DispatcherTimer _resizeDebounce;

    private ConsoleSession? _session;

    /// <summary>
    /// The log, handed down by the window rather than injected: this control is constructed by XAML.
    /// The same route <see cref="CommitWindow.KeepAlive"/> takes.
    /// </summary>
    public ILog? Log { get; set; }

    /// <summary>True while keystrokes are going to the shell rather than to WPF.</summary>
    public bool IsConsoleFocused { get; private set; }

    public bool IsRunning => _session?.IsRunning == true;

    /// <summary>The user asked to come back to WPF. The window decides where the caret lands.</summary>
    public event Action? EscapeRequested;

    public ConsolePane()
    {
        InitializeComponent();

        HeaderText.Text = Strings.Get("console.header");
        StatusText.Text = Strings.Get("console.hint");

        _resizeDebounce = new DispatcherTimer { Interval = ResizeDelay };
        _resizeDebounce.Tick += OnResizeSettled;

        Host.ClickedIn += OnClickedIn;
        Host.EscapeRequested += OnEscapePressed;
        Host.FocusReceived += TakeKeyboard;

        SizeChanged += (_, _) =>
        {
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        };
    }

    /// <summary>Starts a shell rooted at <paramref name="workingDirectory"/>, if one is not already running.</summary>
    public async Task StartAsync(string workingDirectory)
    {
        if (IsRunning)
            return;

        _session ??= new ConsoleSession(Log ?? NullLog.Instance);

        Say(Strings.Get("console.starting"), failed: false);

        string? failure = await _session.StartAsync(Host.Container, workingDirectory).ConfigureAwait(true);

        if (failure is null)
        {
            Say(Strings.Get("console.hint"), failed: false);
            _session.Resize(Host.ClientSize.Width, Host.ClientSize.Height);
            return;
        }

        Say(failure, failed: true);
    }

    /// <summary>
    /// The strip's right-hand text. A failure is coloured by setting the brush and an ordinary message
    /// by clearing it again, so the Muted style stays the one source of the normal colour.
    /// </summary>
    private void Say(string text, bool failed)
    {
        StatusText.Text = text;

        if (failed)
            StatusText.Foreground = (Brush)FindResource("DangerText");
        else
            StatusText.ClearValue(TextBlock.ForegroundProperty);
    }

    /// <summary>
    /// Ends the session. Called when the window is reused for another repository and when it closes —
    /// a shell still rooted at the previous repository would be leaked state, and on Windows an open
    /// directory handle is enough to make a later branch switch fail.
    /// </summary>
    public void Stop()
    {
        _resizeDebounce.Stop();
        ReleaseFocus();

        _session?.Stop();

        Say(Strings.Get("console.hint"), failed: false);
    }

    /// <summary>
    /// Puts the caret in the shell, and arms the one gesture that can bring it back.
    ///
    /// Two halves, and both are needed. The Win32 half -- joining the queue of the thread that really
    /// owns the console window and calling <c>SetFocus</c> -- is <see cref="ConsoleSession.Focus"/>.
    /// The WPF half is here: WPF's own focus model has to move too, or it fights. While WPF believes one
    /// of its elements holds the keyboard it acts on that belief and restores Win32 focus to its own
    /// window at the next opportunity. Focusing the <see cref="HwndHost"/> element is what makes the
    /// two models agree: WPF records the host as its focused element and stops competing, and the
    /// host's <c>WM_SETFOCUS</c> carries the keyboard the rest of the way down through
    /// <see cref="TakeKeyboard"/>.
    /// </summary>
    public void FocusConsole()
    {
        if (!IsRunning)
        {
            (Log ?? NullLog.Instance).Debug("Console pane: FocusConsole ignored -- no shell is running.");
            return;
        }

        // Raises WM_SETFOCUS, and so TakeKeyboard -- but not when the host already holds WPF focus,
        // which is why the call below is not left to the event. Both paths are idempotent.
        Host.Focus();
        TakeKeyboard();
    }

    /// <summary>
    /// Hands the keyboard to the shell and arms the way back out. Reached from
    /// <see cref="FocusConsole"/> and from the host's <c>WM_SETFOCUS</c>, so focus arriving by a route
    /// this pane did not initiate -- WPF restoring its last focused element when the window is
    /// activated, above all -- lands the caret in the console rather than on an invisible container.
    /// </summary>
    private void TakeKeyboard()
    {
        if (!IsRunning)
            return;

        _session!.Focus();
        IsConsoleFocused = true;
        Host.ClaimEscape();
    }

    /// <summary>
    /// WPF keyboard focus landed on <paramref name="newFocus"/>. Anything but the console's own host
    /// means the keyboard has genuinely left the shell, whatever route it took -- a click in the file
    /// list, the diff or the message box, Tab, a collapsed element handing focus on.
    ///
    /// This is what keeps <see cref="IsConsoleFocused"/> an observation rather than a guess, and the
    /// guess had a specific cost: Ctrl+` is one gesture over two mechanisms, a WPF binding going in and
    /// a <c>RegisterHotKey</c> coming out, and only one is armed at a time. Left saying "focused" after
    /// the caret had come back to WPF it armed the wrong one, so Ctrl+` fired the exit path for a
    /// console the user was not in -- which looks exactly like the key doing nothing at all.
    /// </summary>
    public void NoteWpfFocus(object? newFocus)
    {
        if (!ReferenceEquals(newFocus, Host))
            ReleaseFocus();
    }

    /// <summary>Gives up the escape hotkey and the "console has focus" state, without moving the caret.</summary>
    public void ReleaseFocus()
    {
        if (IsConsoleFocused)
            (Log ?? NullLog.Instance).Debug("Console pane: releasing the console's claim on the keyboard.");

        Host.ReleaseEscape();
        _session?.Blur();
        IsConsoleFocused = false;
    }

    /// <summary>
    /// Drops the escape hotkey while FlickGit is not the active application, and takes it back
    /// afterwards.
    ///
    /// <see cref="RegisterHotKey"/> is machine-wide: left claimed, FlickGit would hold Ctrl+` away from
    /// whatever the user switched to. The focus state itself is untouched, because the console still
    /// has the caret and will still need a way out when they come back.
    /// </summary>
    public void SuspendEscape() => Host.ReleaseEscape();

    public void ResumeEscape()
    {
        if (IsConsoleFocused)
            Host.ClaimEscape();
    }

    private void OnClickedIn()
    {
        (Log ?? NullLog.Instance).Debug("Console pane: WM_PARENTNOTIFY -- a click landed in the console.");
        FocusConsole();
    }

    private void OnEscapePressed()
    {
        (Log ?? NullLog.Instance).Debug("Console pane: WM_HOTKEY -- Ctrl+` taking the caret out of the console.");
        ReleaseFocus();
        EscapeRequested?.Invoke();
    }

    private void OnResizeSettled(object? sender, EventArgs e) => ResizeNow();

    /// <summary>
    /// Resizes the console to the pane's current size at once, skipping the debounce.
    ///
    /// For a change that is a jump rather than a drag — folding the commit area away — where waiting
    /// 100 ms would show the shell at its old size in a pane that is already the height of the window.
    /// The layout pass first, because the row heights were assigned a moment ago and the container has
    /// not been arranged at its new size yet.
    /// </summary>
    public void ResizeNow()
    {
        _resizeDebounce.Stop();
        UpdateLayout();

        (int width, int height) = Host.ClientSize;
        _session?.Resize(width, height);
    }
}
