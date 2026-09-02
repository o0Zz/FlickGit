namespace FlickGit.App.Settings;

/// <summary>
/// Whether FlickGit starts with the session, and the two ways to change that.
///
/// A logon Scheduled Task on Windows and a <c>launchd</c> LaunchAgent on macOS: the same question,
/// two mechanisms with nothing in common but the answer. Both are asked rather than remembered —
/// CLAUDE.md, on the settings window: every value is read from its source of truth on open, because
/// a checkbox disagreeing with the system is worse than no checkbox.
///
/// <see cref="Enable"/> and <see cref="Disable"/> return a sentence as well as a flag because both
/// can fail for reasons only the mechanism knows, and the user has to be told which.
/// </summary>
public interface IAutostart
{
    /// <summary>Read from the scheduler itself, never from a stored flag.</summary>
    bool IsEnabled();

    (bool Succeeded, string Message) Enable();

    (bool Succeeded, string Message) Disable();
}
