using FlickGit.Git;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;
using static FlickGit.Git.GitPathspec;

namespace FlickGit.Diff;

/// <summary>
/// What "revert this file" means for one row — a question with two answers, because it turns on
/// something the row's letter alone does not say: whether HEAD has the path at all.
///
/// <b>This is one menu item, not two.</b> "Put this row back the way HEAD has it" is a single
/// sentence for the user, and for a path HEAD has never held, the way HEAD has it is <i>not
/// tracked</i> — so the mechanic falls out of the row's state rather than out of a second thing to
/// click.
/// </summary>
public enum RevertKind
{
    /// <summary>Nothing this tool will decide. The row is left exactly as it is.</summary>
    None,

    /// <summary>
    /// HEAD has the path. Both sides come back from it, over a copy sent to the Recycle Bin first,
    /// because this is the one thing in the product that discards uncommitted work.
    /// </summary>
    Restore,

    /// <summary>
    /// HEAD has no copy and the index does — a staged addition. The index entry goes and the file
    /// stays exactly where it is, so <b>nothing is discarded and nothing is binned</b>. This is the
    /// way back out of an <c>Add</c> pressed by mistake, and there is no other: Delete removes the
    /// file itself, and unticking the row alone leaves the index holding what the user did not want
    /// in it.
    /// </summary>
    Unstage,
}

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
/// <b>Only the restore half lives here.</b> <see cref="KindFor"/> also answers
/// <see cref="RevertKind.Unstage"/>, and that half is <c>CommitService.UnstageAsync</c> — the call the
/// commit sequence already makes for an unticked file. Two services rather than a second method here,
/// because the two answers are not variations of one command: one discards work and needs the bin, the
/// other touches the index and must not go near it.
///
/// <b>Both sides, from HEAD.</b> <c>--staged --worktree --source=HEAD</c> rather than the default,
/// which restores the working tree from the <i>index</i> and would leave a staged change standing —
/// so a file the user had already <c>git add</c>-ed would come back looking reverted and still be
/// committed. "Revert this file" has one meaning, and it is the one the row's letter goes away for.
/// </summary>
public sealed class RestoreService(IGitProcessRunner git, RepositoryService repositories, ILog log)
{
    /// <summary>
    /// Which of the two things a revert of this row is, or that it is neither.
    ///
    /// The question is only ever <i>where is this path</i>, because a revert puts back HEAD's version
    /// of it and there is nothing else it could put back.
    ///
    /// <list type="bullet">
    /// <item><description><b>Added → <see cref="RevertKind.Unstage"/>.</b> The index has it and HEAD
    /// does not, so the way HEAD has this path is: untracked. <c>git restore --staged</c> gets there
    /// and leaves the file alone. <b>What must never run on such a path is the restore below</b> —
    /// <c>--source=HEAD --staged --worktree</c> on a path HEAD does not have <b>deletes the file</b>,
    /// exit code 0, no message. That is uncommitted work destroyed by a command that reported
    /// success, which is the exact failure the Safety Rules exist to prevent, and it is why this is
    /// an enum rather than a bool.</description></item>
    /// <item><description><b>Untracked → <see cref="RevertKind.None"/>.</b> Neither HEAD nor the
    /// index has it, so there is no state to go back to. Removing it is what the user wants and
    /// Delete is the item that does it, to the Recycle Bin.</description></item>
    /// <item><description><b>Renamed or copied → <see cref="RevertKind.None"/>.</b> HEAD has the
    /// <i>old</i> path, not this one, so a correct revert is two operations — restore the old, remove
    /// the new — with two ways to fail half way. A tool that is not a complete Git client may decline
    /// that; what it may not do is run the one-path restore and silently delete the renamed
    /// file.</description></item>
    /// <item><description><b>Conflicted → <see cref="RevertKind.None"/>.</b> Resolving a merge by
    /// taking HEAD's side is a merge decision wearing a revert's label, and conflict resolution is
    /// out of scope entirely.</description></item>
    /// </list>
    ///
    /// Everything else is <see cref="RevertKind.Restore"/>: modified, deleted, type-changed, staged
    /// or not.
    /// </summary>
    public static RevertKind KindFor(GitFileChange file) =>
        file.IsUntracked
        || file.IsConflicted
        || file.IndexStatus is GitChangeType.Renamed or GitChangeType.Copied ? RevertKind.None
        : file.IndexStatus == GitChangeType.Added ? RevertKind.Unstage
        : RevertKind.Restore;

    /// <summary>
    /// Restores <paramref name="path"/> from HEAD, in the working tree and the index.
    /// </summary>
    /// <remarks>
    /// The path is passed after <c>--</c> and as its own argument, so a file named like an option
    /// cannot become one, and through <see cref="GitPathspec.Literal"/> so it cannot glob either.
    /// That second guard is the load-bearing one here: this is the only command in the product that
    /// asks Git to discard uncommitted work, so a pathspec matching a file the user did not select
    /// destroys work in it, with an exit code of 0.
    ///
    /// <b>There is deliberately no overload taking a list</b>, and reverting a multi-selection does not
    /// want one: the caller loops, because each file's copy on disk goes to the Recycle Bin
    /// immediately before its restore. Binning the whole selection and then restoring it in one command
    /// would leave every file binned and none replaced when the restore fails; interleaving leaves one.
    /// So the safer shape is also the one that keeps this method naming a single path.
    /// </remarks>
    public async Task<RestoreResult> RevertAsync(
        RepositoryInfo repository,
        string path,
        CancellationToken cancellationToken)
    {
        GitResult result = await git.RunAsync(
            repository.Root,
            ["restore", "--source=HEAD", "--staged", "--worktree", "--", Literal(path)],
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
