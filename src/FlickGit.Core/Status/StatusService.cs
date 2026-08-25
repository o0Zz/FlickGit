using FlickGit.Diagnostics;
using FlickGit.Git;
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
    OperationTimings? timings = null)
{
    public async Task<RepositoryStatus> GetStatusAsync(
        RepositoryInfo repository,
        CancellationToken cancellationToken)
    {
        long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        //--branch so the header comes out of this call rather than a fourth process.
        //--untracked-files=all, not Git's default of normal, which collapses a wholly
        //untracked directory to a single "dir/" row. That row is unusable in every way
        //this window works: it cannot be ticked file by file, the measurer has nothing
        //to count, and clicking it asks the diff pane to read a directory as text -- so
        //a new folder holding new files was the one change the list could not show. The
        //rows it adds are ignored-file noise only in a repository with no .gitignore,
        //and they arrive unticked, which is what makes the extra length harmless.
        
        Task<GitResult> statusTask = git.ReadAsync(
            repository.Root,
            ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"],
            cancellationToken);

        Task<GitResult> worktreeTask = git.ReadAsync(
            repository.Root,
            ["diff", "--numstat", "-z"],
            cancellationToken);

        Task<GitResult> stagedTask = git.ReadAsync(
            repository.Root,
            ["diff", "--cached", "--numstat", "-z"],
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

        foreach (GitFileChange file in statusFiles)
        {
            worktreeCounts.TryGetValue(file.Path, out NumstatEntry? worktree);
            stagedCounts.TryGetValue(file.Path, out NumstatEntry? staged);

            bool looksLikeSecret = SecretDetector.LooksLikeSecretPath(file.Path);

            merged.Add(file.IsUntracked
                ? WithUntrackedCounts(repository, file, looksLikeSecret)
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
        };
    }

    private GitFileChange WithUntrackedCounts(
        RepositoryInfo repository,
        GitFileChange file,
        bool looksLikeSecret)
    {
        UntrackedFileMeasurer.Measurement measurement =
            untracked.Measure(Path.Combine(repository.Root, file.Path.Replace('/', Path.DirectorySeparatorChar)));

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
