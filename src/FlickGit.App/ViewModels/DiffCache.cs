using FlickGit.Diff;
using FlickGit.Logging;
using FlickGit.Models;

namespace FlickGit.App.ViewModels;

/// <summary>
/// Diffs, cached and prefetched, with only one computation in flight.
///
/// Separated from the commit window's view model because it is a different concern with a different
/// lifetime: the view model is about what is on screen, this is about how fast it gets there.
/// CLAUDE.md, "Diff Viewer → Performance" asks for two things, and both live here:
///
/// <list type="bullet">
/// <item><description><b>Prefetch the top five files</b> as soon as the status resolves, so a click
/// on one of them is a cache hit inside the 80 ms budget rather than the 250 ms cold one.</description></item>
/// <item><description><b>Cancel the in-flight diff</b> when the selection moves on. Arrowing down a
/// long list otherwise leaves a queue of `git show` calls competing to paint a pane that has already
/// moved past them.</description></item>
/// </list>
///
/// The cache is keyed by path alone, so anything that changes a file's content has to
/// <see cref="Invalidate"/> it — a save, or a commit.
/// </summary>
public sealed class DiffCache(DiffService diffs, ILog log)
{
    private readonly Dictionary<string, SideBySideDiff> _cache = new(StringComparer.Ordinal);

    private CancellationTokenSource? _inFlight;

    private RepositoryInfo? _repository;

    /// <summary>Points the cache at a repository, discarding anything held for the previous one.</summary>
    public void Reset(RepositoryInfo repository)
    {
        Cancel();
        _cache.Clear();
        _repository = repository;
    }

    /// <summary>Drops one file, after a save or a commit changed it.</summary>
    public void Invalidate(string path) => _cache.Remove(path);

    /// <summary>Drops everything, after a commit moved HEAD under every diff.</summary>
    public void Clear() => _cache.Clear();

    /// <summary>Cancels the in-flight computation. Called when a window closes.</summary>
    public void Cancel()
    {
        _inFlight?.Cancel();
        _inFlight = null;
    }

    /// <summary>A cached diff for <paramref name="path"/>, or null when there is none.</summary>
    public SideBySideDiff? Cached(string path) =>
        _cache.TryGetValue(path, out SideBySideDiff? diff) ? diff : null;

    /// <summary>
    /// The diff for <paramref name="file"/>, computing it if it is not cached.
    /// </summary>
    /// <returns>
    /// The diff, or null when the computation was superseded by a newer selection or failed. A null
    /// return is not an error the caller should report: the reason is either "the user moved on" or
    /// already in the log.
    /// </returns>
    public async Task<SideBySideDiff?> GetAsync(GitFileChange file)
    {
        if (_repository is null)
            return null;

        if (Cached(file.Path) is { } cached)
            return cached;

        //A new token per request, and the previous one cancelled: only the newest selection is
        //worth computing.
        Cancel();

        var cancellation = new CancellationTokenSource();
        _inFlight = cancellation;

        try
        {
            SideBySideDiff diff = await diffs
                .ComputeAsync(_repository, file, cancellation.Token)
                .ConfigureAwait(true);

            if (cancellation.IsCancellationRequested)
                return null;

            _cache[file.Path] = diff;
            return diff;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            log.Warn($"Diff failed for {file.Path}: {ex.Message}");
            return null;
        }
        finally
        {
            if (_inFlight == cancellation)
                _inFlight = null;
        }
    }

    /// <summary>
    /// Fills the cache for <paramref name="files"/> in the background.
    ///
    /// Uncancelled and unreported: prefetch is an optimisation, so a file that fails here is simply
    /// computed again — and reported — when the user actually clicks it.
    /// </summary>
    public async Task PrefetchAsync(IReadOnlyList<GitFileChange> files)
    {
        if (_repository is null)
            return;

        foreach (GitFileChange file in files)
        {
            if (_cache.ContainsKey(file.Path))
                continue;

            try
            {
                _cache[file.Path] = await diffs
                    .ComputeAsync(_repository, file, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                log.Debug($"Prefetch failed for {file.Path}: {ex.Message}");
            }
        }
    }
}
