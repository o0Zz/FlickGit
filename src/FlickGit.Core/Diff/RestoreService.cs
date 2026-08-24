using FlickGit.Git;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;

namespace FlickGit.Diff;

/// <param name="Succeeded">False leaves the working tree and the index exactly as they were.</param>
/// <param name="Error">Git's own words. Never paraphrased, never generic.</param>
public sealed record RestoreResult(bool Succeeded, string? Error)
{
    public static readonly RestoreResult Ok = new(true, null);

    public static RestoreResult Failed(string error) => new(false, error);
}

/// <summary>
/// Puts one file back the way HEAD has it — the working tree and the index together.
///
/// <b>This is the only place in the product that discards uncommitted work with a Git command</b>,
/// so the two halves of CLAUDE.md's Safety Rules meet here. It is on the forbidden list
/// (<c>git restore .</c>, <c>git checkout -- .</c>) because those spellings take the <i>whole</i>
/// working tree without being asked; this one names a single path the user right-clicked, after a
/// confirmation, which is the "explicit user intent, expressed in the moment" the same section
/// allows. The other half — "never discard uncommitted work" — is not this class's to keep: the
/// caller sends the copy on disk to the Recycle Bin first, the same way the file list's Delete does,
/// which is what makes the answer recoverable if it was the wrong one.
///
/// <b>Both sides, from HEAD.</b> <c>--staged --worktree --source=HEAD</c> rather than the default,
/// which restores the working tree from the <i>index</i> and would leave a staged change standing —
/// so a file the user had already <c>git add</c>-ed would come back looking reverted and still be
/// committed. "Revert this file" has one meaning, and it is the one the row's letter goes away for.
/// </summary>
public sealed class RestoreService(IGitProcessRunner git, RepositoryService repositories, ILog log)
{
    /// <summary>
    /// Whether reverting this file is a thing that can be asked for at all.
    ///
    /// The question is only ever <i>is this path in HEAD</i>, because a revert restores HEAD's
    /// version of it and there is nothing else it could restore. Every refusal below is a path that
    /// is not:
    ///
    /// <list type="bullet">
    /// <item><description><b>Untracked.</b> Git has never seen it, so HEAD has nothing to put back.
    /// Removing it is what the user wants and Delete is the item that does it, to the Recycle
    /// Bin.</description></item>
    /// <item><description><b>Added.</b> Staged, but still absent from HEAD — and this one is not
    /// merely useless, it is dangerous: <c>git restore --source=HEAD --staged --worktree</c> on a
    /// path HEAD does not have <b>deletes the file</b>, exit code 0, no message. That is uncommitted
    /// work destroyed by a command that reported success, which is the exact failure the Safety
    /// Rules exist to prevent. Unticking the row is how a staged new file is taken out of the
    /// commit.</description></item>
    /// <item><description><b>Renamed or copied.</b> HEAD has the <i>old</i> path, not this one, so a
    /// correct revert is two operations — restore the old, remove the new — with two ways to fail
    /// half way. A tool that is not a complete Git client may decline that; what it may not do is
    /// run the one-path command and silently delete the renamed file, which is the Added case
    /// again.</description></item>
    /// <item><description><b>Conflicted.</b> Resolving a merge by taking HEAD's side is a merge
    /// decision wearing a revert's label, and conflict resolution is out of scope
    /// entirely.</description></item>
    /// </list>
    ///
    /// What is left is every ordinary case: modified, deleted, type-changed, staged or not.
    /// </summary>
    public static bool CanRevert(GitFileChange file) =>
        !file.IsUntracked
        && !file.IsConflicted
        && file.IndexStatus is not (GitChangeType.Added or GitChangeType.Renamed or GitChangeType.Copied);

    /// <summary>
    /// Restores <paramref name="path"/> from HEAD, in the working tree and the index.
    /// </summary>
    /// <remarks>
    /// The path is passed after <c>--</c> and as its own argument, so a file named like an option
    /// cannot become one. There is deliberately no overload taking a list: this is reached from a
    /// right-click on one row, and a method that could take every path is a method that could be
    /// handed all of them.
    /// </remarks>
    public async Task<RestoreResult> RevertAsync(
        RepositoryInfo repository,
        string path,
        CancellationToken cancellationToken)
    {
        GitResult result = await git.RunAsync(
            repository.Root,
            ["restore", "--source=HEAD", "--staged", "--worktree", "--", path],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            log.Warn($"git restore of {path} failed ({result.ExitCode}): {result.StdErr.Trim()}");

            return RestoreResult.Failed(
                result.StdErr.Trim() is { Length: > 0 } stderr ? stderr : result.StdOut.Trim());
        }

        //Both the working tree and the index moved, so every cached answer about this repository is
        //stale -- and the palette's overview reads the same generation counter.
        repositories.Invalidate(repository.Root);

        log.Info($"Reverted {path} to HEAD in {repository.Root}.");
        return RestoreResult.Ok;
    }
}
