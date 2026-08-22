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
/// The order CLAUDE.md prescribes is: the folder selected in Explorer, then the folder Explorer is
/// showing, then the most recently used repository — "clearly labelled in the popup header". The
/// first two need <c>IShellWindows</c> and arrive with <see cref="ExplorerFolderResolver"/>; until
/// then this falls back to the MRU and says so, which is the honest behaviour rather than a
/// placeholder.
/// </summary>
public sealed class QuickCommitLauncher(
    ExplorerFolderResolver folders,
    RepositoryService repositories,
    RecentRepositories recent,
    QuickCommitWindowHost popup,
    WindowVerbs windows,
    ILog log)
{
    /// <param name="foreground">
    /// The window that was in front when the trigger fired. Zero when there was none to speak of —
    /// a tray click, for instance — which lands on the recent list.
    /// </param>
    public async Task LaunchAsync(nint foreground)
    {
        try
        {
            FolderCandidates candidates = await folders
                .ResolveAsync(foreground, CancellationToken.None)
                .ConfigureAwait(true);

            //Explorer's answer wins outright, and that includes its negative answer. A user looking
            //at a folder that is not a repository is asking to clone into *that* folder -- falling
            //through to the most recent repository would commit somewhere they are not looking,
            //which is the one thing CLAUDE.md says the popup must never do.
            FolderCandidate[] fromExplorer = [.. candidates.Ordered.Where(c => c.Origin != FolderOrigin.MostRecent)];

            if (fromExplorer.Length > 0)
            {
                if (await OpenFirstRepositoryAsync(fromExplorer, candidates.Ambiguous).ConfigureAwait(true))
                    return;

                //None of them is a repository. Offer to clone into the one the user is actually
                //looking at; `git init` is a Phase 5 choice and deliberately not the default here.
                windows.Clone(VerbOutput.Direct(), fromExplorer[0].Path, url: null);
                return;
            }

            //Explorer had nothing to say -- a tray click, another application in front, or a shell
            //that did not answer in time. The most recent repository, clearly labelled as such.
            if (await OpenFirstRepositoryAsync(candidates.Ordered, candidates.Ambiguous).ConfigureAwait(true))
                return;

            VerbOutput.Direct().Notice(Strings.Get("app.name"), Strings.Get("quick.norecent"), compact: true);
        }
        catch (Exception ex)
        {
            //The trigger fires on a keypress, so a failure here must not take the resident service
            //with it -- and the user gets a notice rather than silence.
            log.Error($"Quick commit could not be launched: {ex}");
            VerbOutput.Direct().Notice(Strings.Get("error.title"), ex.Message, compact: false);
        }
    }

    /// <summary>
    /// Opens the popup on the first candidate that is a usable repository.
    /// </summary>
    /// <returns>False when none of them was one.</returns>
    private async Task<bool> OpenFirstRepositoryAsync(IReadOnlyList<FolderCandidate> candidates, bool ambiguous)
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

            //Every surface that resolves a repository remembers it, so the recent list stays an
            //honest "recently used" rather than "recently right-clicked".
            recent.Remember(repository);

            //The header has to say so whenever the popup is not showing the folder the user was
            //looking at -- either because it fell back, or because Explorer had several tabs open
            //and could not say which was in front.
            bool uncertain = candidate.Origin is FolderOrigin.MostRecent or FolderOrigin.ExplorerTab || ambiguous;

            await popup.ShowAsync(repository, isFallback: uncertain).ConfigureAwait(true);
            return true;
        }

        return false;
    }
}
