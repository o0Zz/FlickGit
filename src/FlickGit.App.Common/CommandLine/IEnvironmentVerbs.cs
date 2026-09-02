using FlickGit.Cli;

namespace FlickGit.App.CommandLine;

/// <summary>
/// The verbs that answer about the installation rather than about a repository, behind an interface
/// for the same reason <see cref="IWindowVerbs"/> is: <see cref="VerbRunner"/> routes them, and half
/// of them are Windows to the bone.
///
/// The split inside this family is the interesting part. <c>Help</c>, <c>Version</c>,
/// <c>Language</c>, <c>Timings</c> and most of <c>Ai</c> are portable and read the same on any host.
/// <c>ContextMenu</c> and <c>Overlay</c> are the registry, <c>Autostart</c> is the Task Scheduler,
/// and <c>Settings</c> opens a window — each has a macOS counterpart that is a different mechanism
/// rather than a port, so the interface names the question and lets the host answer it.
/// </summary>
public interface IEnvironmentVerbs
{
    VerbResult Help(VerbOutput output);

    VerbResult Version(VerbOutput output);

    VerbResult ContextMenu(VerbOutput output, bool install);

    Task<VerbResult> OverlayAsync(VerbOutput output, bool install, string? scope);

    VerbResult Autostart(VerbOutput output, string? switchTo);

    Task<VerbResult> AiAsync(VerbOutput output, string? subcommand, string? action);

    VerbResult Language(VerbOutput output, string? code);

    Task<VerbResult> DoctorAsync(VerbOutput output);

    VerbResult Timings(VerbOutput output);

    VerbResult Settings(VerbOutput output, SettingsTab tab = SettingsTab.General);
}
