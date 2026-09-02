using FlickGit.App.CommandLine;
using FlickGit.App.Settings;
using H.NotifyIcon;

namespace FlickGit.App.Resident;

/// <summary>
/// Success notifications, through the tray icon when there is one.
///
/// These matter precisely because the commit window closes itself after a successful commit by
/// default. Without a notification the only evidence is a window that vanished — CLAUDE.md,
/// "Notifications": "Committed 5 files / 8f9ab42 fix: handle rebase conflicts".
///
/// A balloon rather than a dialog. CLAUDE.md: "Avoid unnecessary confirmation dialogs. Optimise for
/// one-click workflows." A modal box after every commit is the opposite of that.
/// </summary>
public sealed class Notifier(FlickSettings settings) : INotifier
{
    /// <summary>
    /// The tray icon, set by the resident service once it exists. Null in a one-shot launch, where
    /// there is no tray icon and the window is still on screen to speak for itself.
    /// </summary>
    public TaskbarIcon? Tray { get; set; }

    /// <summary>
    /// Whether there is anywhere to show one at all — a tray icon, which means the resident service.
    ///
    /// Asked by <see cref="VerbOutput.Say"/>, which prefers a notification to a window for an
    /// ordinary outcome and has to put the window back when the answer is no.
    /// </summary>
    public bool CanNotify => Tray is not null;

    /// <summary>
    /// The commit celebration, and the one thing <c>ShowSuccessNotification</c> turns off — the
    /// setting says "after a successful commit" and means exactly that.
    /// </summary>
    public void Success(string title, string message)
    {
        if (!settings.ShowSuccessNotification)
            return;

        Show(title, message);
    }

    /// <summary>
    /// Anything that has to arrive: a failure with no window to say so in, and every verb outcome
    /// that reached <see cref="VerbOutput.Say"/> without a console to print to.
    ///
    /// <b>Deliberately not gated on <c>ShowSuccessNotification</c>.</b> That setting turns off the
    /// celebration after a commit, not the tool's ability to speak. A hotkey that could not be
    /// registered has to be sayable, and a modal window at logon would be worse than the problem it
    /// describes — and for `flick add` on a selection this notification <i>is</i> the report, so
    /// suppressing it would leave the operation with no outcome at all.
    /// </summary>
    public void Show(string title, string message)
    {
        if (Tray is null)
            return;

        try
        {
            Tray.ShowNotification(title, message);
        }
        catch (Exception)
        {
            //Notifications are a courtesy. A shell that refuses to show one -- focus assist, a
            //policy, a broken notification area -- is not a reason to fail the operation that
            //just happened.
        }
    }
}
