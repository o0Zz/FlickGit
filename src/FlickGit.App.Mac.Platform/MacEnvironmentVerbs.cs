using FlickGit.App.CommandLine;
using FlickGit.Cli;

namespace FlickGit.App.Mac;

/// <summary>
/// The environment verbs on macOS: six answered for real, four refused by name.
///
/// The five that work are not reimplemented here — they delegate to
/// <see cref="EnvironmentReports"/>, the same object the Windows host calls, so `flick language`
/// cannot list one set of languages on one platform and another set on the other.
/// <c>autostart</c> is the interesting one: it reads the same on both hosts and does something
/// completely different underneath, because <see cref="Settings.IAutostart"/> is a launchd
/// LaunchAgent here and a Scheduled Task there.
///
/// The refused ones are those with no mechanism yet:
/// <list type="bullet">
/// <item><description><c>install-shell</c> and <c>install-overlay</c> — a Finder Sync extension,
/// which cannot be written in C# and ships inside the app bundle.</description></item>
/// <item><description><c>diag doctor</c> — most of what it reports is the registry, the overlay slot
/// and the input trigger. A macOS doctor is a different report rather than a port of this one, and
/// writing half of it would be worse than saying so.</description></item>
/// </list>
/// </summary>
public sealed class MacEnvironmentVerbs(EnvironmentReports reports) : IEnvironmentVerbs
{
    public VerbResult Help(VerbOutput output) => reports.Help(output);

    public VerbResult Version(VerbOutput output) => reports.ReportVersion(output);

    public VerbResult Autostart(VerbOutput output, string? switchTo) =>
        reports.Autostart(output, switchTo);

    public VerbResult Language(VerbOutput output, string? code) => reports.Language(output, code);

    public VerbResult Timings(VerbOutput output) => reports.Timings(output);

    public VerbResult ContextMenu(VerbOutput output, bool install) =>
        throw new HostCapabilityException(install ? "install-shell" : "uninstall-shell");

    public Task<VerbResult> OverlayAsync(VerbOutput output, bool install, string? scope) =>
        throw new HostCapabilityException(install ? "install-overlay" : "uninstall-overlay");

    /// <summary>
    /// `flick ai` and `flick ai key [set|clear]`, out of the same shared reports the Windows host
    /// calls. The Keychain answers the store and an Avalonia window answers the prompt, which were
    /// the only two reasons this verb was ever refused here.
    /// </summary>
    public Task<VerbResult> AiAsync(VerbOutput output, string? subcommand, string? action) =>
        reports.AiAsync(output, subcommand, action);

    public Task<VerbResult> DoctorAsync(VerbOutput output) => throw new HostCapabilityException("diag doctor");

    /// <summary>
    /// The settings window.
    ///
    /// <b>Answered by the host that owns the windows, not here.</b> This project has no UI toolkit
    /// reference on purpose — it is the launchd, Trash and socket half — so the GUI host supplies
    /// the callback and a headless run keeps the refusal it always had.
    /// </summary>
    public VerbResult Settings(VerbOutput output, SettingsTab tab = SettingsTab.General)
    {
        if (OpenSettings is null)
            throw new HostCapabilityException("settings");

        OpenSettings(tab);

        return VerbResult.Stay();
    }

    /// <summary>
    /// Opens the settings window, set by the GUI host at startup.
    ///
    /// A property rather than a constructor parameter, because the window layer is built after this:
    /// the container resolves the verb router before any window type is touched.
    /// </summary>
    public Action<SettingsTab>? OpenSettings { get; set; }
}
