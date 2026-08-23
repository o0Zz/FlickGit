using FlickGit.App.Ai;
using FlickGit.App.CommandLine;
using FlickGit.App.Localization;
using FlickGit.App.Resident;
using FlickGit.App.Settings;
using FlickGit.Branches;
using FlickGit.Commits;
using FlickGit.Diff;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Status;

namespace FlickGit.App.ViewModels;

/// <summary>
/// Assembles one <see cref="CommitViewModel"/>.
///
/// A view model is not a service — it is per-window state — so it is not registered in the
/// container. But it needs ten services to exist, and the class that owns the window's lifecycle
/// should not have to carry all ten just to pass them along. That was the whole justification for
/// handing <see cref="CommitWindowHost"/> an <c>IServiceProvider</c>, which
/// <b>Hard Requirement 3</b> forbids; this is the honest version of the same thing.
///
/// The <see cref="DiffCache"/> is created per view model rather than shared: it holds diffs for one
/// repository and one selection, and a cache outliving its repository is how a stale diff reaches
/// the screen.
/// </summary>
public sealed class CommitViewModelFactory(
    StatusService status,
    DiffService diffs,
    CommitService commits,
    BranchService branches,
    CommitFlow flow,
    UpstreamConsent consent,
    PatchService patches,
    WorkingTreeWriter writer,
    CommitMessageService messages,
    FlickSettings settings,
    Notifier notifier,
    ILog log)
{
    public CommitViewModel Create()
    {
        var viewModel = new CommitViewModel(
            //A placeholder until the first Reset points it at a folder. The window is never shown
            //in this state; the resident service pre-warms it long before anybody right-clicks.
            RepositoryInfo.None,
            status,
            new DiffCache(diffs, log),
            commits,
            branches,
            flow,
            consent,
            patches,
            writer,
            messages,
            settings,
            log);

        //The window closes itself after a successful commit by default, so a notification is the
        //only thing left to say it worked. Subscribed here rather than inside the view model: the
        //view model's job is the outcome, not which surfaces hear about it.
        viewModel.Committed += result => notifier.Success(
            Strings.Get("app.name"),
            Strings.Get("commit.success", result.ShortHash, result.Subject));

        return viewModel;
    }
}
