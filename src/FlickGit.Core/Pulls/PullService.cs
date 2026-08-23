using FlickGit.Git;
using FlickGit.Models;
using FlickGit.Repositories;

namespace FlickGit.Pulls;

/// <summary>
/// `pull --rebase`, and the submodule update that follows it.
///
/// Two behaviours here come straight from CLAUDE.md and are the whole reason this is a
/// service rather than two lines at a call site:
///
/// <list type="bullet">
/// <item><description><b>A conflict is not an error to recover from automatically.</b>
/// "Do not automatically abort a rebase." A half-finished rebase is a state the user can
/// resolve; a `rebase --abort` fired on their behalf throws away the merge work they
/// were part-way through.</description></item>
/// <item><description><b>A submodule failure does not roll back the pull.</b> The pull
/// succeeded. Reporting it as a failure would invite the user to try to undo it.</description></item>
/// <item><description><b><c>--autostash</c> is unconditional.</b> There is no plain
/// <c>pull --rebase</c> anywhere in the product, and no second verb offering the stashing one.
/// A pull that refuses because the working tree is dirty is the state the user is in every
/// time they are part-way through something, and "come back when you have committed" is not
/// an answer a one-click menu entry gets to give. Git stashes only when there is something to
/// stash, restores it afterwards, and unwinds the whole thing itself if the rebase fails — which
/// is why this is Git's flag rather than a stash/pull/pop sequence of ours.</description></item>
/// </list>
/// </summary>
public sealed class PullService(IGitProcessRunner git, RepositoryService repositories)
{
    public async Task<PullOutcome> PullRebaseAsync(
        RepositoryInfo repository,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report("Pull --rebase");

        GitResult pull = await git
            .RunAsync(repository.Root, ["pull", "--rebase", "--autostash"], cancellationToken)
            .ConfigureAwait(false);
        repositories.Invalidate(repository.Root);

        if (!pull.Succeeded)
        {
            //Distinguish "stopped on conflicts" from "could not start". The first is a
            //state the user is now in and has to be told how to leave; the second is a
            //failure with nothing changed.
            bool conflicted = await IsRebaseInProgressAsync(repository, cancellationToken).ConfigureAwait(false);

            return new PullOutcome(
                Succeeded: false,
                StoppedOnConflict: conflicted,
                GitError: pull.ErrorText,
                SubmodulesUpdated: false,
                SubmoduleError: null,
                Suggestion: conflicted
                    ? "Resolve the conflicts, stage the files, then continue with:\n\ngit rebase --continue"
                    : null);
        }

        //Only when .gitmodules exists -- a file-system probe already done by
        //RepositoryService. CLAUDE.md, "Submodules": never run `git submodule status`
        //just to find out whether there are submodules.
        if (!repository.HasSubmodules)
            return new PullOutcome(true, false, null, false, null, null);

        progress?.Report("Update submodules");

        GitResult submodules = await git.RunAsync(
            repository.Root,
            ["submodule", "update", "--init", "--recursive"],
            cancellationToken).ConfigureAwait(false);

        //The pull stays successful either way. "The pull succeeded, the submodules are
        //stale, here is the error."
        return new PullOutcome(
            Succeeded: true,
            StoppedOnConflict: false,
            GitError: null,
            SubmodulesUpdated: submodules.Succeeded,
            SubmoduleError: submodules.Succeeded ? null : submodules.ErrorText,
            Suggestion: null);
    }

    /// <summary>
    /// True when the repository is sitting in an unfinished rebase.
    ///
    /// Asked by path rather than by running `git status` again: these two directories are
    /// how Git itself records an in-progress rebase, and the answer is needed on a
    /// failure path where another process start is the last thing wanted.
    /// </summary>
    private async Task<bool> IsRebaseInProgressAsync(
        RepositoryInfo repository,
        CancellationToken cancellationToken)
    {
        GitResult gitDir = await git.ReadAsync(
            repository.Root,
            ["rev-parse", "--git-dir"],
            cancellationToken).ConfigureAwait(false);

        if (!gitDir.Succeeded)
            return false;

        string dir = gitDir.StdOut.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (!Path.IsPathRooted(dir))
            dir = Path.Combine(repository.Root, dir);

        return Directory.Exists(Path.Combine(dir, "rebase-merge"))
               || Directory.Exists(Path.Combine(dir, "rebase-apply"));
    }
}

/// <param name="Succeeded">The pull itself. A submodule failure does not clear this.</param>
/// <param name="StoppedOnConflict">A rebase is now in progress and waiting for the user.</param>
/// <param name="GitError">Git's own stderr, when the pull failed.</param>
/// <param name="SubmodulesUpdated">False both when there are none and when the update failed.</param>
/// <param name="SubmoduleError">Reported separately from the pull, never merged into it.</param>
/// <param name="Suggestion">The next command to run, when there is a specific one.</param>
public sealed record PullOutcome(
    bool Succeeded,
    bool StoppedOnConflict,
    string? GitError,
    bool SubmodulesUpdated,
    string? SubmoduleError,
    string? Suggestion);
