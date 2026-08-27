using FlickGit.Logging;
using FlickGit.Models;

namespace FlickGit.Files;

/// <summary>How far the removal got, and therefore what is now true on disk.</summary>
public enum FolderRemovalOutcome
{
    /// <summary>The folder is in the Recycle Bin and the deletions are staged.</summary>
    Removed,

    /// <summary>Git has nothing under it. Nothing happened.</summary>
    NotTracked,

    /// <summary>Git refused: something inside holds uncommitted work. Nothing happened.</summary>
    Refused,

    /// <summary>The user said no. Nothing happened.</summary>
    Declined,

    /// <summary>The Recycle Bin would not take it. The index is untouched.</summary>
    BinFailed,

    /// <summary>
    /// The folder <b>is in the Recycle Bin</b> and the index was not updated. The only outcome after
    /// which the working tree and the index disagree, and the only one whose message has to say where
    /// the files went.
    /// </summary>
    RecordFailed,
}

/// <param name="TrackedFiles">What Git has under the folder — the number the question states.</param>
/// <param name="UntrackedFiles">
/// What Git has never seen under it, excluding what <c>.gitignore</c> hides. Those go to the Recycle
/// Bin too and have nothing behind them, which is the part of the question that earns the bin.
/// </param>
public sealed record FolderRemovalPlan(int TrackedFiles, int UntrackedFiles);

/// <param name="Error">Git's own words, or the shell's. Null when there is nothing to add.</param>
public sealed record FolderRemoval(FolderRemovalOutcome Outcome, string? Error, int TrackedFiles);

/// <summary>
/// Removing a folder, and <b>the order is the substance</b>:
///
/// <code>
/// 1. count what Git has          nothing under it is a refusal, not an empty success
/// 2. ask Git whether it may go   --dry-run, so this is the last point at which nothing has happened
/// 3. count what Git has not      the untracked files, for the question
/// 4. ask the user                with both numbers in it
/// 5. Recycle Bin                 the working tree, all of it, tracked or not
/// 6. record the deletions        --cached, because the working tree is already dealt with
/// </code>
///
/// <b>Step 2 has to precede step 5.</b> A folder is where the untracked files are, so
/// <c>git rm -r</c> is the wrong instrument — it refuses over them or leaves them behind — and the
/// working tree is dealt with by the Recycle Bin instead. That puts the destructive step outside Git,
/// where Git can no longer refuse it, so the refusal has to be asked for in advance. Run the two the
/// other way round and a folder holding uncommitted work is in the bin before anything objects.
///
/// That is a silent failure and it is invisible from the outside — every step still "succeeds" —
/// which is why this is in Core with tests rather than in the verb, where it could only be exercised
/// by clicking and only observed by losing something.
///
/// The two steps this cannot perform itself are parameters rather than dependencies: asking the user
/// is a window, and the Recycle Bin is a Windows shell facility that <c>net9.0</c> cannot reach.
/// Per-invocation, so this stays a singleton with nothing to reset between two right-clicks.
/// </summary>
public sealed class FolderRemovalFlow(TrackingService tracking, ILog log)
{
    /// <param name="confirmAsync">
    /// The question, with both counts. False stops the flow before anything is touched.
    /// </param>
    /// <param name="sendToRecycleBinAsync">
    /// The working tree half. Its <c>Error</c> may be null on failure — the shell puts up its own
    /// dialog when it cannot delete, and paraphrasing that is worse than saying nothing.
    /// </param>
    public async Task<FolderRemoval> RunAsync(
        RepositoryInfo repository,
        string path,
        Func<FolderRemovalPlan, Task<bool>> confirmAsync,
        Func<Task<TrackingResult>> sendToRecycleBinAsync,
        CancellationToken cancellationToken)
    {
        //1. Git's own answer to "is there anything here to remove", asked as a count because the
        //question in step 4 has to state it.
        int tracked = await tracking
            .TrackedCountAsync(repository, path, cancellationToken)
            .ConfigureAwait(false);

        if (tracked == 0)
            return new FolderRemoval(FolderRemovalOutcome.NotTracked, null, 0);

        //2. The gate. Nothing after this point can refuse on the user's behalf.
        TrackingResult allowed = await tracking
            .CanRemoveFolderAsync(repository, path, cancellationToken)
            .ConfigureAwait(false);

        if (!allowed.Succeeded)
            return new FolderRemoval(FolderRemovalOutcome.Refused, allowed.Error, tracked);

        //3 and 4. Only now, because a folder that was going to be refused should be refused rather
        //than asked about.
        int untracked = await tracking
            .UntrackedCountAsync(repository, path, cancellationToken)
            .ConfigureAwait(false);

        if (!await confirmAsync(new FolderRemovalPlan(tracked, untracked)).ConfigureAwait(false))
            return new FolderRemoval(FolderRemovalOutcome.Declined, null, tracked);

        //5.
        TrackingResult binned = await sendToRecycleBinAsync().ConfigureAwait(false);

        if (!binned.Succeeded)
            return new FolderRemoval(FolderRemovalOutcome.BinFailed, binned.Error, tracked);

        //6. The index catches up with a working tree that has already changed.
        TrackingResult recorded = await tracking
            .RemoveFolderAsync(repository, path, cancellationToken)
            .ConfigureAwait(false);

        if (!recorded.Succeeded)
            return new FolderRemoval(FolderRemovalOutcome.RecordFailed, recorded.Error, tracked);

        log.Info($"Removed {path} and staged {tracked} deletion(s) in {repository.Root}.");
        return new FolderRemoval(FolderRemovalOutcome.Removed, null, tracked);
    }
}
