using FlickGit.Logging;
using FlickGit.Repositories;

namespace FlickGit.Palette;

/// <summary>
/// Finds repositories under the configured scan roots. Runs no Git at all.
///
/// A working tree is a directory containing <c>.git</c>, which is a file-system question — so this
/// answers it with <c>Directory.Exists</c> rather than with <c>rev-parse</c> per candidate. The
/// palette's whole promise is that it paints before anything blocks, and a process per directory on
/// a scan root holding thirty repositories is the one thing that would break it.
/// </summary>
public sealed class RepositoryScanner(ILog log)
{
    /// <summary>
    /// How far below a scan root to look.
    ///
    /// Three levels reaches <c>C:\dev\repo</c>, <c>C:\dev\client\repo</c> and
    /// <c>C:\dev\client\team\repo</c>, which is how people actually arrange work. Deeper turns a
    /// scan root of <c>C:\</c> into a full-disk walk, and the answer to "my repositories are eight
    /// levels down" is to name a closer scan root.
    /// </summary>
    private const int MaxDepth = 3;

    /// <summary>
    /// Directories never entered, because no repository lives inside one and they are enormous.
    ///
    /// Anything beginning with a dot is skipped separately, which covers <c>.vs</c>, <c>.idea</c>
    /// and the rest without naming them.
    /// </summary>
    private static readonly string[] NeverEntered =
        ["node_modules", "bin", "obj", "packages", "target", "dist", "build", "venv", "__pycache__"];

    /// <summary>
    /// Every repository found under <paramref name="roots"/>, in the order the roots were given.
    ///
    /// A repository's own subdirectories are not searched: a submodule belongs to its parent, and
    /// listing both would put two rows in the palette for one thing the user thinks of as one thing.
    /// </summary>
    public IReadOnlyList<string> Scan(IEnumerable<string> roots, CancellationToken cancellationToken)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(PathComparison.Comparer);

        foreach (string root in roots)
        {
            if (root.Length == 0 || !Directory.Exists(root))
                continue;

            Walk(root, depth: 0, found, seen, cancellationToken);
        }

        return found;
    }

    private void Walk(string directory, int depth, List<string> found, HashSet<string> seen, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (IsWorkingTree(directory))
        {
            if (seen.Add(directory))
                found.Add(directory);

            return;
        }

        if (depth >= MaxDepth)
            return;

        string[] children;

        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            //A permission-denied or offline directory is not an error worth surfacing: the palette
            //shows the repositories it could reach, which is every one the user can actually use.
            log.Debug($"Not scanning {directory}: {ex.Message}");
            return;
        }

        foreach (string child in children)
        {
            string name = Path.GetFileName(child);

            if (name.StartsWith('.') || NeverEntered.Contains(name, StringComparer.OrdinalIgnoreCase))
                continue;

            //Junctions and symlinks, which is how a scan of C:\ turns into an infinite one.
            try
            {
                if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                    continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Walk(child, depth + 1, found, seen, cancellationToken);
        }
    }

    /// <summary>
    /// True when <paramref name="directory"/> is the root of a working tree.
    ///
    /// <c>.git</c> is a directory in the ordinary case and a <i>file</i> in a linked worktree or a
    /// submodule, so both are accepted — a worktree the user is working in is a repository as far as
    /// committing is concerned.
    /// </summary>
    private static bool IsWorkingTree(string directory)
    {
        string dotGit = Path.Combine(directory, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }
}
