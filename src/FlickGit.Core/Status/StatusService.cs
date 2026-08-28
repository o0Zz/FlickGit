using FlickGit.Commits;
using FlickGit.Diagnostics;
using FlickGit.Git;
using FlickGit.Merges;
using FlickGit.Models;
using FlickGit.Secrets;

namespace FlickGit.Status;

/// <summary>
/// Produces the commit window's file list: what changed, and by how many lines.
///
/// Three Git invocations, run **in parallel** — CLAUDE.md, "File List → Data sources".
/// Sequentially they cost the sum of three process starts; in parallel they cost the
/// slowest one, which is what makes the 60 ms warm budget reachable at all.
///
/// <code>
/// status --porcelain=v2 --branch -z   status letters, branch, ahead/behind
/// diff --numstat -z                   working tree vs index
/// diff --cached --numstat -z          index vs HEAD
/// </code>
///
/// They are then merged on path. The merge is the interesting part: a file can be
/// staged *and* modified again in the working tree, so both numstat calls can name it.
/// The counts are summed for display and kept apart for the tooltip.
/// </summary>
public sealed class StatusService(
    IGitProcessRunner git,
    UntrackedFileMeasurer untracked,
    MergeStateService merges,
    PreparedMessageService prepared,
    OperationTimings? timings = null)
{
    /// <summary>
    /// How many untracked rows get their line count read off the disk. See
    /// <see cref="UntrackedToMeasure"/> for why there is a limit and what the rows past it show.
    ///
    /// A named constant rather than a setting: nobody is going to want a different number, and Hard
    /// Requirement 2 rules out a settings key for a value with one sensible answer. Two hundred is
    /// comfortably past any change a person made by hand, and short of the thousands a stray
    /// dependency directory produces.
    /// </summary>
    private const int MeasuredUntrackedFiles = 200;

    public async Task<RepositoryStatus> GetStatusAsync(
        RepositoryInfo repository,
        CancellationToken cancellationToken)
    {
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        //--branch so the header comes out of this call rather than a fourth process.
        //
        //--untracked-files=all, not Git's default of normal, which collapses a wholly
        //untracked directory to a single "dir/" row. That row is unusable in every way
        //this window works: it cannot be ticked file by file, the measurer has nothing
        //to count, and clicking it asks the diff pane to read a directory as text -- so
        //a new folder holding new files was the one change the list could not show.
        //
        //Ignored files are unaffected either way -- listing those needs --ignored, which
        //nothing here passes. What this adds is one row per file inside an untracked
        //*directory*, which is unbounded: a stray node_modules is thousands of rows. They
        //arrive unticked, so the length is harmless, but each one used to cost a file read
        //-- see MeasuredUntrackedFiles for the ceiling that puts on the 60 ms budget.
        Task<GitResult> statusTask = git.ReadAsync(
            repository.Root,
            ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"],
            cancellationToken);

        //GitDiffFlags on both, or a `textconv` filter reports line counts for a binary file and the
        //list shows a fabricated "+42 -17" where it owes the user "bin". No -M: these are merged onto
        //the porcelain paths below, and porcelain does its own rename detection.
        Task<GitResult> worktreeTask = git.ReadAsync(
            repository.Root,
            ["diff", "--numstat", "-z", .. GitDiffFlags.ReadSafe],
            cancellationToken);

        Task<GitResult> stagedTask = git.ReadAsync(
            repository.Root,
            ["diff", "--cached", "--numstat", "-z", .. GitDiffFlags.ReadSafe],
            cancellationToken);

        await Task.WhenAll(statusTask, worktreeTask, stagedTask).ConfigureAwait(false);

        GitResult status = await statusTask.ConfigureAwait(false);
        if (!status.Succeeded)
            throw new GitOperationException("Read repository status", repository.Root, status);

        PorcelainStatus parsed = PorcelainV2Parser.Parse(status.StdOut);

        //A failed numstat is not fatal. The status letters are the load-bearing half --
        //without counts the list still shows what changed, and a diff on a file with an
        //unresolvable base is a real case (an unborn HEAD, a corrupt object). Losing the
        //whole window over a missing "+42" would be the wrong trade.
        IReadOnlyDictionary<string, NumstatEntry> worktreeCounts =
            ParseCountsOrEmpty(await worktreeTask.ConfigureAwait(false));
        IReadOnlyDictionary<string, NumstatEntry> stagedCounts =
            ParseCountsOrEmpty(await stagedTask.ConfigureAwait(false));

        List<GitFileChange> files = Merge(repository, parsed.Files, worktreeCounts, stagedCounts);

        timings?.Record("status+numstat merge", System.Diagnostics.Stopwatch.GetElapsedTime(startedAt));

        return new RepositoryStatus
        {
            Repository = repository,
            Branch = parsed.Branch,
            Upstream = parsed.Upstream,
            Ahead = parsed.Ahead,
            Behind = parsed.Behind,
            HeadCommit = parsed.HeadCommit,
            IsDetachedHead = parsed.IsDetachedHead,
            IsUnborn = parsed.IsUnborn,
            Files = files,

            //A handful of File.Exists calls over the Git directory the repository already knows --
            //no fourth process, which is why this can sit on the path budgeted at 60 ms. It is read
            //here rather than by the window so that every existing refresh carries it.
            Merge = merges.Read(repository),

            //And two more File.Exists over the same directory, for the same reason: read here, a
            //message prepared by Git reaches the commit window on the refresh it already does rather
            //than needing a call of its own.
            PreparedMessage = prepared.Read(repository),
        };
    }

    private static IReadOnlyDictionary<string, NumstatEntry> ParseCountsOrEmpty(GitResult result) =>
        result.Succeeded
            ? NumstatParser.Parse(result.StdOut)
            : new Dictionary<string, NumstatEntry>(StringComparer.Ordinal);

    private List<GitFileChange> Merge(
        RepositoryInfo repository,
        IReadOnlyList<GitFileChange> statusFiles,
        IReadOnlyDictionary<string, NumstatEntry> worktreeCounts,
        IReadOnlyDictionary<string, NumstatEntry> stagedCounts)
    {
        var merged = new List<GitFileChange>(statusFiles.Count);

        //Which untracked rows are worth a disk read, decided before any of them is built --
        //see the method for why there is a ceiling at all.
        IReadOnlySet<string> measurable = UntrackedToMeasure(statusFiles);

        foreach (GitFileChange file in statusFiles)
        {
            worktreeCounts.TryGetValue(file.Path, out NumstatEntry? worktree);
            stagedCounts.TryGetValue(file.Path, out NumstatEntry? staged);

            bool looksLikeSecret = SecretDetector.LooksLikeSecretPath(file.Path);

            merged.Add(file.IsUntracked
                ? WithUntrackedCounts(repository, file, looksLikeSecret, measurable.Contains(file.Path))
                : WithTrackedCounts(file, worktree, staged, looksLikeSecret));
        }

        //Conflicted first, untracked last, alphabetical inside each group. The order is
        //from CLAUDE.md and it matches how the list is used: the rows that need a
        //decision are at the top, the rows that are unticked by default are at the
        //bottom.
        merged.Sort(static (a, b) =>
        {
            int byRank = a.SortRank.CompareTo(b.SortRank);
            return byRank != 0 ? byRank : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });

        return merged;
    }

    /// <summary>
    /// The untracked paths that get a line count: the first <see cref="MeasuredUntrackedFiles"/> in
    /// display order.
    ///
    /// <b>Measuring every untracked file is the one loop here whose length is not bounded by the
    /// size of the change.</b> <c>--untracked-files=all</c> lists every file inside an untracked
    /// directory, so a stray <c>node_modules</c> is thousands of rows -- and a count for each means
    /// a file open plus a full read of its bytes, on the path CLAUDE.md budgets at 60 ms warm.
    ///
    /// The rows past the ceiling keep a null count, which is the display an oversized or unreadable
    /// file already gets: a blank counts column rather than a wrong number.
    ///
    /// Sorted by path here rather than picked off the list <see cref="Merge"/> has already sorted,
    /// because <see cref="GitFileChange"/> is a class whose counts are <c>init</c>-only -- a row
    /// cannot be measured once it is built. Reproducing the order costs nothing: every untracked row
    /// shares one <c>SortRank</c>, so within that group the sort <i>is</i> this comparison.
    /// </summary>
    private static IReadOnlySet<string> UntrackedToMeasure(IReadOnlyList<GitFileChange> statusFiles)
    {
        var untrackedPaths = new List<string>();

        foreach (GitFileChange file in statusFiles)
        {
            if (file.IsUntracked)
                untrackedPaths.Add(file.Path);
        }

        if (untrackedPaths.Count <= MeasuredUntrackedFiles)
            return untrackedPaths.ToHashSet(StringComparer.Ordinal);

        untrackedPaths.Sort(static (a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));

        return untrackedPaths.Take(MeasuredUntrackedFiles).ToHashSet(StringComparer.Ordinal);
    }

    private static GitFileChange WithTrackedCounts(
        GitFileChange file,
        NumstatEntry? worktree,
        NumstatEntry? staged,
        bool looksLikeSecret)
    {
        bool isBinary = (worktree?.IsBinary ?? false) || (staged?.IsBinary ?? false);

        //Summed for display, split kept for the tooltip. A file staged with +8 and then
        //modified again with +3 really has 11 added lines relative to HEAD-ish, and
        //showing only one half of that is how a user commits something they did not
        //think was there.
        int? added = isBinary ? null : Add(worktree?.Added, staged?.Added);
        int? removed = isBinary ? null : Add(worktree?.Removed, staged?.Removed);

        return new GitFileChange
        {
            Path = file.Path,
            OldPath = file.OldPath ?? staged?.OldPath ?? worktree?.OldPath,
            IndexStatus = file.IndexStatus,
            WorkTreeStatus = file.WorkTreeStatus,
            AddedLines = added,
            RemovedLines = removed,
            StagedAddedLines = staged?.Added,
            StagedRemovedLines = staged?.Removed,
            IsBinary = isBinary,
            IsUntracked = false,
            IsStaged = file.IsStaged,
            LooksLikeSecret = looksLikeSecret,

            //Tracked modifications and deletions are ticked by default -- this is the
            //fast path and the user is expected to be able to commit without reading the
            //list. A conflicted file is never ticked: committing a file with conflict
            //markers in it is the single worst thing this tool could do silently.
            IsSelected = !looksLikeSecret && !file.IsConflicted,

            //Carried across, not recomputed -- there is nothing here to recompute them from. This
            //record is rebuilt field by field, so a conflict's stages would silently become "neither
            //side exists" and the resolution bar would offer nothing on every row.
            HasOurSide = file.HasOurSide,
            HasTheirSide = file.HasTheirSide,
        };
    }

    /// <param name="measure">
    /// False past the ceiling <see cref="UntrackedToMeasure"/> sets, which leaves the counts null --
    /// the same display an unreadable file gets.
    /// </param>
    private GitFileChange WithUntrackedCounts(
        RepositoryInfo repository,
        GitFileChange file,
        bool looksLikeSecret,
        bool measure)
    {
        UntrackedFileMeasurer.Measurement measurement = measure
            ? untracked.Measure(Path.Combine(repository.Root, file.Path.Replace('/', Path.DirectorySeparatorChar)))
            : default;

        return new GitFileChange
        {
            Path = file.Path,
            IndexStatus = GitChangeType.None,
            WorkTreeStatus = file.WorkTreeStatus,
            AddedLines = measurement.AddedLines,
            RemovedLines = measurement.RemovedLines,
            IsBinary = measurement.IsBinary,
            SizeInBytes = measurement.SizeInBytes,
            IsUntracked = true,
            IsStaged = false,
            LooksLikeSecret = looksLikeSecret,

            //Never ticked. CLAUDE.md calls this "the single most valuable safety default
            //in the product", and it is the reason a hurried commit does not carry
            //bin/, obj/ and a heap dump with it.
            IsSelected = false,
        };
    }

    private static int? Add(int? a, int? b) =>
        a is null && b is null ? null : (a ?? 0) + (b ?? 0);
}
