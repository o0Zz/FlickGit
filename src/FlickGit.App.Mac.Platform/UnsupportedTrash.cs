using FlickGit.App.Infrastructure;

namespace FlickGit.App.Mac;

/// <summary>
/// <see cref="ITrash"/> where there is no bin to reach.
///
/// Refuses rather than falling back to a plain delete, and that is the whole point of it: the only
/// caller is the one path that removes a file Git has never seen, where the bin *is* the undo. A
/// fallback that deleted the file outright would turn "put this where I can get it back" into
/// "destroy this", which is the one thing CLAUDE.md's Safety Rules never permit implicitly.
/// </summary>
public sealed class UnsupportedTrash : ITrash
{
    public DeleteOutcome Delete(string repositoryRoot, string relativePath) =>
        DeleteOutcome.Refused("Moving a file to the Trash is only available on macOS in this build.");
}
