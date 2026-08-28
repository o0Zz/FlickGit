using System.Collections.Concurrent;
using FlickGit.Git;
using FlickGit.Models;

namespace FlickGit.Repositories;

/// <summary>
/// Answers "is this path inside a repository, and where is its root?" — the question
/// every surface asks before it can show anything.
///
/// Backed by a short-TTL cache keyed by directory, per CLAUDE.md, "Repository
/// Detection". The cache is what makes right-clicking a subdirectory as cheap as
/// right-clicking the root: both normalise to one entry, one `rev-parse`.
/// </summary>
public sealed class RepositoryService(IGitProcessRunner git)
{
    /// <summary>
    /// 30 s, from CLAUDE.md. Long enough that a right-click, a popup and the commit
    /// window that follows share one `rev-parse`; short enough that a repository
    /// deleted or cloned behind the tool's back is noticed without a restart.
    /// </summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    private long _writeGeneration;

    /// <summary>
    /// The repository containing <paramref name="path"/>, or null when there is none.
    ///
    /// <paramref name="path"/> may be the root, any subdirectory, or a file inside the
    /// working tree — all three are normalised to the same root, which is what lets the
    /// Explorer background verb and the file verb share a cache entry.
    /// </summary>
    public async Task<RepositoryInfo?> ResolveAsync(string path, CancellationToken cancellationToken)
    {
        string? directory = ToDirectory(path);
        if (directory is null)
            return null;

        if (_cache.TryGetValue(directory, out CacheEntry entry) && !entry.IsStale)
            return entry.Repository;

        RepositoryInfo? resolved = await ProbeAsync(directory, cancellationToken).ConfigureAwait(false);

        //Misses are cached too. "Not a repository" is the answer for every folder on the
        //machine that is not one, and re-probing C:\Windows on every right-click there
        //would cost a process start for nothing.
        _cache[directory] = new CacheEntry(resolved, DateTime.UtcNow);

        if (resolved is not null)
            _cache[resolved.Root] = new CacheEntry(resolved, DateTime.UtcNow);

        return resolved;
    }

    /// <summary>
    /// How many times the tool has written to a repository.
    ///
    /// Every write path in the product already calls <see cref="Invalidate"/>, which makes this the
    /// one honest "something changed" signal in the codebase. Another cache with a longer memory —
    /// the palette's, which holds a status per repository — reads this to know its snapshot is stale
    /// without anybody having to remember a second invalidation call. A counter rather than an
    /// event, because a field is the boring mechanism and there is nothing to unsubscribe from.
    /// </summary>
    public long WriteGeneration => Interlocked.Read(ref _writeGeneration);

