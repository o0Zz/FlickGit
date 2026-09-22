using System.Diagnostics;
using System.Globalization;
using FlickGit.Diagnostics;
using FlickGit.Git;
using FlickGit.Models;
using FlickGit.Status;

namespace FlickGit.History;

/// <summary>
/// Everything the log window asks Git: a page of commits, the files a range changed, and the range
/// as a patch file.
///
/// <b>This service only ever reads.</b> Every call goes through
/// <see cref="IGitProcessRunner.ReadAsync"/>, which is what makes that mechanical rather than a
/// promise -- and the one thing here that creates anything, <see cref="SavePatchAsync"/>, writes
/// at a path the user named in a dialog, outside the repository.
///
/// <b>Four of the five reads take a <c>relativePath</c>, and they take it together.</b> The log
/// window is either scoped to one file or it is not: a filtered commit list over an unfiltered diff
/// makes the gap disclosure lie, because <see cref="CommitRange.Resolve"/> counts what the user
/// skipped from the list it was handed. A commit that never touched the file contributes nothing to
/// a path-scoped diff and so is not a gap -- which is true exactly while the diff is scoped too.
/// </summary>
public sealed class HistoryService(IGitProcessRunner git, OperationTimings? timings = null)
{
    /// <summary>
    /// Commits per page. One `git log` process costs the same whether it emits 100 records or 200,
    /// and 100 rows is short enough that a maximised window on a tall display scrolls into a second
    /// Git call on the user's first flick of the wheel.
    /// </summary>
    public const int PageSize = 200;

    /// <summary>
    /// The flags every diff here carries, against the user's own gitconfig: <c>color.ui = always</c>
    /// would put ANSI escapes inside the field the parser reads, <c>diff.external</c> would replace
    /// the output entirely -- producing an empty file list and no error -- and a textconv filter would
    /// spawn a process per blob.
    ///
    /// <c>-M</c> is here for a different reason: rename detection is on by default, so it usually
    /// changes nothing, but it removes the possibility of the two parallel calls being configured
    /// differently <i>relative to each other</i>, which is the only way they can produce two file
    /// lists that disagree about whether a rename happened.
    /// </summary>
    private static readonly string[] DiffFlags = [.. GitDiffFlags.ReadSafe, "-M"];

    /// <summary>
    /// The trailing <c>-- :(literal)&lt;path&gt;</c>, or nothing at all when the read is not scoped.
    ///
    /// The <c>--</c> is what stops a file called <c>main</c> being read as a revision, and the magic
    /// prefix is what stops <c>report[final].xlsx</c> matching <c>reportf.xlsx</c> instead. Built
    /// here rather than by the caller so the pathspec never crosses out of Core: the window hands
    /// over a plain repository-relative path and cannot get the quoting wrong.
    /// </summary>
    private static string[] Scope(string? relativePath) =>
        relativePath is { Length: > 0 } path ? ["--", GitPathspec.Literal(path)] : [];

    /// <param name="relativePath">
    /// Repository-relative, forward slashes, as Git spells a path -- see
    /// <see cref="RepositoryInfo.Relative"/>. Null for the whole repository.
    /// </param>
    /// <param name="skip">
    /// How many commits to pass over. <c>--skip</c> rather than a "start from the last sha I saw"
    /// cursor, which is wrong twice: <c>&lt;sha&gt;^</c> does not resolve when the last row of a page
    /// is the root commit, and when it is a merge the caret silently switches the walk to the
    /// first-parent line, so the next page is a different set of commits.
    /// </param>
    public async Task<LogPage> GetPageAsync(
        RepositoryInfo repository,
        int skip,
        string? relativePath,
        CancellationToken cancellationToken)
    {
        long startedAt = Stopwatch.GetTimestamp();

        var args = new List<string>
        {
            "log",

            //Against the user's gitconfig, as above: log.decorate = full would make %D emit
            //"refs/heads/main" instead of "main", and color.decorate would put escapes in it.
            "--decorate=short",
            "--no-color",

            //One more than a page. The extra record is dropped and its presence *is* HasMore, so "Load more"
            //cannot appear on an exhausted history.
            $"--max-count={PageSize + 1}",
        };

        if (skip > 0)
            args.Add($"--skip={skip}");

        args.Add("--format=" + CommitLogParser.Format);

        //Last, because everything after `--` is a path.
        args.AddRange(Scope(relativePath));

        GitResult result = await git.ReadAsync(repository.Root, args, cancellationToken).ConfigureAwait(false);

        //An unborn HEAD exits non-zero with "does not have any commits yet". A fresh repository having
        //no history is a true answer, not a failure to report.
        if (!result.Succeeded)
            return LogPage.Empty;

        IReadOnlyList<LogCommit> commits = CommitLogParser.Parse(result.StdOut);

        timings?.Record("log.page", Stopwatch.GetElapsedTime(startedAt));

        return commits.Count > PageSize
            ? new LogPage([.. commits.Take(PageSize)], true)
            : new LogPage(commits, false);
    }

