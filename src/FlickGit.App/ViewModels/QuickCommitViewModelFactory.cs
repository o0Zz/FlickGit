using FlickGit.App.Ai;
using FlickGit.App.CommandLine;
using FlickGit.App.Settings;
using FlickGit.Branches;
using FlickGit.Commits;
using FlickGit.Logging;
using FlickGit.Status;

namespace FlickGit.App.ViewModels;

/// <summary>
/// Assembles one <see cref="QuickCommitViewModel"/>.
///
/// Same reason as <see cref="CommitViewModelFactory"/>: a view model is per-window state rather
/// than a service, so it is not in the container, but it needs seven services to exist and the class
/// that owns the popup's lifecycle should not carry all seven just to pass them along.
/// </summary>
public sealed class QuickCommitViewModelFactory(
    StatusService status,
    BranchService branches,
    CommitFlow flow,
    CommitMessageService messages,
    UpstreamConsent consent,
    FlickSettings settings,
    ILog log)
{
    public QuickCommitViewModel Create() =>
        new(status, branches, flow, messages, consent, settings, log);
}
