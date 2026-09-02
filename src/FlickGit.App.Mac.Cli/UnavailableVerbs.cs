using System.Reflection;
using FlickGit.App.CommandLine;
using FlickGit.Cli;
using FlickGit.Models;

namespace FlickGit.App.Mac;

/// <summary>
/// The verbs this host cannot answer yet, refused by name.
///
/// Both interfaces are implemented together because the reason is the same for all of them and the
/// answer is one sentence. <see cref="IWindowVerbs"/> is every window, which this host does not
/// have. <see cref="IEnvironmentVerbs"/> is mostly the registry, the Task Scheduler and a settings
/// window, each of which has a macOS counterpart that is a different mechanism rather than a port.
///
/// <c>version</c> and <c>help</c> are answered rather than refused: they are the two verbs that have
/// to work on any host, including one that can do nothing else. Both come from the same place the
/// Windows build takes them from — <c>Verb.HelpText</c> is a constant in FlickGit.Core — so the two
/// hosts cannot drift about the grammar.
///
/// Every refusal is raised as a <see cref="HostCapabilityException"/> and reported by whichever
/// host boundary knows where the output goes. See that type for why it is not simply written here.
/// </summary>
public sealed class UnavailableVerbs : IEnvironmentVerbs, IWindowVerbs
{
    /// <summary>
    /// Always throws. Typed as <see cref="VerbResult"/> so the members below stay expressions rather
    /// than each growing a body to satisfy the compiler.
    /// </summary>
    private static VerbResult Refuse(string verb) => throw new HostCapabilityException(verb);

    // ---- IEnvironmentVerbs ---------------------------------------------------------------------

    public VerbResult Help(VerbOutput output)
    {
        output.Line(Verb.HelpText);

        return VerbResult.Exit(ExitCodes.Success);
    }

    public VerbResult Version(VerbOutput output)
    {
        output.Line($"FlickGit {Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown"}");

        return VerbResult.Exit(ExitCodes.Success);
    }

    /// <summary>The Explorer context menu. A Finder Sync extension here.</summary>
    public VerbResult ContextMenu(VerbOutput output, bool install) =>
        Refuse(install ? "install-shell" : "uninstall-shell");

    /// <summary>The repository badge. A Finder Sync badge here, with no registry and no slot limit.</summary>
    public Task<VerbResult> OverlayAsync(VerbOutput output, bool install, string? scope) =>
        Task.FromResult(Refuse(install ? "install-overlay" : "uninstall-overlay"));

    /// <summary>A logon task on Windows; a launchd LaunchAgent here.</summary>
    public VerbResult Autostart(VerbOutput output, string? switchTo) => Refuse("autostart");

    /// <summary>Needs the credential store, which is Keychain here rather than Credential Manager.</summary>
    public Task<VerbResult> AiAsync(VerbOutput output, string? subcommand, string? action) =>
        Task.FromResult(Refuse("ai"));

    public VerbResult Language(VerbOutput output, string? code) => Refuse("language");

    public Task<VerbResult> DoctorAsync(VerbOutput output) => Task.FromResult(Refuse("diag doctor"));

    public VerbResult Timings(VerbOutput output) => Refuse("diag timings");

    public VerbResult Settings(VerbOutput output, SettingsTab tab = SettingsTab.General) =>
        Refuse("settings");

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
