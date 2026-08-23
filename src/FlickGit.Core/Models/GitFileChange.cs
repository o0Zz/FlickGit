namespace FlickGit.Models;

/// <summary>
/// One row of the commit window's file list.
///
/// The shape is fixed by CLAUDE.md, "File List → Model". Two points there are
/// load-bearing rather than incidental:
///
/// <list type="bullet">
/// <item><description>The index side and the working-tree side are stored
/// separately, because a file can be staged as modified and modified again
/// afterwards. The list displays the sum; the tooltip shows the split.</description></item>
/// <item><description>Line counts are nullable, and <c>null</c> means binary —
/// not zero. `--numstat` prints <c>-</c> for both counts on a binary file, and a
/// binary file showing "+0 -0" reads as "nothing changed".</description></item>
/// </list>
/// </summary>
public sealed class GitFileChange
{
    /// <summary>Repository-relative, forward slashes, exactly as Git reported it.</summary>
    public required string Path { get; init; }

    /// <summary>Set for a rename or a copy only.</summary>
    public string? OldPath { get; init; }

    public GitChangeType IndexStatus { get; init; }
    public GitChangeType WorkTreeStatus { get; init; }

    /// <summary>Lines added, or <c>null</c> when the file is binary or uncounted.</summary>
    public int? AddedLines { get; init; }

    /// <summary>Lines removed, or <c>null</c> when the file is binary or uncounted.</summary>
    public int? RemovedLines { get; init; }

    public bool IsBinary { get; init; }
    public bool IsUntracked { get; init; }

    /// <summary>Lines added on the index side alone. Shown in the tooltip split.</summary>
    public int? StagedAddedLines { get; init; }

    /// <summary>Lines removed on the index side alone.</summary>
    public int? StagedRemovedLines { get; init; }

    /// <summary>
    /// Set when the file's size or a content sniff put it past a display threshold,
    /// so the list can say so instead of printing a line count nobody counted.
    /// </summary>
    public long? SizeInBytes { get; init; }

    /// <summary>True when something in this file matched <see cref="Secrets.SecretDetector"/>.</summary>
    public bool LooksLikeSecret { get; init; }

    /// <summary>Anything staged at all, per porcelain v2's index column.</summary>
    public bool IsStaged { get; set; }

    /// <summary>Ticked in the file list, i.e. part of the next commit.</summary>
    public bool IsSelected { get; set; }

    /// <summary>
    /// The user staged part of this file by hunk, so the index already holds exactly what they chose.
    ///
    /// <b>This is the third staging state, and it exists because the other two would destroy the
    /// choice.</b> A ticked file is staged whole by <c>git add</c>, which would swallow the unstaged
    /// hunks the user deliberately left out; an unticked one is taken back out by
    /// <c>git restore --staged</c>, which would throw away the hunks they deliberately put in. A file
    /// in this state is touched by neither — the index is already right, so the commit sequence leaves
    /// it alone.
    ///
    /// Set by the diff viewer when a hunk is staged, and not derivable from Git: a file with both
    /// staged and unstaged changes looks identical whether the user chose that or merely edited a file
    /// they had already added, and those two want opposite behaviour from a tick.
    /// </summary>
    public bool HasChosenHunks { get; set; }

    /// <summary>
    /// The deletion is already recorded in the index, and the file is gone from disk.
    ///
    /// <b><c>git add</c> must not be run on this, and that is not an optimisation.</b> Pathspec
    /// matching looks at the working tree and the index; a file in this state is in neither, so
    /// <c>git add -- &lt;path&gt;</c> fails outright:
    ///
    /// <code>fatal: pathspec 'src/Thing.cs' did not match any files</code>
    ///
    /// The distinction is invisible on the row, which is what made this worth a named property. Both
    /// of these show a <c>D</c>:
    ///
    /// <list type="bullet">
    /// <item><description><c>1 .D</c> — deleted from the working tree only. The index entry is still
    /// there, so <c>git add</c> matches it and stages the deletion. This is the ordinary
    /// case.</description></item>
    /// <item><description><c>1 D.</c> — deleted with <c>git rm</c>, so the deletion is staged
    /// already. Nothing to match, and nothing to do: the index holds exactly what the user is asking
    /// to commit.</description></item>
    /// </list>
    /// </summary>
    public bool IsDeletionStaged =>
        IndexStatus == GitChangeType.Deleted && WorkTreeStatus == GitChangeType.None;

    /// <summary>Unmerged on either side. Not safe to edit, stage or commit.</summary>
    public bool IsConflicted =>
        IndexStatus == GitChangeType.Conflicted || WorkTreeStatus == GitChangeType.Conflicted;

    /// <summary>
    /// The letter the row shows. The working tree wins when both sides changed,
    /// because that is the state on disk and the state the diff pane opens on.
    /// </summary>
    public GitChangeType DisplayStatus =>
        IsConflicted ? GitChangeType.Conflicted
        : WorkTreeStatus != GitChangeType.None ? WorkTreeStatus
        : IndexStatus;

    /// <summary>
    /// Sort key, per CLAUDE.md: conflicted, modified, added, deleted, renamed,
    /// untracked last. Untracked files sort last because they are unchecked by
    /// default and the user should not have to scroll past them.
    /// </summary>
    public int SortRank => DisplayStatus switch
    {
        GitChangeType.Conflicted => 0,
        GitChangeType.Modified => 1,
        GitChangeType.TypeChanged => 1,
        GitChangeType.Added => 2,
        GitChangeType.Deleted => 3,
        GitChangeType.Renamed => 4,
        GitChangeType.Copied => 4,
        GitChangeType.Untracked => 5,
        _ => 6,
    };

    public override string ToString() =>
        $"{DisplayStatus.ToShortCode()} {Path}" + (IsBinary ? " bin" : $" +{AddedLines} -{RemovedLines}");
}
