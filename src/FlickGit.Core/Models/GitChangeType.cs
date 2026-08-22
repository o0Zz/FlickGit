namespace FlickGit.Models;

/// <summary>
/// What happened to a path, on one side of the index.
///
/// Deliberately not a [Flags] enum: a path has exactly one state per side, and
/// <see cref="GitFileChange"/> carries the two sides separately because a file can
/// be staged as modified and then modified again in the working tree.
/// </summary>
public enum GitChangeType
{
    /// <summary>No change on this side.</summary>
    None = 0,

    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,

    /// <summary>Present on disk, not in the index. Never has an index-side status.</summary>
    Untracked,

    /// <summary>Matched by an ignore rule. Only ever reported when explicitly asked for.</summary>
    Ignored,

    /// <summary>Unmerged. Both sides carry this; the file is not safe to edit or commit.</summary>
    Conflicted,
}

public static class GitChangeTypeExtensions
{
    /// <summary>
    /// The single letter the file list shows, matching what a Git user already reads
    /// in `git status --short`.
    /// </summary>
    public static string ToShortCode(this GitChangeType type) => type switch
    {
        GitChangeType.Modified => "M",
        GitChangeType.Added => "A",
        GitChangeType.Deleted => "D",
        GitChangeType.Renamed => "R",
        GitChangeType.Copied => "C",
        GitChangeType.TypeChanged => "T",
        GitChangeType.Untracked => "?",
        GitChangeType.Ignored => "!",
        GitChangeType.Conflicted => "U",
        _ => " ",
    };

    /// <summary>
    /// Maps one XY character of porcelain v2 status. Unknown letters map to
    /// <see cref="GitChangeType.None"/> rather than throwing: a future Git that adds
    /// a status letter must not stop the tool from showing the rest of the list.
    /// </summary>
    public static GitChangeType FromStatusChar(char c) => c switch
    {
        'M' => GitChangeType.Modified,
        'A' => GitChangeType.Added,
        'D' => GitChangeType.Deleted,
        'R' => GitChangeType.Renamed,
        'C' => GitChangeType.Copied,
        'T' => GitChangeType.TypeChanged,
        'U' => GitChangeType.Conflicted,
        '?' => GitChangeType.Untracked,
        '!' => GitChangeType.Ignored,
        _ => GitChangeType.None,
    };
}
