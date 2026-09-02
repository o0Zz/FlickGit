using System.Windows;
using System.Windows.Threading;
using FlickGit.App.CommandLine;

namespace FlickGit.App.Views;

/// <summary>
/// The WPF answer to <see cref="IDialogs"/>: the notice window and the confirmation window, which
/// the verb layer used to reach statically.
///
/// A class of two short methods rather than the two direct calls it replaces, and that is the point
/// — those calls were what tied the verb layer to WPF and the reason it could not be compiled for
/// anything else. What is genuinely WPF about them stayed here: that a notice with no owner has to
/// place itself, that these are windows rather than a MessageBox (which would be modal to the thread
/// that in resident mode also runs the tray icon and the pipe listener), and the dispatcher hop.
///
/// <b>The dispatcher hop is not decoration.</b> A verb can arrive on the pipe listener's thread, and
/// touching a <see cref="Window"/> from there throws. Both entry points marshal, so neither caller
/// has to know which thread it is on.
/// </summary>
public sealed class WindowDialogs : IDialogs
{
    public void Notice(string title, string message, bool compact) =>
        Dispatch(() =>
            //NoticeWindow defaults to CenterOwner, which is right for every other caller in the
            //product and wrong here: this is the one notice with no owner to centre on.
            new NoticeWindow(title, message, compact)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
            }.Show());

    public Task<bool> ConfirmAsync(string title, string body, string yes, string no, bool destructive = false) =>
        //Already answered by the time this returns: WPF's ShowDialog blocks, so there is nothing to
        //await and a completed task is the honest shape.
        Task.FromResult(Dispatch(() =>
            ConfirmWindow.Ask(owner: null, title, body, yes, no, destructive: destructive)));

    private static void Dispatch(Action action)
    {
        Dispatcher dispatcher = Application.Current.Dispatcher;

        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.Invoke(action);
    }

    private static T Dispatch<T>(Func<T> action)
    {
        Dispatcher dispatcher = Application.Current.Dispatcher;

        return dispatcher.CheckAccess() ? action() : dispatcher.Invoke(action);
    }
}
