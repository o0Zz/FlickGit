using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;
using FlickGit.Status;

namespace FlickGit.Palette;

/// <summary>
/// The palette's list of repositories and what there is to do in each, held between openings.
///
/// <b>This cache is the reason the palette can meet its 80 ms budget.</b> CLAUDE.md: "Render from
/// cache <i>synchronously on open</i>, then refresh asynchronously and update in place. Never wait on
/// a `git` process before showing." So <see cref="Snapshot"/> never blocks and never awaits — it
/// returns whatever was last read, including nothing at all on the very first open — and
/// <see cref="RefreshAsync"/> is what the caller starts afterwards.
/// </summary>
public sealed class RepositoryOverviewCache(
    RepositoryScanner scanner,
    RepositoryService repositories,
    StatusService status,
    ILog log)
{
    /// <summary>5 s, from CLAUDE.md. Long enough that two openings in a row cost one scan.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How many repositories are read at once.
    ///
    /// Each one costs three Git processes, so thirty repositories unthrottled is ninety processes
    /// competing for the same disk — slower than doing it in batches, and enough to make the machine
    /// audibly unhappy. Capped rather than configurable: the right number is "about as many as there
    /// are cores", which the machine already knows.
    /// </summary>
    private static readonly int Concurrency = Math.Clamp(Environment.ProcessorCount, 2, 8);

    private IReadOnlyList<RepositoryOverview> _snapshot = [];
    private DateTime _readAtUtc = DateTime.MinValue;
    private long _readAtGeneration = -1;

    /// <summary>
    /// The last list read. Never blocks; empty until the first refresh completes.
    /// </summary>
    public IReadOnlyList<RepositoryOverview> Snapshot => _snapshot;

    /// <summary>
    /// Whether <see cref="Snapshot"/> is worth re-reading.
    ///
    /// Two reasons it can be: the 5 s window has passed, or the tool has written to a repository
    /// since — which <see cref="RepositoryService.WriteGeneration"/> reports without the palette
    /// having to be told. CLAUDE.md requires both: "Cache TTL 5 s, invalidated on any write the tool
    /// performs."
    /// </summary>
    public bool IsStale =>
        DateTime.UtcNow - _readAtUtc > Lifetime || _readAtGeneration != repositories.WriteGeneration;

    /// <summary>
    /// Re-reads every repository, and replaces the snapshot with the result.
    ///
    /// <paramref name="recent"/> comes first and is kept even when it is not under a scan root: a
    /// repository the user worked in an hour ago belongs in this list whether or not they have told
    /// the tool where to look.
    /// </summary>
    public async Task<IReadOnlyList<RepositoryOverview>> RefreshAsync(
        IReadOnlyList<string> scanRoots,
        IReadOnlyList<string> recent,
        CancellationToken cancellationToken)
    {
        //Read before the work, not after: a write that lands *during* the refresh must leave the
        //result stale rather than being stamped as current.
        long generation = repositories.WriteGeneration;

        var roots = new List<string>();
        var seen = new HashSet<string>(PathComparison.Comparer);

        foreach (string root in recent.Concat(scanner.Scan(scanRoots, cancellationToken)))
        {
            if (seen.Add(root))
                roots.Add(root);
        }

        var overviews = new RepositoryOverview[roots.Count];

        await Parallel.ForAsync(
            0,
            roots.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = cancellationToken },
            async (index, token) => overviews[index] = await ReadAsync(roots[index], token).ConfigureAwait(false))
            .ConfigureAwait(false);

        _snapshot = overviews;
        _readAtUtc = DateTime.UtcNow;
        _readAtGeneration = generation;

        return _snapshot;
    }

    private async Task<RepositoryOverview> ReadAsync(string root, CancellationToken cancellationToken)
    {
        //Built rather than resolved. `rev-parse` would be a fourth process to re-establish what
        //finding `.git` in this directory already proved, and the fields it would add are a
        //file-system probe and a flag no scanned working tree can have set.
        //
        //The Git directory is composed rather than read, which is the one place in the product that
        //guesses it: it is right for an ordinary clone and wrong for a worktree, where `.git` is a
        //file. The palette shows no merge state, so the cost of being wrong is a probe that finds
        //nothing -- and paying a process per scanned repository to be right would defeat the scan.
        var repository = new RepositoryInfo(
            root,
            Path.GetFileName(root),
            HasSubmodules: File.Exists(Path.Combine(root, ".gitmodules")),
            IsBare: false,
            GitDirectory: Path.Combine(root, ".git"));

        try
        {
            RepositoryStatus read = await status.GetStatusAsync(repository, cancellationToken).ConfigureAwait(false);

            return new RepositoryOverview(
                repository,
                read.Branch,
                read.TrackedChangeCount,
                read.UntrackedCount,
                read.Ahead,
                read.Behind,
                Failed: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            //One unreadable repository must not empty the palette. It keeps its row, marked, which
            //is how the user finds out that the drive holding it is offline.
            log.Debug($"Could not read {root} for the palette: {ex.Message}");
            return new RepositoryOverview(repository, null, 0, 0, 0, 0, Failed: true);
        }
    }
}
