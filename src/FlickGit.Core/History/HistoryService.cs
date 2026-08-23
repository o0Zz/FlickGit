using System.Diagnostics;
using FlickGit.Diagnostics;
using FlickGit.Git;
using FlickGit.Models;
using FlickGit.Status;

namespace FlickGit.History;

/// <summary>
/// Everything the log window asks Git: a page of commits, the files a range changed, and the
/// range as a patch file.
///
/// Named History rather than Log because <c>ILog log</c> is injected into half the product, and a
/// <c>LogService</c> beside it is a collision waiting to happen.
///
/// <b>This service only ever reads.</b> The log window offers no checkout, reset, revert,
/// cherry-pick or tag, and the one thing here that creates anything —
/// <see cref="SavePatchAsync"/> — writes a file at a path the user named in a dialog, outside the
/// repository. Every call goes through <see cref="IGitProcessRunner.ReadAsync"/>, which is what
/// makes "read-only" mechanical rather than a promise.
/// </summary>
public sealed class HistoryService(IGitProcessRunner git, OperationTimings? timings = null)
{
    /// <summary>
    /// Commits per page.
    ///
    /// One `git log` process costs the same whether it emits 100 records or 200, 200 records is
    /// some 40 KB of stdout, and 100 rows is short enough that a maximised window on a tall
    /// display scrolls into a second Git call on the user's first flick of the wheel.
    /// </summary>
    public const int PageSize = 200;

    /// <summary>
    /// The flags every diff here carries, against the user's own gitconfig.
    ///
    /// The same argument CLAUDE.md makes for the AI payload, and it applies with more force to a
    /// file list: <c>color.ui = always</c> would put ANSI escapes inside the field the parser
    /// reads, <c>diff.external</c> would replace the output entirely — producing an empty file
    /// list and no error — and a textconv filter would spawn a process per blob.
    ///
    /// <c>-M</c> is here for a different reason: rename detection is on by default, so this
    /// usually changes nothing, but it removes the possibility of the two parallel calls being
    /// configured differently <i>relative to each other</i>, which is the only way they can
    /// produce two file lists that disagree about whether a rename happened.
    /// </summary>
    private static readonly string[] DiffFlags = ["--no-color", "--no-ext-diff", "--no-textconv", "-M"];

    /// <summary>
    /// One page of history, newest first.
    /// </summary>
    /// <param name="skip">
    /// How many commits to pass over. <c>--skip</c> rather than a "start from the last sha I saw"
    /// cursor, which is wrong twice: <c>&lt;sha&gt;^</c> does not resolve when the last row of a
    /// page is the root commit, and when it is a merge the caret silently switches the walk to the
    /// first-parent line, so the next page is a different set of commits from the one the user was
    /// scrolling through.
    /// </param>
    public async Task<LogPage> GetPageAsync(
        RepositoryInfo repository,
        int skip,
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

            //One more than a page. The extra record is dropped and its presence *is* HasMore, so
            //"Load more" cannot appear on an exhausted history.
            $"--max-count={PageSize + 1}",
        };

        if (skip > 0)
            args.Add($"--skip={skip}");

        args.Add("--format=" + CommitLogParser.Format);

        GitResult result = await git.ReadAsync(repository.Root, args, cancellationToken).ConfigureAwait(false);

        //An unborn HEAD exits non-zero with "does not have any commits yet". A fresh repository
        //having no history is a true answer, not a failure to report.
        if (!result.Succeeded)
            return LogPage.Empty;

        IReadOnlyList<LogCommit> commits = CommitLogParser.Parse(result.StdOut);

        timings?.Record("log.page", Stopwatch.GetElapsedTime(startedAt));

        return commits.Count > PageSize
            ? new LogPage([.. commits.Take(PageSize)], true)
            : new LogPage(commits, false);
    }

    /// <summary>
    /// The files a range changed, with their line counts.
    ///
    /// Two reads in parallel and merged on path, exactly as <see cref="StatusService"/> does:
    /// <c>--name-status</c> has the letters and no counts, <c>--numstat</c> has the counts and no
    /// letters. Both key on the post-image path, so a rename cannot key differently in the two
    /// streams.
    /// </summary>
    public async Task<IReadOnlyList<GitFileChange>> GetFilesAsync(
        RepositoryInfo repository,
        CommitRange range,
        CancellationToken cancellationToken)
    {
        long startedAt = Stopwatch.GetTimestamp();

        Task<GitResult> namesTask = git.ReadAsync(
            repository.Root,
            ["diff", "--name-status", "-z", .. DiffFlags, range.BaseSpec, range.TipSpec],
            cancellationToken);

        Task<GitResult> countsTask = git.ReadAsync(
            repository.Root,
            ["diff", "--numstat", "-z", .. DiffFlags, range.BaseSpec, range.TipSpec],
            cancellationToken);

        GitResult names = await namesTask.ConfigureAwait(false);
        GitResult counts = await countsTask.ConfigureAwait(false);

        if (!names.Succeeded)
            return [];

        IReadOnlyDictionary<string, NameStatusEntry> statuses = NameStatusParser.Parse(names.StdOut);

        //A failed numstat is not fatal, for the reason StatusService gives: the letters are the
        //load-bearing half and a list without counts is still a list.
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

                //The range's letter goes on the working-tree side so DisplayStatus, SortRank and
                //the row's letter all come out right with nothing changed. There is no index in a
                //historical diff, which is exactly what IndexStatus = None means.
                WorkTreeStatus = entry.Status,
                IndexStatus = GitChangeType.None,

                //Not "?? 0": a file in --name-status but absent from --numstat is uncounted, and
                //null already means "uncounted or binary" everywhere in the product. Zero would
                //read as "nothing changed".
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
    /// Writes the range's unified patch to <paramref name="destinationPath"/>.
    /// </summary>
    /// <remarks>
    /// <c>--output</c> rather than capturing stdout and writing it ourselves, and that is the
    /// whole reason this lives here rather than in the window: the patch never becomes a C#
    /// string, so a repository holding a Latin-1 source file gets byte-exact bytes instead of ones
    /// replaced by U+FFFD on the way through <see cref="GitResult.StdOut"/>. It also settles the
    /// BOM question — a BOM in front of <c>diff --git</c> makes <c>git apply</c> refuse the file —
    /// and the line-ending question, neither of which we then have to have an opinion about.
    ///
    /// <see cref="IGitProcessRunner.ReadAsync"/> rather than <c>RunAsync</c>: what this does to the
    /// <i>repository</i> is a read, and the file it creates is outside it, at a path the user just
    /// chose in a dialog.
    /// </remarks>
    public Task<GitResult> SavePatchAsync(
        RepositoryInfo repository,
        CommitRange range,
        string destinationPath,
        CancellationToken cancellationToken) =>
        git.ReadAsync(
            repository.Root,
            [
                "diff",
                .. DiffFlags,

                //Without this a patch touching a binary file says "Binary files differ" and can be
                //applied by nothing. A patch that does not apply is not a patch.
                "--binary",

                $"--output={destinationPath}",

                range.BaseSpec,
                range.TipSpec,
            ],
            cancellationToken);
}
