using FlickGit.App.CommandLine;
using FlickGit.App.Localization;
using FlickGit.App.Resident;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;

namespace FlickGit.App.Trigger;

/// <summary>
/// Turns "the trigger fired" into a repository and a window.
///
/// Split from <see cref="TriggerService"/> on purpose: that one owns Windows input resources, this
/// one owns the decision about which repository the user meant. Folding them together would give
/// one class that installs input hooks <i>and</i> resolves Explorer windows <i>and</i> opens
/// windows, which Hard Requirement 3 calls a class doing two jobs.
///
/// <b>Explorer's answer is the only answer.</b> The folder selected in Explorer, then the folder
/// Explorer is showing — and if Explorer has nothing to say, nothing happens at all. This used to
/// fall through to the most recently used repository, with the popup's header saying so; the popup
/// is gone and so is the fallback. A trigger pressed from an IDE opening the commit window on
/// whichever repository happened to be last is a guess, and CLAUDE.md's rule that this must never
/// act on a repository the user is not looking at is easier to keep by not guessing than by
/// labelling the guess.
/// </summary>
public sealed class CommitLauncher(
    ExplorerFolderResolver folders,
    RepositoryService repositories,
    RecentRepositories recent,
    CommitWindowHost commitWindow,
    WindowVerbs windows,
    ILog log)
{
    /// <param name="foreground">
    /// The window that was in front when the trigger fired. Zero when there was none to speak of —
    /// a tray click, for instance — which resolves to nothing and opens nothing.
    /// </param>
    public async Task LaunchAsync(nint foreground)
    {
        try
        {
            FolderCandidates candidates = await folders
                .ResolveAsync(foreground, CancellationToken.None)
                .ConfigureAwait(true);

            if (candidates.Ordered.Count == 0)
            {
                //Not a failure and not worth a notification: the user pressed the trigger somewhere
                //it does not apply, and the answer is for nothing to happen. Logged because it is
                //also what "the hotkey does nothing" looks like when something else has claimed it.
                log.Debug("The trigger fired with no Explorer folder to act on; nothing opened.");
                return;
            }

            if (candidates.Ambiguous)
            {
                //Several tabs on one window and no way to tell which is active. The window names the
                //repository in its title, so this is a log line rather than a prompt -- but it is the
                //answer to "why did it open the wrong one".
                log.Info("Explorer had several tabs open and none identifiable as active; using the first candidate.");
            }

            if (await OpenFirstRepositoryAsync(candidates.Ordered).ConfigureAwait(true))
                return;

            //None of them is a repository. Offer to clone into the one the user is actually looking
            //at; `git init` is deliberately not the default here.
            windows.Clone(candidates.Ordered[0].Path, url: null);
        }
        catch (Exception ex)
        {
            //The trigger fires on a keypress, so a failure here must not take the resident service
            //with it -- and the user gets a notice rather than silence.
            log.Error($"The commit window could not be launched: {ex}");
            VerbOutput.Direct().Notice(Strings.Get("error.title"), ex.Message, compact: false);
        }
    }

    /// <summary>
    /// Opens the commit window on the first candidate that is a usable repository.
    /// </summary>
    /// <returns>False when none of them was one.</returns>
    private async Task<bool> OpenFirstRepositoryAsync(IReadOnlyList<FolderCandidate> candidates)
    {
        foreach (FolderCandidate candidate in candidates)
        {
            RepositoryInfo? repository = await repositories
                .ResolveAsync(candidate.Path, CancellationToken.None)
                .ConfigureAwait(true);

            //A bare repository has no working tree, so it is not a candidate for committing --
            //but it is also not a reason to stop looking.
            if (repository is null || repository.IsBare)
                continue;

            //Every surface that resolves a repository remembers it, so the tray's recent list stays
            //an honest "recently used" rather than "recently right-clicked".
            recent.Remember(repository);

            await commitWindow.ShowAsync(repository).ConfigureAwait(true);
            return true;
        }

        return false;
    }
}
