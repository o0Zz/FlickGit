namespace FlickGit.App.CommandLine;

/// <summary>
/// Which page <c>flick settings</c> opens on. Part of the command-line grammar rather than of the
/// window, which is why it lives here and not beside the WPF one that renders it.
/// </summary>
public enum SettingsTab
{
    General,
    Help,
    About,
}
