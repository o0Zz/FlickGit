using System.Reflection;
using FlickGit.App.CommandLine;
using FlickGit.Cli;
using FlickGit.Models;

namespace FlickGit.App.Mac;

/// <summary>
/// Every verb that opens a window, refused by name.
///
/// This host has no windows at all, so the refusal is the same sentence thirteen times and the
/// interface is implemented in one place rather than thirteen. When the Avalonia host arrives it
/// replaces this class outright; until then a window verb says what it is rather than doing nothing.
///
/// The environment verbs used to be refused here too. Half of them turned out to be portable and
/// now answer for real — see <see cref="MacEnvironmentVerbs"/>.
/// </summary>
public sealed class UnavailableVerbs : IWindowVerbs
{
    /// <summary>
    /// Always throws. Typed as <see cref="VerbResult"/> so the members below stay expressions rather
    /// than each growing a body to satisfy the compiler.
    /// </summary>
    private static VerbResult Refuse(string verb) => throw new HostCapabilityException(verb);

    // ---- IWindowVerbs --------------------------------------------------------------------------

    public Task<VerbResult> CommitAsync(VerbOutput output, RepositoryInfo repository) =>
        Task.FromResult(Refuse("commit"));

    public Task<VerbResult> PaletteAsync() => Task.FromResult(Refuse("palette"));

    public Task<VerbResult> LogAsync(RepositoryInfo repository) => Task.FromResult(Refuse("log"));

    public Task<VerbResult> BlameAsync(VerbOutput output, RepositoryInfo repository, string path) =>
        Task.FromResult(Refuse("blame"));

    public Task<VerbResult> PullRequestAsync(RepositoryInfo repository) => Task.FromResult(Refuse("pr"));

    public Task<VerbResult> PullAsync(RepositoryInfo repository) => Task.FromResult(Refuse("pull-rebase"));

    public Task<VerbResult> SwitchPickerAsync(RepositoryInfo repository) =>
        Task.FromResult(Refuse("switch"));

    public VerbResult TagPicker(RepositoryInfo repository) => Refuse("tag");

    public VerbResult StashPicker(RepositoryInfo repository) => Refuse("stash");

    public VerbResult Submodules(RepositoryInfo repository) => Refuse("submodule");

    public VerbResult Repo(RepositoryInfo repository) => Refuse("repo");

    public VerbResult Clone(string path, string? url) => Refuse("clone");

    public VerbResult Terminal(VerbOutput output, string? path) => Refuse("terminal");
}
