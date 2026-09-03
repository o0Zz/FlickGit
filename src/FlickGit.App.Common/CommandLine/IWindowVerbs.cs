using FlickGit.Cli;
using FlickGit.Models;

namespace FlickGit.App.CommandLine;

/// <summary>
/// The verbs that open something and stay, behind an interface so <see cref="VerbRunner"/> can route
/// every verb on a host that has no windows yet.
///
/// Fourteen members rather than one <c>RunAsync(verb)</c>, deliberately. Which verbs are windows is
/// part of the command-line grammar and belongs with the rest of the routing in
/// <see cref="VerbRunner"/>; collapsing it to a single call would move that knowledge into each host
/// and let two hosts disagree about it. The cost is fourteen refusals in a host without windows,
/// which is fourteen one-line methods delegating to one helper.
/// </summary>
public interface IWindowVerbs
{
    Task<VerbResult> CommitAsync(VerbOutput output, RepositoryInfo repository);

    Task<VerbResult> PaletteAsync();

    Task<VerbResult> LogAsync(RepositoryInfo repository);

    Task<VerbResult> BlameAsync(VerbOutput output, RepositoryInfo repository, string path);

    Task<VerbResult> PullRequestAsync(RepositoryInfo repository);

    Task<VerbResult> PullAsync(RepositoryInfo repository);

    /// <summary>Switch to the primary branch, then pull there. Beside the pull, because it ends in one.</summary>
    Task<VerbResult> BackAsync(RepositoryInfo repository);

    Task<VerbResult> SwitchPickerAsync(RepositoryInfo repository);

    VerbResult TagPicker(RepositoryInfo repository);

    VerbResult StashPicker(RepositoryInfo repository);

    VerbResult Submodules(RepositoryInfo repository);

    VerbResult Repo(RepositoryInfo repository);

    VerbResult Clone(string path, string? url);

    VerbResult Terminal(VerbOutput output, string? path);
}
