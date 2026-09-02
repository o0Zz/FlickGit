using FlickGit.App.CommandLine;

namespace FlickGit.App.Mac;

/// <summary>
/// <see cref="INotifier"/> for a host with no notification area.
///
/// <see cref="CanNotify"/> being false is the whole of it: <c>VerbOutput.Say</c> then prints when
/// there is a console and falls back to <c>IDialogs.Notice</c> when there is not, so no outcome goes
/// unreported. The two methods are unreachable while CanNotify is false, and are empty rather than
/// throwing — a notifier that threw would turn a reporting path into a failure path.
/// </summary>
public sealed class SilentNotifier : INotifier
{
    public bool CanNotify => false;

    public void Show(string title, string message)
    {
    }

    public void Success(string title, string message)
    {
    }
}
