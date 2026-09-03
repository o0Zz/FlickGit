using FlickGit.Actions;
using FlickGit.Palette;

namespace FlickGit.App.ViewModels;

/// <summary>
/// One line in the palette, whatever the palette is currently listing.
///
/// Repositories, actions and branch completions share one row type because they share one list and
/// one selected index. Three collections and three selections would be three ways for the arrow keys
/// to disagree with what Enter does.
/// </summary>
/// <param name="Primary">The name, in normal weight: a repository, an action, a branch.</param>
/// <param name="Detail">Muted, beside it: "3 modified", or the command an action would run.</param>
/// <param name="Trailing">Right-aligned: "↑2 ↓4", or nothing.</param>
/// <param name="Command">
/// The exact command Enter would run, for the footer. Computed when the row is built rather than when
/// the footer is read: that is where the action and the repository are both already in hand, and it
/// leaves the footer with nothing to decide.
/// </param>
/// <param name="HasWork">Drives the bullet. Only ever true for a repository row.</param>
/// <param name="Repository">Set on a repository row. What Enter would act on.</param>
/// <param name="Action">Set on an action row.</param>
/// <param name="Parameter">Set on a completion row: the branch this row would pass to the action.</param>
public sealed record PaletteRow(
    string Primary,
    string Detail,
    string Trailing,
    string Command = "",
    bool HasWork = false,
    RepositoryOverview? Repository = null,
    GitAction? Action = null,
    string? Parameter = null);
