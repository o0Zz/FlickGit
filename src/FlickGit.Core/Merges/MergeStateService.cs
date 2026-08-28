using FlickGit.Models;

namespace FlickGit.Merges;

/// <summary>
/// Whether the repository is part-way through a merge, a rebase, a cherry-pick or a revert.
///
/// <b>No <c>git.exe</c>, and that is the design rather than an optimisation.</b> Git records every
/// one of these states as a file or a directory it creates in the Git directory, and
/// <see cref="RepositoryInfo.GitDirectory"/> already came back from the <c>rev-parse</c> that
/// resolved the repository. So this is a handful of <c>File.Exists</c> calls -- which is what lets
/// <see cref="Status.StatusService"/> fold the answer into every status read without adding a fourth
/// process to the path CLAUDE.md budgets at 60 ms.
///
/// <b>Nothing here throws.</b> It runs on the status path, and a status window that failed to open
/// because a counter file was half-written would be a worse bug than a missing progress number. Every
/// failure degrades to <see cref="MergeState.None"/> or to a null step.
/// </summary>
public sealed class MergeStateService
{
    /// <summary>
    /// The state, read from the file system.
    ///
    /// The rebase directories are tested first, which is the order Git's own <c>wt-status.c</c> uses.
    /// It is a defensive order rather than a fix for an observed collision — Git 2.43 leaves no
    /// sequencer file beside <c>rebase-merge/</c>, for a conflicted pick or an <c>edit</c> stop — but a
    /// rebase drives the sequencer internally, so if one ever does appear the rebase is still the
    /// operation the user started and the one whose <c>--continue</c> spelling works.
    /// </summary>
    public MergeState Read(RepositoryInfo repository)
    {
        string gitDirectory = repository.GitDirectory;

        if (gitDirectory.Length == 0)
            return MergeState.None;

        try
        {
            //Two spellings of the same state. `rebase-merge` is the interactive machinery, which a
            //plain `git rebase` has also used since 2.26; `rebase-apply` is the older `am`-based one
            //and is still what `git rebase --apply` and `git am` produce.
            if (Directory.Exists(Path.Combine(gitDirectory, "rebase-merge")))
            {
                return new MergeState(
                    MergeOperation.Rebase,
                    ReadCount(gitDirectory, "rebase-merge", "msgnum"),
                    ReadCount(gitDirectory, "rebase-merge", "end"));
            }

            if (Directory.Exists(Path.Combine(gitDirectory, "rebase-apply")))
            {
                return new MergeState(
                    MergeOperation.Rebase,
                    ReadCount(gitDirectory, "rebase-apply", "next"),
                    ReadCount(gitDirectory, "rebase-apply", "last"));
            }

            //No counter for any of these three: each is one commit being applied, so "1 of 1" would
            //be a number that never changes and says nothing.
            if (File.Exists(Path.Combine(gitDirectory, "MERGE_HEAD")))
                return new MergeState(MergeOperation.Merge, null, null);

            if (File.Exists(Path.Combine(gitDirectory, "CHERRY_PICK_HEAD")))
                return new MergeState(MergeOperation.CherryPick, null, null);

            if (File.Exists(Path.Combine(gitDirectory, "REVERT_HEAD")))
                return new MergeState(MergeOperation.Revert, null, null);

            return MergeState.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            //An unreadable Git directory is not a state to report. Saying "nothing in progress" hides
            //the resolution bar, which leaves the window exactly as it is today.
            return MergeState.None;
        }
    }

    /// <summary>
    /// One of Git's counter files -- a single decimal number and a newline -- or null.
    ///
    /// Null rather than zero for every failure, because the caller formats "3 of 7" only when both
    /// halves came back: a counter read as 0 would print "0 of 7" and look like a state.
    /// </summary>
    private static int? ReadCount(string gitDirectory, string directory, string name)
    {
        try
        {
            string path = Path.Combine(gitDirectory, directory, name);

            if (!File.Exists(path))
                return null;

            return int.TryParse(File.ReadAllText(path).Trim(), out int value) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            //Git rewrites these between steps, so a read landing mid-write is a real race. The
            //progress number is the only thing lost.
            return null;
        }
    }
}
