using FlickGit.Logging;
using FlickGit.Models;

namespace FlickGit.Files;

/// <summary>How far the removal got, and therefore what is now true on disk.</summary>
public enum RemovalOutcome
{
    /// <summary>Everything asked for is gone and every deletion is staged.</summary>
    Removed,

    /// <summary>Git has nothing under one of the targets. Nothing happened.</summary>
    NotTracked,

    /// <summary>Git refused: something holds uncommitted work. Nothing happened.</summary>
    Refused,

    /// <summary>The user said no. Nothing happened.</summary>
    Declined,

    /// <summary>
    /// The Recycle Bin would not take a folder. Everything before it in the batch is done; the index
    /// is untouched from that folder on.
    /// </summary>
    BinFailed,

    /// <summary>
    /// A folder <b>is in the Recycle Bin</b> and the index was not updated for it. The only outcome
    /// after which the working tree and the index disagree, and the only one whose message has to say
    /// where the files went.
    /// </summary>
    RecordFailed,
}

/// <param name="Relative">The repository-relative, forward-slashed path Git speaks.</param>
/// <param name="IsFolder">
/// Everything below it is in scope, so it is counted and confirmed before anything runs — and it goes
/// to the Recycle Bin rather than to <c>git rm</c>.
/// </param>
public sealed record RemovalTarget(string Relative, bool IsFolder);

/// <summary>
/// The totals across the whole selection — what the one question states.
///
/// Four numbers rather than one, because they answer different questions and only one of them is
/// recoverable: <paramref name="TrackedFiles"/> is what HEAD still has, and
/// <paramref name="UntrackedFiles"/> is what nothing has once the bin is emptied.
/// </summary>
/// <param name="Files">How many targets are files.</param>
/// <param name="Folders">How many targets are folders.</param>
/// <param name="TrackedFiles">What Git has across every target.</param>
/// <param name="UntrackedFiles">
/// What Git has never seen under the folder targets, excluding what <c>.gitignore</c> hides. Those go
/// to the Recycle Bin too and have nothing behind them, which is the part of the question that earns
/// the bin.
/// </param>
public sealed record RemovalPlan(int Files, int Folders, int TrackedFiles, int UntrackedFiles);

/// <param name="Error">Git's own words, or the shell's. Null when there is nothing to add.</param>
/// <param name="TrackedFiles">What Git had across the whole selection, for the success message.</param>
/// <param name="Path">
/// Where it stopped, for every outcome that is not <see cref="RemovalOutcome.Removed"/>. Null when the
/// outcome is about the batch rather than about one target — a declined question, or a batch with no
/// targets at all.
/// </param>
/// <param name="Done">
/// How many targets were fully removed before the failure. Zero on every outcome that changed nothing,
/// which is every one of them except <see cref="RemovalOutcome.BinFailed"/> and
/// <see cref="RemovalOutcome.RecordFailed"/> — the two that can stop part-way through a selection, and
/// therefore the two whose message has to say how much went first.
/// </param>
public sealed record Removal(
    RemovalOutcome Outcome,
    string? Error,
    int TrackedFiles,
    string? Path,
    int Done = 0);