    /// <summary>
    /// Drops the cached answer for a repository, and every directory inside it. Called after any
    /// write the tool performs, per CLAUDE.md: "Invalidate on any write operation the tool performs."
    ///
    /// The separator is appended before comparing, which is the same trap
    /// <c>WorkingTreeWriter.ResolveInsideRepository</c> and <c>WorktreeService.IsInside</c> both
    /// guard: a bare <c>StartsWith</c> makes <c>C:\dev\repo2</c> count as inside <c>C:\dev\repo</c>,
    /// so committing in one repository threw away the other's entry. Only ever a wasted
    /// <c>rev-parse</c> rather than a wrong answer -- but the rule is the rule in three places now,
    /// and one of them quietly not following it is how it stops being one.
    /// </summary>
    public void Invalidate(string repositoryRoot)
    {
        Interlocked.Increment(ref _writeGeneration);

        string root = TrimTrailingSeparator(repositoryRoot);
        string inside = root + Path.DirectorySeparatorChar;

        foreach (string key in _cache.Keys)
        {
            if (string.Equals(key, root, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(inside, StringComparison.OrdinalIgnoreCase))
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    private async Task<RepositoryInfo?> ProbeAsync(string directory, CancellationToken cancellationToken)
    {
        //--show-toplevel gives the working-tree root; --is-bare-repository comes back in
        //the same invocation because a bare repository has no working tree to commit
        //into and every commit surface has to refuse it. One process, three answers.
        //
        //--absolute-git-dir is last on purpose. rev-parse answers in the order it was
        //asked, so appending rather than inserting leaves lines[0] and lines[1] meaning
        //what they have always meant -- and --absolute-git-dir rather than --git-dir,
        //which answers a bare ".git" when it happens to be run at the top level.
        GitResult result = await git.ReadAsync(
            directory,
            ["rev-parse", "--show-toplevel", "--is-bare-repository", "--absolute-git-dir"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            //A bare repository fails the whole invocation, because --show-toplevel has no answer there:
            //"fatal: this operation must be run in a work tree". Told apart from "not a repository at
            //all" by Git's own words, so the ordinary miss -- a right-click on any folder on the machine
            //-- still costs exactly one process. Without this IsBare is never true, and every
            //`error.bare` guard downstream is unreachable code behind a generic "not a repository".
            return result.StdErr.Contains("work tree", StringComparison.OrdinalIgnoreCase)
                ? await ProbeBareAsync(directory, cancellationToken).ConfigureAwait(false)
                : null;
        }

        string[] lines = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return null;

        //Git answers with forward slashes even on Windows. Normalising here means the
        //cache key, the window title, the registry command line and the file-system
        //calls all agree on one spelling.
        string root = NormaliseRoot(lines[0].Trim());
        if (root.Length == 0)
            return null;

        bool isBare = lines.Length > 1 && lines[1].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

        //A file-system probe, not `git submodule status`. CLAUDE.md, "Submodules": this
        //gate is checked before every submodule action, so it has to cost microseconds.
        bool hasSubmodules = File.Exists(Path.Combine(root, ".gitmodules"));

        //Normalised the same way the root is, and for the same reason: this path is handed
        //to File.Exists and Directory.Exists, which want the Windows spelling.
        string gitDirectory = lines.Length > 2 ? NormaliseRoot(lines[2].Trim()) : string.Empty;

        return new RepositoryInfo(root, Path.GetFileName(root), hasSubmodules, isBare, gitDirectory);
    }

    /// <summary>
    /// The second probe, reached only when the first failed for want of a working tree.
    ///
    /// <c>Root</c> is the Git directory, because in a bare repository that is the repository: there is
    /// no working tree for it to be anything else. Nothing commits into it -- every commit surface
    /// refuses a bare repository by name, which is the whole reason this has to be detectable.
    /// </summary>
    private async Task<RepositoryInfo?> ProbeBareAsync(string directory, CancellationToken cancellationToken)
    {
        GitResult result = await git.ReadAsync(
            directory,
            ["rev-parse", "--is-bare-repository", "--absolute-git-dir"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
            return null;

        string[] lines = result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        //Not bare after all: the first probe failed for some other reason, and inventing a repository
        //out of that would be worse than the generic refusal.
        if (lines.Length < 2 || !lines[0].Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
            return null;

        string gitDirectory = NormaliseRoot(lines[1].Trim());

        if (gitDirectory.Length == 0)
            return null;

        return new RepositoryInfo(
            gitDirectory,
            Path.GetFileName(gitDirectory),
            HasSubmodules: false,
            IsBare: true,
            gitDirectory);
    }

    /// <summary>
    /// The directory to run Git in: <paramref name="path"/> itself when it is one, its
    /// parent when it is a file.
    /// </summary>
    internal static string? ToDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0)
            return null;

        try
        {
            string full = Path.GetFullPath(trimmed);

            if (Directory.Exists(full))
                return TrimTrailingSeparator(full);

            if (File.Exists(full))
                return Path.GetDirectoryName(full) is { Length: > 0 } parent
                    ? TrimTrailingSeparator(parent)
                    : null;

            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            //Not a usable path. Callers show "not a repository", which is the right
            //answer for a path Windows itself will not resolve.
            return null;
        }
    }

    /// <summary>
    /// Git's <c>--show-toplevel</c> spelling turned into the Windows one: back slashes,
    /// drive letter upper-cased, no trailing separator.
    /// </summary>
    internal static string NormaliseRoot(string gitToplevel)
    {
        if (gitToplevel.Length == 0)
            return string.Empty;

        string windows = gitToplevel.Replace('/', Path.DirectorySeparatorChar);

        try
        {
            windows = Path.GetFullPath(windows);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            //Keep what Git said rather than losing the answer entirely.
        }

        windows = TrimTrailingSeparator(windows);

        //"c:\dev\repo" and "C:\dev\repo" are one directory, and the cache is
        //case-insensitive anyway; upper-casing the drive letter keeps the *displayed*
        //path stable no matter which spelling Explorer handed over.
        if (windows.Length >= 2 && windows[1] == ':')
            windows = char.ToUpperInvariant(windows[0]) + windows[1..];

        return windows;
    }

    private static string TrimTrailingSeparator(string path)
    {
        //A root directory ("C:\") keeps its separator -- removing it would leave "C:",
        //which Windows reads as "the current directory on drive C", not as the root.
        if (path.Length <= 3)
            return path;

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private readonly record struct CacheEntry(RepositoryInfo? Repository, DateTime ResolvedAtUtc)
    {
        public bool IsStale => DateTime.UtcNow - ResolvedAtUtc > CacheLifetime;
    }
}
