using FlickGit.Cli;

namespace FlickGit.App.CommandLine;

/// <summary>
/// The notification area: the way to tell the user something without costing them a click.
///
/// Separate from <see cref="IDialogs"/> on purpose, and the split is CLAUDE.md's own rule rather
/// than a tidying instinct — "a window is for a *question* or a *failure*; everything else is a
/// notification". Two interfaces keep that distinction visible at every call site, where one
/// combined surface would let an ordinary outcome quietly acquire a button to press.
///
/// Implemented on Windows by the tray icon and on macOS by a user notification.
/// </summary>
public interface INotifier
{
    /// <summary>
    /// False when there is nothing to speak through — a one-shot launch with no resident service.
    /// Callers fall back to <see cref="IDialogs.Notice"/>, because the resident service is an
    /// optimisation and an outcome must not become invisible without it.
    /// </summary>
    bool CanNotify { get; }

    /// <summary>An outcome the user needs to see. Never gated by a setting.</summary>
    void Show(string title, string message);

    /// <summary>
    /// The commit celebration, and only that — gated by <c>ShowSuccessNotification</c>. Where a
    /// notification is the *only* report of an outcome, use <see cref="Show"/> instead: suppressing
    /// it would leave the operation with nothing to show for itself.
    /// </summary>
    void Success(string title, string message);
}
