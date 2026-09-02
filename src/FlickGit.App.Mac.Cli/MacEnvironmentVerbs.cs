using FlickGit.App.CommandLine;
using FlickGit.Cli;

namespace FlickGit.App.Mac;

/// <summary>
/// The environment verbs on macOS: five answered for real, five refused by name.
///
/// The five that work are not reimplemented here — they delegate to
/// <see cref="EnvironmentReports"/>, the same object the Windows host calls, so `flick language`
/// cannot list one set of languages on one platform and another set on the other.
/// <c>autostart</c> is the interesting one: it reads the same on both hosts and does something
/// completely different underneath, because <see cref="Settings.IAutostart"/> is a launchd
/// LaunchAgent here and a Scheduled Task there.
///
/// The five refused are the ones with no mechanism yet:
/// <list type="bullet">
/// <item><description><c>install-shell</c> and <c>install-overlay</c> — a Finder Sync extension,
/// which cannot be written in C# and ships inside the app bundle.</description></item>
/// <item><description><c>ai</c> — needs the credential store, which is Keychain here and is the one
/// seam still without a macOS implementation.</description></item>
/// <item><description><c>settings</c> — a window.</description></item>
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

    public Task<VerbResult> AiAsync(VerbOutput output, string? subcommand, string? action) =>
        throw new HostCapabilityException("ai");

    public Task<VerbResult> DoctorAsync(VerbOutput output) => throw new HostCapabilityException("diag doctor");

    public VerbResult Settings(VerbOutput output, SettingsTab tab = SettingsTab.General) =>
        throw new HostCapabilityException("settings");
}
