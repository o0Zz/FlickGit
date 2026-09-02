using Avalonia.Threading;
using FlickGit.App.CommandLine;
using FlickGit.App.Mac.Views;

namespace FlickGit.App.Mac;

/// <summary>
/// <see cref="IDialogs"/> on Avalonia.
///
/// Both methods marshal to the UI thread for the same reason the WPF implementation does: a verb can
/// arrive on the socket listener's thread, and a window touched from there throws. <c>Post</c> for
/// the notice, because nothing waits on an outcome; <c>InvokeAsync</c> for the question, because the
/// answer is the whole point.
/// </summary>
public sealed class AvaloniaDialogs : IDialogs
{
    public void Notice(string title, string message, bool compact)
    {
        //compact distinguishes a one-line window from a full one in the WPF views. This window sizes
        //to its content, so the distinction makes no difference to what the user sees.
        _ = compact;

        Dispatcher.UIThread.Post(() => MessageWindow.Notice(title, message));
    }

    public Task<bool> ConfirmAsync(string title, string body, string yes, string no, bool destructive = false) =>
        //InvokeAsync unwraps an async delegate for us, so there is one task here rather than a
        //Task<Task<bool>> to flatten.
        Dispatcher.UIThread.InvokeAsync(() => MessageWindow.AskAsync(title, body, yes, no, destructive));
}