    /// <summary>
    /// How many commits HEAD has behind it, itself included -- the number a build stamps into its
    /// version, so a row in the log can be matched against a version that shipped.
    ///
    /// One process for the whole window rather than one per row. The rows count down from this by
    /// their position, which is exact while the history is linear and an over-estimate for a row
    /// above a merge. Counting each row exactly means walking the graph, which is not worth doing for
    /// a number in a gutter.
    ///
    /// Zero when there is nothing to count: an unborn HEAD exits non-zero, and the window shows no
    /// number rather than a wrong one.
    ///
    /// Scoped by <paramref name="relativePath"/> along with everything else, and it has to be: the
    /// rows count down from this by their position in the list, so a count of the whole history over
    /// a list holding only one file's commits numbers the top row after commits that are not in it.
    /// Scoped, the column keeps meaning what the row is -- the Nth change to this file.
    /// </summary>
    public async Task<int> GetCommitCountAsync(
        RepositoryInfo repository,
        string? relativePath,
        CancellationToken cancellationToken)
    {
        GitResult result = await git
            .ReadAsync(repository.Root, ["rev-list", "--count", "HEAD", .. Scope(relativePath)], cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded && int.TryParse(result.StdOut.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count)
            ? count
            : 0;
    }

    /// <summary>
    /// The files a range changed, with their line counts. Two reads in parallel and merged on path,
    /// exactly as <see cref="StatusService"/> does: <c>--name-status</c> has the letters and no
    /// counts, <c>--numstat</c> the counts and no letters. Both key on the post-image path, so a
    /// rename cannot key differently in the two streams.
    /// </summary>
    /// <param name="baseSpec">The left side. A bare object id, never revision syntax.</param>
    /// <param name="relativePath">Scopes both reads, or null for every file the range touched.</param>
    public async Task<IReadOnlyList<GitFileChange>> GetFilesAsync(
        RepositoryInfo repository,
        string baseSpec,
        string tipSpec,
        string? relativePath,
        CancellationToken cancellationToken)
    {
        long startedAt = Stopwatch.GetTimestamp();

        string[] scope = Scope(relativePath);

        Task<GitResult> namesTask = git.ReadAsync(
            repository.Root,
            ["diff", "--name-status", "-z", .. DiffFlags, baseSpec, tipSpec, .. scope],
            cancellationToken);

        Task<GitResult> countsTask = git.ReadAsync(
            repository.Root,
            ["diff", "--numstat", "-z", .. DiffFlags, baseSpec, tipSpec, .. scope],
            cancellationToken);

        GitResult names = await namesTask.ConfigureAwait(false);
        GitResult counts = await countsTask.ConfigureAwait(false);

        if (!names.Succeeded)
            return [];

        IReadOnlyDictionary<string, NameStatusEntry> statuses = NameStatusParser.Parse(names.StdOut);

        //A failed numstat is not fatal: the letters are the load-bearing half and a list without counts
        //is still a list.
        IReadOnlyDictionary<string, NumstatEntry> measured = counts.Succeeded
            ? NumstatParser.Parse(counts.StdOut)
            : new Dictionary<string, NumstatEntry>();

        var files = new List<GitFileChange>(statuses.Count);

        foreach (NameStatusEntry entry in statuses.Values)
        {
            measured.TryGetValue(entry.Path, out NumstatEntry? numbers);

            files.Add(new GitFileChange
            {
                Path = entry.Path,
                OldPath = entry.OldPath ?? numbers?.OldPath,

                //The range's letter goes on the working-tree side so DisplayStatus, SortRank and the row's letter
                //all come out right with nothing changed. There is no index in a historical diff, which is what
                //IndexStatus = None means.
                WorkTreeStatus = entry.Status,
                IndexStatus = GitChangeType.None,

                //Not "?? 0": a file in --name-status but absent from --numstat is uncounted, and null already
                //means "uncounted or binary" everywhere in the product. Zero would read as "nothing changed".
                AddedLines = numbers?.Added,
                RemovedLines = numbers?.Removed,
                IsBinary = numbers?.IsBinary ?? false,
            });
        }

        files.Sort(static (a, b) =>
        {
            int byRank = a.SortRank.CompareTo(b.SortRank);
            return byRank != 0 ? byRank : string.CompareOrdinal(a.Path, b.Path);
        });

        timings?.Record("log.range.files", Stopwatch.GetElapsedTime(startedAt));

        return files;
    }

    /// <summary>
    /// The commits in <paramref name="revisionRange"/>, newest first.
    ///
    /// Its own method rather than an argument on <see cref="GetPageAsync"/>, because it answers a
    /// different question: that one pages through a branch's whole history and this reads a bounded
    /// range in one call. There is nothing to page through -- a branch with more commits than
    /// <paramref name="maxCount"/> is one whose description will not be improved by reading the rest.
    /// </summary>
    public async Task<IReadOnlyList<LogCommit>> GetRangeAsync(
        RepositoryInfo repository,
        string revisionRange,
        int maxCount,
        CancellationToken cancellationToken)
    {
        GitResult result = await git.ReadAsync(
            repository.Root,
            [
                "log",
                "--decorate=short",
                "--no-color",
                $"--max-count={maxCount}",
                "--format=" + CommitLogParser.Format,
                revisionRange,
            ],
            cancellationToken).ConfigureAwait(false);

        //A range that resolves to nothing is a true answer -- the branch is level with its target. So is
        //a range naming a ref that does not exist yet, which is what a target branch the user has never
        //fetched looks like.
        return result.Succeeded ? CommitLogParser.Parse(result.StdOut) : [];
    }

    /// <summary>Writes the range's unified patch to <paramref name="destinationPath"/>.</summary>
    /// <remarks>
    /// <c>--output</c> rather than capturing stdout, and that is the whole reason this lives here
    /// rather than in the window: the patch never becomes a C# string, so a Latin-1 source file gets
    /// byte-exact bytes instead of U+FFFD. It also settles the BOM question -- a BOM in front of
    /// <c>diff --git</c> makes <c>git apply</c> refuse the file -- and the line-ending question.
    ///
    /// <see cref="IGitProcessRunner.ReadAsync"/> rather than <c>RunAsync</c>: what this does to the
    /// <i>repository</i> is a read.
    /// </remarks>
    public Task<GitResult> SavePatchAsync(
        RepositoryInfo repository,
        CommitRange range,
        string destinationPath,
        string? relativePath,
        CancellationToken cancellationToken) =>
        git.ReadAsync(
            repository.Root,
            [
                "diff",
                .. DiffFlags,

                //Without this a patch touching a binary file says "Binary files differ" and can be applied by
                //nothing.
                "--binary",

                $"--output={destinationPath}",

                range.BaseSpec,
                range.TipSpec,

                //The patch is the file list the window showed, so it carries the same scope. Unscoped here,
                //the button would hand somebody a patch of everything under a window titled after one file.
                .. Scope(relativePath),
            ],
            cancellationToken);
}
