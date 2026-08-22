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
public sealed class Notifier(FlickSettings settings)
{
    /// <summary>
    /// The tray icon, set by the resident service once it exists. Null in a one-shot launch, where
    /// there is no tray icon and the window is still on screen to speak for itself.
    /// </summary>
    public TaskbarIcon? Tray { get; set; }

    public void Success(string title, string message)
    {
        if (!settings.ShowSuccessNotification)
            return;

        Show(title, message);
    }

    /// <summary>
    /// Something did not work, and there is no window to say so in.
    ///
    /// Deliberately not gated on <c>ShowSuccessNotification</c>: that setting turns off the
    /// celebration after a commit, not the tool's ability to report a failure. A hotkey that could
    /// not be registered or a popup Windows refused to raise has to be sayable, and a modal window
    /// at logon would be worse than the problem it describes.
    /// </summary>
    public void Warn(string title, string message) => Show(title, message);

    private void Show(string title, string message)
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
