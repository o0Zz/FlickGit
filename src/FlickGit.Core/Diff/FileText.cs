using System.Text;

namespace FlickGit.Diff;

/// <summary>
/// How a line ends in this file. Detected on load and restored on save.
/// </summary>
public enum LineEndingStyle
{
    /// <summary>Empty file, or a single line with no terminator. Nothing to preserve.</summary>
    None,

    Lf,
    CrLf,
    Cr,

    /// <summary>More than one style present. Saving must not "fix" it.</summary>
    Mixed,
}

/// <summary>
/// A file's text together with everything needed to write it back **byte-identically**
/// except for the edit.
///
/// CLAUDE.md, "Live Editing the Working Tree" is blunt about why this type exists:
/// "Silently normalising line endings on a Windows tool turns a three-line change into a
/// whole-file diff, and it will happen on the first CRLF repository otherwise." The same
/// goes for the BOM — UTF-8 with a BOM and UTF-8 without one are different files to Git,
/// and dropping three bytes shows up as a change to line 1 of a file nobody touched.
///
/// The stamp fields (<see cref="SizeInBytes"/>, <see cref="LastWriteTimeUtc"/>,
/// <see cref="ContentHash"/>) are the external-modification guard: an IDE that
/// reformatted on save, or a build that regenerated the file, must not be silently
/// overwritten. All three are captured at load; a save compares them first.
/// </summary>
public sealed record FileText
{
    /// <summary>The text, with every line ending normalised to <c>\n</c> for diffing and editing.</summary>
    public required string Text { get; init; }

    /// <summary>The encoding the bytes were in. Restored verbatim on save.</summary>
    public required Encoding Encoding { get; init; }

    /// <summary>Whether the file began with a byte-order mark.</summary>
    public required bool HasByteOrderMark { get; init; }

    public required LineEndingStyle LineEndings { get; init; }

    /// <summary>Whether the last line was terminated. Adding one is a diff; so is removing one.</summary>
    public required bool EndsWithNewline { get; init; }

    public long SizeInBytes { get; init; }

    public DateTime LastWriteTimeUtc { get; init; }

    /// <summary>SHA-256 of the bytes as read. The authoritative half of the external-change check.</summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>A NUL byte was found. The viewer stays read-only and shows no text diff.</summary>
    public bool IsBinary { get; init; }

    /// <summary>
    /// The exact terminator that followed each line, in order, for a file whose endings are
    /// <see cref="LineEndingStyle.Mixed"/>. Null for every other file.
    ///
    /// Populated only for mixed files because only they need it, and it costs an allocation per
    /// line. For a uniformly-terminated file <see cref="NewLine"/> is enough to rebuild the
    /// original bytes exactly; for a mixed one it is not, and reconstructing from the dominant
    /// style alone would rewrite every line of the other kind — turning a one-line edit into a
    /// whole-file diff, which is the specific outcome this type exists to prevent.
    ///
    /// The last entry is the empty string when the file does not end with a newline.
    /// </summary>
    public IReadOnlyList<string>? PerLineEndings { get; init; }

    /// <summary>
    /// The terminator to write for a line break.
    ///
    /// For a mixed file this is the *dominant* style, and it is used only for lines the user
    /// added: lines that were already there keep their own terminator from
    /// <see cref="PerLineEndings"/>.
    /// </summary>
    public string NewLine => LineEndings switch
    {
        LineEndingStyle.CrLf => "\r\n",
        LineEndingStyle.Cr => "\r",
        LineEndingStyle.Mixed => DominantNewLine ?? "\n",
        _ => "\n",
    };

    /// <summary>
    /// The most common terminator in a mixed file. Set alongside <see cref="PerLineEndings"/>.
    /// </summary>
    public string? DominantNewLine { get; init; }

    /// <summary>Line count, cheap enough to recompute and not worth caching on a record.</summary>
    public int LineCount => Text.Length == 0 ? 0 : Text.AsSpan().Count('\n') + (EndsWithNewline ? 0 : 1);

    /// <summary>An empty left-hand side: an untracked file, or one added in this commit.</summary>
    public static FileText Empty { get; } = new()
    {
        Text = string.Empty,
        Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        HasByteOrderMark = false,
        LineEndings = LineEndingStyle.None,
        EndsWithNewline = false,
    };
}
