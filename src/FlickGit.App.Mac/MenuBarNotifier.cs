using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using FlickGit.App.CommandLine;
using FlickGit.App.Settings;
using FlickGit.Logging;

namespace FlickGit.App.Mac;

/// <summary>
/// <see cref="INotifier"/> on macOS: a real Notification Centre banner, with the menu bar item's
/// tooltip as the fallback.
///
/// <b>Why <c>osascript</c> rather than an API.</b> The modern one is
/// <c>UNUserNotificationCenter</c>, which requires a bundle identifier, a code-signed bundle, and an
/// authorisation prompt the first time — none of which holds for a development run, and the signing
/// half of which is still open (see <c>docs/macos-port.md</c>, notarisation).
/// <c>NSUserNotification</c>, the one that needed none of that, was removed in macOS 11.
/// <c>display notification</c> works from any process today and puts the banner where the user
/// expects it. The cost is attribution: the banner is credited to the script runner rather than to
/// FlickGit until the bundle is notarised, at which point <see cref="Deliver"/> is the one method to
/// replace.
///
/// <b>Nothing here blocks.</b> The process is started and abandoned; a notification nobody sees is
/// not worth a millisecond of the commit path, which is why there is no wait and no exit-code check.
/// </summary>
public sealed class MenuBarNotifier(FlickSettings settings, ILog log) : INotifier
{
    /// <summary>The menu bar item, set once the application has started.</summary>
    public TrayIcon? Item { get; set; }

    /// <summary>
    /// True on macOS, where <c>osascript</c> is part of the system.
    ///
    /// <b>Read by <c>VerbOutput.Say</c> to decide between a notification and a window</b>, so
    /// answering true here is what stops an ordinary success costing the user a click. Off anywhere
    /// else, which is the development-on-Windows case: there the fallback window is the honest
    /// report.
    /// </summary>
    public bool CanNotify => OperatingSystem.IsMacOS();

    public void Show(string title, string message) => Deliver(title, message);

    /// <summary>
    /// Gated by <c>ShowSuccessNotification</c> — the commit celebration and nothing else. Where a
    /// notification is the only report of an outcome, callers use <see cref="Show"/> instead, because
    /// suppressing it would leave the operation with nothing to show for itself.
    /// </summary>
    public void Success(string title, string message)
    {
        if (settings.ShowSuccessNotification)
            Deliver(title, message);
    }

    private void Deliver(string title, string message)
    {
        //The tooltip regardless, so the menu bar item carries the last outcome even on a machine
        //where the banner was refused or suppressed.
        Dispatcher.UIThread.Post(() =>
        {
            if (Item is not null)
                Item.ToolTipText = $"{title}: {message}";
        });

        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            //One -e argument, built with ArgumentList so nothing is ever concatenated into a command
            //line. The quoting that matters is AppleScript's own, which Quote below owns.
            var start = new ProcessStartInfo("/usr/bin/osascript")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            start.ArgumentList.Add("-e");
            start.ArgumentList.Add($"display notification {Quote(message)} with title {Quote(title)}");

            //Disposed immediately: nothing waits on it, and leaving the handle open would leak one
            //per notification for the life of a resident process.
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex)
        {
            //Never thrown onward. This is the last step of an operation that already succeeded, and
            //a machine that refuses to show a banner must not turn a good commit into an error.
            log.Debug($"Notification could not be delivered: {ex.Message}");
        }
    }

    /// <summary>
    /// An AppleScript string literal.
    ///
    /// <b>Backslash first, then the quote</b>, or escaping the quote would escape the backslash this
    /// method had just inserted. Newlines become spaces: AppleScript has no escape for a literal one
    /// inside a quoted string, and a banner is a line anyway.
    /// </summary>
    private static string Quote(string value)
    {
        var text = new StringBuilder(value.Length + 2);

        text.Append('"');

        foreach (char c in value)
        {
            switch (c)
            {
                case '\\':
                    text.Append("\\\\");
                    break;

                case '"':
                    text.Append("\\\"");
                    break;

                case '\r':
                case '\n':
                    text.Append(' ');
                    break;

                default:
                    text.Append(c);
                    break;
            }
        }

        return text.Append('"').ToString();
    }
}
