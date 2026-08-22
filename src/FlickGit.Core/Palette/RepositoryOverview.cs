using FlickGit.Models;

namespace FlickGit.Palette;

/// <summary>
/// One row of the palette: a repository and what there is to do in it.
///
/// Deliberately not a <c>RepositoryStatus</c>. The palette shows counts, not files, and holding a
/// full status per repository would keep every <c>GitFileChange</c> of every scanned repository alive
/// for the life of the resident service — against an 80 MB idle working-set target.
/// </summary>
/// <param name="Repository">Enough to hand straight to any verb the palette can run.</param>
/// <param name="Branch">Null in an unborn or detached repository, which the palette shows as-is.</param>
/// <param name="Changed">Tracked modifications — what the mock-up's "3 modified" counts.</param>
/// <param name="Untracked">Shown separately, because the staging default excludes them.</param>
/// <param name="Ahead">Commits to push. Zero when there is no upstream.</param>
/// <param name="Behind">Commits to pull.</param>
/// <param name="Failed">
/// True when the status could not be read at all — a repository mid-clone, a drive that went away.
/// The row stays, because removing it would make a broken repository invisible rather than obvious.
/// </param>
public sealed record RepositoryOverview(
    RepositoryInfo Repository,
    string? Branch,
    int Changed,
    int Untracked,
    int Ahead,
    int Behind,
    bool Failed)
{
    public string Name => Repository.Name;

    public string Root => Repository.Root;

    /// <summary>
    /// Whether this repository has anything the user would want to act on.
    ///
    /// This is the whole ordering rule of the palette. CLAUDE.md: it "opens on repositories that
    /// have something to do, not on a command list" — so an untracked file counts, because a new
    /// file nobody has committed is exactly the thing a person forgets.
    /// </summary>
    public bool HasWork => Changed > 0 || Untracked > 0 || Ahead > 0 || Behind > 0;
}
