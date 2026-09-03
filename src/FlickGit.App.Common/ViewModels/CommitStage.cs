namespace FlickGit.App.ViewModels;

/// <summary>
/// Where the commit window is in the type-Enter-done sequence.
///
/// A state rather than a pair of bools because Enter and Esc each mean something different in every
/// one of them: queue the commit, cancel the queue, or nothing at all.
/// </summary>
public enum CommitStage
{
    /// <summary>Nothing in flight. Enter commits.</summary>
    Idle,

    /// <summary>A message is streaming in. Enter queues rather than refusing.</summary>
    Generating,

    /// <summary>Enter was pressed during generation. The commit fires when the message lands.</summary>
    Queued,

    /// <summary>The commit is running. Past the point where Esc can take it back.</summary>
    Committing,
}
