namespace FlickGit.App.ViewModels;

/// <summary>
/// Where the quick-commit popup is in the trigger-Enter-done sequence.
///
/// An enum rather than a pair of flags because Esc means something different in each state, and
/// because the one transition that must never happen — committing without a message — is then a
/// single edge to guard rather than a condition scattered across handlers.
/// </summary>
public enum QuickCommitStage
{
    /// <summary>Nothing in flight. Enter commits, Esc dismisses.</summary>
    Idle,

    /// <summary>
    /// The AI is writing. Enter queues rather than refusing — CLAUDE.md: "do not block and do not
    /// refuse".
    /// </summary>
    Generating,

    /// <summary>
    /// Enter was pressed during generation. The commit fires the instant the message lands, and Esc
    /// still cancels until it does.
    /// </summary>
    Queued,

    /// <summary>
    /// <see cref="Commits.CommitFlow"/> is running. The point of no return: Esc does nothing,
    /// because there is nothing left to cancel that would not leave the repository half-changed.
    /// </summary>
    Committing,
}
