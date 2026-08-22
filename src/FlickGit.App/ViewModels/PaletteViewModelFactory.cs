using FlickGit.Actions;
using FlickGit.App.Resident;
using FlickGit.App.Settings;
using FlickGit.Branches;
using FlickGit.Logging;
using FlickGit.Palette;

namespace FlickGit.App.ViewModels;

/// <summary>
/// Assembles one <see cref="PaletteViewModel"/>.
///
/// Same reason as the other two factories: a view model is per-window state rather than a service,
/// so it is not in the container, but the class owning the palette's lifecycle should not carry five
/// services just to pass them along.
/// </summary>
public sealed class PaletteViewModelFactory(
    ActionCatalog catalog,
    RepositoryOverviewCache overviews,
    BranchService branches,
    RecentRepositories recent,
    FlickSettings settings,
    ILog log)
{
    public PaletteViewModel Create() => new(catalog, overviews, branches, recent, settings, log);
}
