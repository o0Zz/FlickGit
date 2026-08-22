using FlickGit.Models;

namespace FlickGit.Status;

/// <summary>
/// Compares two status snapshots to answer one question: did anything the user selected change
/// under them?
///
/// This exists because of the order of operations in CLAUDE.md, "Branch Selector → Resolution on
/// commit". Committing to a different branch means switching first — and after a successful
/// switch, "the diff the user reviewed was computed against the <b>old</b> branch's HEAD".
///
/// So: refresh, compare, and "if any selected file's content or status changed as a result of
/// the switch, abort and show the refreshed list rather than committing something the user has
/// not seen."
///
/// A pure function over two snapshots, so the rule is testable without a repository, a branch or
/// a window.
/// </summary>
public static class StatusComparer
{
    /// <summary>
    /// The selected files whose status or line counts differ between the two snapshots, or which
    /// disappeared entirely.
    ///
    /// Only selected files are considered. A file the user did not tick changing is not a reason
    /// to stop — it is not going into the commit.
    /// </summary>
    public static IReadOnlyList<string> SelectedFilesThatChanged(
        RepositoryStatus before,
        RepositoryStatus after)
    {
        var afterByPath = after.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var changed = new List<string>();

        foreach (GitFileChange selected in before.Files.Where(f => f.IsSelected))
        {
            if (!afterByPath.TryGetValue(selected.Path, out GitFileChange? now))
            {
                //Gone from the list. The switch resolved it, or it is identical to the new
                //branch's version -- either way what the user reviewed is not what would be
                //committed.
                changed.Add(selected.Path);
                continue;
            }

            if (Differs(selected, now))
                changed.Add(selected.Path);
        }

        return changed;
    }

    /// <summary>
    /// Whether two snapshots of the same path describe the same pending change.
    ///
    /// Line counts are compared as well as status letters, because a file that is "modified"
    /// before and after a switch can be modified by an entirely different amount — the status
    /// letter alone would call that unchanged.
    /// </summary>
    private static bool Differs(GitFileChange before, GitFileChange after) =>
        before.IndexStatus != after.IndexStatus
        || before.WorkTreeStatus != after.WorkTreeStatus
        || before.AddedLines != after.AddedLines
        || before.RemovedLines != after.RemovedLines
        || before.IsBinary != after.IsBinary
        || !string.Equals(before.OldPath, after.OldPath, StringComparison.Ordinal);
}
