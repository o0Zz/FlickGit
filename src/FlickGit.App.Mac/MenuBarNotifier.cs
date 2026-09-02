using Avalonia.Controls;
using Avalonia.Threading;
using FlickGit.App.CommandLine;
using FlickGit.App.Settings;

namespace FlickGit.App.Mac;

/// <summary>
/// <see cref="INotifier"/> through the macOS menu bar.
///
/// <b>Avalonia has <see cref="TrayIcon"/> built in</b>, and it maps onto <c>NSStatusItem</c> here and
/// <c>Shell_NotifyIcon</c> on Windows. The port was expected to need an <c>H.NotifyIcon.Avalonia</c>
/// package to match the WPF one; no such package exists, and none is wanted — this keeps the
/// dependency list one shorter than planned.
///
/// <b>What it cannot do yet is the notification itself.</b> A menu bar item is not a notification
/// centre: macOS delivers those through <c>UNUserNotification</c>, which needs an app bundle with
/// the right entitlement and asks the user's permission the first time. Until the bundle exists,
/// <see cref="CanNotify"/> is <c>false</c> — which is not a gap so much as the honest answer, and
/// <c>VerbOutput.Say</c> already routes to text or to a notice window when it hears it.
/// </summary>
public sealed class MenuBarNotifier(FlickSettings settings) : INotifier
{
    /// <summary>The menu bar item, set once the application has started.</summary>
    public TrayIcon? Item { get; set; }

    /// <summary>
    /// False until there is a bundle to deliver a real notification from. See the class remarks: an
    /// icon in the menu bar is somewhere to click, not somewhere to be told something.
    /// </summary>
    public bool CanNotify => false;

    public void Show(string title, string message) => Fallback(title, message);

    /// <summary>
    /// Gated by <c>ShowSuccessNotification</c> — the commit celebration and nothing else. Where a
    /// notification is the only report of an outcome, callers use <see cref="Show"/> instead.
    /// </summary>
    public void Success(string title, string message)
    {
        if (settings.ShowSuccessNotification)
            Fallback(title, message);
    }

    /// <summary>
    /// Unreachable while <see cref="CanNotify"/> is false, and written rather than left empty so the
    /// day the bundle arrives there is one place to change.
    /// </summary>
    private void Fallback(string title, string message) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (Item is not null)
                Item.ToolTipText = $"{title}: {message}";
        });
}
