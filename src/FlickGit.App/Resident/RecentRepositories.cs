using System.IO;
using FlickGit.App.Settings;
using FlickGit.Models;

namespace FlickGit.App.Resident;

/// <summary>
/// The most-recently-used repository list, behind the tray menu's <i>Recent repositories</i>.
///
/// Kept because the product is built for someone working across five to ten repositories a day:
/// the tray is how they reach one when they are not already standing in its folder in Explorer.
/// Phase 5's palette scores by the same order — CLAUDE.md, "Repository Palette": "scored by
/// contiguity, word-boundary hits and MRU rank".
///
/// Stored in settings.json rather than a file of its own. It is small, it is per-user, and it wants
/// exactly the atomic write and schema versioning that file already has.
/// </summary>
public sealed class RecentRepositories(FlickSettings settings)
{
    /// <summary>
    /// How many to remember.
    ///
    /// Ten, because the product's stated audience works across "5–10 repositories per day" and a
    /// menu longer than that stops being a shortcut.
    /// </summary>
    private const int Capacity = 10;

    /// <summary>Most recent first. Entries whose directory has gone are dropped on the way out.</summary>
    public IReadOnlyList<string> Paths =>
        settings.RecentRepositories.Where(Directory.Exists).Take(Capacity).ToList();

    /// <summary>
    /// Moves <paramref name="repository"/> to the front.
    ///
    /// Called whenever a verb resolves a repository, which is the only honest definition of "used":
    /// it covers the context menu, the CLI and the tray alike.
    /// </summary>
    public void Remember(RepositoryInfo repository)
    {
        if (repository.Root.Length == 0)
            return;

        List<string> paths = settings.RecentRepositories;

        //Removed then inserted, so a repository used twice does not appear twice. Case-insensitive
        //because Windows paths are, even though Git's own paths are not.
        paths.RemoveAll(p => string.Equals(p, repository.Root, StringComparison.OrdinalIgnoreCase));
        paths.Insert(0, repository.Root);

        if (paths.Count > Capacity)
            paths.RemoveRange(Capacity, paths.Count - Capacity);

        settings.Save();
    }
}
