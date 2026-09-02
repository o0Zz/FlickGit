using System.Windows;
using FlickGit.App.CommandLine;

namespace FlickGit.App.Views;

/// <summary>
/// The WPF answer to <see cref="IDialogs"/>: the notice window and the confirmation window, which
/// the verb layer used to reach statically.
///
/// Two one-line methods rather than the two direct calls they replace, and that is the point — those
/// calls were what tied the verb layer to WPF and the reason it could not be compiled for anything
/// else. What is genuinely WPF about them stayed here: that a notice with no owner has to place
/// itself, and that these are windows rather than a MessageBox, which would be modal to the thread
/// that in resident mode also runs the tray icon and the pipe listener.
/// </summary>
public sealed class WindowDialogs : IDialogs
{
    public void Notice(string title, string message, bool compact) =>
        //NoticeWindow defaults to CenterOwner, which is right for every other caller in the product
        //and wrong here: this is the one notice with no owner to centre on.
        new NoticeWindow(title, message, compact)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        }.Show();

    public bool Confirm(string title, string body, string yes, string no, bool destructive = false) =>
        ConfirmWindow.Ask(owner: null, title, body, yes, no, destructive: destructive);
}