/// <summary>
/// Removing a selection — files, folders, or a mixture — and <b>the order is the substance</b>:
///
/// <code>
/// 1. count what Git has, per target    nothing under one of them is a refusal, not an empty success
/// 2. gate every target                 --dry-run, so this is the last point at which nothing has happened
/// 3. count what Git has not            the untracked files, for the question
/// 4. ask the user, once                with the totals in it
/// 5. the files                         one `git rm`, which deletes and records in the same step
/// 6. the folders, one at a time         Recycle Bin, then `rm -r --cached`
/// </code>
///
/// <b>Step 2 has to precede step 5, and it has to cover every target.</b> A folder is where the
/// untracked files are, so <c>git rm -r</c> is the wrong instrument — it refuses over them or leaves
/// them behind — and the working tree is dealt with by the Recycle Bin instead. That puts the
/// destructive step outside Git, where Git can no longer refuse it, so the refusal has to be collected
/// in advance. Run the two the other way round and a folder holding uncommitted work is in the bin
/// before anything objects.
///
/// That is a silent failure and it is invisible from the outside — every step still "succeeds" — which
/// is why this is in Core with tests rather than in the verb, where it could only be exercised by
/// clicking and only observed by losing something.
///
/// <b>Gating every target before asking is what a selection adds to that rule</b>, and it is the whole
/// reason the question is asked once for the batch rather than once per item. Per-item questions would
/// run the gate for the fifth item only after the user had already answered — and therefore already
/// destroyed — the first four.
///
/// <b>Any refusal refuses the whole batch.</b> Half a removal is the state the user cannot reason
/// about: nothing in the working tree says which half, and the question they answered described all of
/// it.
///
/// <b>The files go before the folders</b> because that orders the two destructive steps by how
/// recoverable they are. <c>git rm</c> without <c>-f</c> only ever removes content HEAD still has, so
/// <i>Revert file…</i> brings it back; a binned folder carries untracked files that nothing but the
/// Recycle Bin has. If the batch is going to stop part-way, it should stop before the irreversible half.
///
/// The two steps this cannot perform itself are parameters rather than dependencies: asking the user is
/// a window, and the Recycle Bin is a Windows shell facility that <c>net9.0</c> cannot reach.
/// Per-invocation, so this stays a singleton with nothing to reset between two right-clicks.
/// </summary>
public sealed class RemovalFlow(TrackingService tracking, ILog log)
{
    /// <param name="confirmAsync">
    /// The question, with the totals. False stops the flow before anything is touched.
    /// </param>
    /// <param name="sendToRecycleBinAsync">
    /// The working tree half, for one folder. Its <c>Error</c> may be null on failure — the shell puts
    /// up its own dialog when it cannot delete, and paraphrasing that is worse than saying nothing.
    /// </param>
    public async Task<Removal> RunAsync(
        RepositoryInfo repository,
        IReadOnlyList<RemovalTarget> targets,
        Func<RemovalPlan, Task<bool>> confirmAsync,
        Func<RemovalTarget, Task<TrackingResult>> sendToRecycleBinAsync,
        CancellationToken cancellationToken)
    {
        if (targets.Count == 0)
            return new Removal(RemovalOutcome.NotTracked, null, 0, null);

        //1. Git's own answer to "is there anything here to remove", per target and asked as a count
        //because the question in step 4 has to state it.
        //
        //A target with nothing tracked under it refuses the *batch*, naming it. An untracked path is
        //not a quiet no-op to be skipped past: the user pointed at it and Explorer's own Delete is
        //what removes it, which is what the caller's message says.
        int tracked = 0;

        foreach (RemovalTarget target in targets)
        {
            int here = await tracking
                .TrackedCountAsync(repository, target.Relative, cancellationToken)
                .ConfigureAwait(false);

            if (here == 0)
                return new Removal(RemovalOutcome.NotTracked, null, 0, target.Relative);

            tracked += here;
        }

        //2. The gate, over every target. Nothing after this point can refuse on the user's behalf.
        if (await GateAsync(repository, targets, cancellationToken).ConfigureAwait(false) is { } refusal)
            return new Removal(RemovalOutcome.Refused, refusal.Error, tracked, refusal.Path);

        //3 and 4. Only now, because a batch that was going to be refused should be refused rather than
        //asked about.
        int untracked = 0;

        foreach (RemovalTarget target in targets)
        {
            if (!target.IsFolder)
                continue;

            untracked += await tracking
                .UntrackedCountAsync(repository, target.Relative, cancellationToken)
                .ConfigureAwait(false);
        }

        var plan = new RemovalPlan(
            Files: targets.Count(t => !t.IsFolder),
            Folders: targets.Count(t => t.IsFolder),
            TrackedFiles: tracked,
            UntrackedFiles: untracked);

        if (!await confirmAsync(plan).ConfigureAwait(false))
            return new Removal(RemovalOutcome.Declined, null, tracked, null);

        //5. The files, in one process. `git rm` deletes the working-tree copy and records the deletion
        //together, so there is no window in which one has happened and the other has not.
        string[] files = [.. targets.Where(t => !t.IsFolder).Select(t => t.Relative)];

        if (files.Length > 0)
        {
            TrackingResult removed = await tracking
                .RemoveAsync(repository, files, cancellationToken)
                .ConfigureAwait(false);

            if (!removed.Succeeded)
            {
                //The gate passed and this did not, so something changed underneath us in between -- a
                //terminal, an IDE, a build. Nothing is in the bin yet, which is the whole point of the
                //files going first: Done stays zero because nothing was done.
                return new Removal(RemovalOutcome.Refused, removed.Error, tracked, files[0]);
            }
        }

        //6. The folders, one at a time, bin before index. Stopping here leaves every folder before this
        //one finished, which is why the outcome carries both the path it stopped on and the count that
        //went before it.
        int done = files.Length;

        foreach (RemovalTarget target in targets)
        {
            if (!target.IsFolder)
                continue;

            TrackingResult binned = await sendToRecycleBinAsync(target).ConfigureAwait(false);

            if (!binned.Succeeded)
                return new Removal(RemovalOutcome.BinFailed, binned.Error, tracked, target.Relative, done);

            //The index catches up with a working tree that has already changed.
            TrackingResult recorded = await tracking
                .RemoveFolderAsync(repository, target.Relative, cancellationToken)
                .ConfigureAwait(false);

            if (!recorded.Succeeded)
                return new Removal(RemovalOutcome.RecordFailed, recorded.Error, tracked, target.Relative, done);

            done++;
        }

        log.Info($"Removed {targets.Count} target(s) and staged {tracked} deletion(s) in {repository.Root}.");
        return new Removal(RemovalOutcome.Removed, null, tracked, null, done);
    }

    /// <summary>
    /// Whether Git will let every target go, with nothing done about it either way — or the first
    /// target it refuses, and why.
    ///
    /// <b>The files are gated together and the folders one at a time</b>, which is not an
    /// inconsistency: one <c>git rm --dry-run</c> over the file list is the same all-or-nothing check
    /// the real call will make, and Git's refusal already names the offending file. A folder has to be
    /// asked about separately so that its own path is what comes back — the caller's message says which
    /// folder held the uncommitted work, and a combined call would only name the file inside it.
    /// </summary>
    private async Task<(string? Error, string Path)?> GateAsync(
        RepositoryInfo repository,
        IReadOnlyList<RemovalTarget> targets,
        CancellationToken cancellationToken)
    {
        string[] files = [.. targets.Where(t => !t.IsFolder).Select(t => t.Relative)];

        if (files.Length > 0)
        {
            TrackingResult allowed = await tracking
                .CanRemoveAsync(repository, files, cancellationToken)
                .ConfigureAwait(false);

            if (!allowed.Succeeded)
                return (allowed.Error, files[0]);
        }

        foreach (RemovalTarget target in targets)
        {
            if (!target.IsFolder)
                continue;

            TrackingResult allowed = await tracking
                .CanRemoveFolderAsync(repository, target.Relative, cancellationToken)
                .ConfigureAwait(false);

            if (!allowed.Succeeded)
                return (allowed.Error, target.Relative);
        }

        return null;
    }
}
