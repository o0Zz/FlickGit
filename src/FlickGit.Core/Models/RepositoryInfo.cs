namespace FlickGit.Models;

/// <summary>
/// A resolved repository. Produced by <see cref="Repositories.RepositoryService"/> and
/// cached, because every surface (menu, popup, palette, CLI) needs it before it can
/// decide what to show.
/// </summary>
/// <param name="Root">
/// Absolute path to the working-tree root, back-slashed and without a trailing
/// separator. Every path in the product is normalised to this, so that
/// right-clicking a subdirectory and right-clicking the root produce one cache entry
/// and one set of Git calls.
/// </param>
/// <param name="Name">The root's directory name — what the window title and the popup header show.</param>
/// <param name="HasSubmodules">
/// True when a <c>.gitmodules</c> file exists at the root. A file-system probe, never
/// `git submodule status`: CLAUDE.md, "Submodules". Gates every submodule action.
/// </param>
/// <param name="IsBare">A bare repository has no working tree, so nothing here can commit into it.</param>
public sealed record RepositoryInfo(
    string Root,
    string Name,
    bool HasSubmodules,
    bool IsBare)
{
    /// <summary>
    /// A repository that is not one, for a window built before anybody has asked about a folder.
    ///
    /// The resident service pre-warms the commit window at logon, long before the first right-click.
    /// Its view model needs *a* repository to construct, and the alternative is making every field
    /// nullable for a state that lasts until the first reset and is never displayed.
    /// </summary>
    public static RepositoryInfo None { get; } =
        new(string.Empty, string.Empty, HasSubmodules: false, IsBare: false);

    public override string ToString() => Root;
}
