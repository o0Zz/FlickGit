namespace FlickGit.App.Infrastructure;

/// <summary>
/// What a delete did, or why it did not happen.
///
/// <paramref name="Message"/> is null on success — and also on the one failure that has already
/// reported itself: the shell puts up its own error when it cannot remove a file, in the system's
/// words about a system operation, and a second dialog paraphrasing it would be worse than none.
/// </summary>
public sealed record DeleteOutcome(bool Succeeded, string? Message)
{
    public static DeleteOutcome Ok() => new(true, null);

    public static DeleteOutcome Refused(string? message) => new(false, message);
}

/// <summary>
/// Puts a path where the user can get it back.
///
/// <b>This is the only route by which FlickGit removes anything from the working tree, and the
/// reason it exists at all.</b> An untracked file is uncommitted work Git has never seen, so
/// <c>git restore</c> cannot bring it back and the system's own bin is the only thing that can. The
/// Recycle Bin on Windows, the Trash on macOS — and in both cases what matters is that the undo is a
/// gesture the user already knows, which is what lets Delete ask nothing.
///
/// <b>It is the bin that deletes, never Git.</b> <c>flick delete</c> pairs this with
/// <c>TrackingService.UntrackAsync</c> so the removal arrives staged, and that call still carries
/// <c>--cached</c> — which is how CLAUDE.md's rule holds while a file genuinely goes: no <c>git rm</c>
/// FlickGit issues can reach the working tree, and what does reach it is recoverable.
///
/// <c>RestoreService.RevertAsync</c> takes the binned state as a <i>parameter</i> and refuses
/// without it, so FlickGit.Core enforces this precondition without being able to reach a bin
/// itself — which is why an implementation here needs no Core change at all.
/// </summary>
public interface ITrash
{
    /// <summary>
    /// Sends one path inside the repository to the bin — a file, or a folder and everything under it.
    ///
    /// The path is resolved against the root and refused if it escapes, and a symlink or junction is
    /// refused outright: following one would delete whatever it points at, somewhere the user never
    /// named.
    /// </summary>
    DeleteOutcome Delete(string repositoryRoot, string relativePath);
}
