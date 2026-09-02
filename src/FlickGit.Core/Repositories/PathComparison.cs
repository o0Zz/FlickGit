namespace FlickGit.Repositories;

/// <summary>
/// How two filesystem paths are compared for identity.
///
/// The comparison has to follow the filesystem rather than the operating system's habits. On
/// Windows <c>C:\Repo</c> and <c>C:\repo</c> are one directory; on a case-sensitive volume they are
/// two, and comparing them case-insensitively there merges two distinct repositories into one.
/// Worse, <see cref="FlickGit.Diff.WorkingTreeWriter.CrossesReparsePoint"/> compares an ancestor
/// directory against the repository root to know when it has arrived: case-insensitively on a
/// case-sensitive volume it never recognises the root, walks past the volume root and refuses the
/// write -- so every save fails, silently and for every file.
///
/// This is a property of the platform, not of the volume, and that is a deliberate simplification.
/// Probing the volume would be more accurate -- macOS formats case-insensitively by default but
/// can be either -- and it is not worth a syscall on a path that may not exist yet. Where the
/// answer actually has to be exact, both sides are derived from the same root string in the same
/// call, so their casing already agrees and the comparison is exact on any volume.
///
/// Ordering is not identity and does not belong here: the file list sorts
/// <see cref="StringComparison.OrdinalIgnoreCase"/> on purpose, so a row's position does not depend
/// on its first letter's case.
/// </summary>
public static class PathComparison
{
    /// <summary>For comparing two paths directly.</summary>
    public static StringComparison Comparison { get; } =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>For sets and dictionaries keyed by path.</summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Whether the two paths name the same thing.</summary>
    public static bool Equal(string left, string right) => string.Equals(left, right, Comparison);

    /// <summary>Whether <paramref name="path"/> begins with <paramref name="prefix"/>.</summary>
    public static bool StartsWith(string path, string prefix) => path.StartsWith(prefix, Comparison);
}
