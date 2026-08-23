namespace FlickGit.Diff;

/// <summary>
/// What the left pane is showing. Permanently labelled in the viewer header, because
/// CLAUDE.md, "The staged-versus-worktree trap" makes it the user's only clue about
/// whether the edit they are about to make will be in the commit.
/// </summary>
public enum DiffComparisonMode
{
    /// <summary>`git show HEAD:&lt;path&gt;` on the left. The default.</summary>
    WorkingTreeVsHead,

    /// <summary>`git show :&lt;path&gt;` on the left — the index. Editing the right pane edits the *working tree*.</summary>
    WorkingTreeVsIndex,
}

/// <summary>
/// How much diffing the file's size allows. Thresholds from CLAUDE.md, "Diff Viewer →
/// Performance", and they exist to keep the viewer responsive rather than to save work
/// for its own sake.
/// </summary>
public enum DiffRenderMode
{
    /// <summary>Side by side, with a word-level pass inside changed line pairs.</summary>
    SideBySideWithWordDiff,

    /// <summary>Side by side, line level only. Above 500 KB.</summary>
    SideBySideLinesOnly,

    /// <summary>Read-only unified text from `git diff`. Above 2 MB or 50,000 lines.</summary>
    UnifiedReadOnly,

    /// <summary>Binary. No text diff is attempted at all.</summary>
    Binary,
}

public enum DiffLineKind
{
    /// <summary>Present and identical on both sides.</summary>
    Unchanged,

    /// <summary>Right side only.</summary>
    Inserted,

    /// <summary>Left side only.</summary>
    Deleted,

    /// <summary>Present on both sides, with differences inside the line.</summary>
    Modified,

    /// <summary>Padding, so the two panes stay aligned. Renders as an empty gutter row.</summary>
    Filler,
}

/// <param name="Start">Character offset within the line.</param>
/// <param name="Length">Characters covered.</param>
public readonly record struct DiffSpan(int Start, int Length);

/// <summary>One pane's half of a row.</summary>
/// <param name="LineNumber">1-based, or null on a filler row where this side has no line.</param>
/// <param name="Text">The line without its terminator. Empty string on a filler row.</param>
/// <param name="ChangedSpans">
/// Word-level ranges to highlight inside <paramref name="Text"/>. Empty unless the row is
/// <see cref="DiffLineKind.Modified"/> and word diffing was enabled for this file.
/// </param>
public sealed record DiffSide(int? LineNumber, string Text, IReadOnlyList<DiffSpan> ChangedSpans)
{
    public bool IsFiller => LineNumber is null;
}

/// <summary>
/// One aligned pair of lines. The unit the renderers consume.
///
/// Alignment lives here rather than in the view because CLAUDE.md requires scrolling to
/// be "locked to the diff alignment, not to raw line numbers": both panes render the same
/// row list, so they cannot drift.
/// </summary>
public sealed record DiffRow(DiffLineKind Kind, DiffSide Left, DiffSide Right);

/// <summary>
/// A computed diff, ready to render.
/// </summary>
public sealed record SideBySideDiff
{
    public required string Path { get; init; }

    /// <summary>
    /// Which working-tree comparison this is. Meaningless when <see cref="Range"/> is set, where
    /// the label comes from the range instead.
    /// </summary>
    public required DiffComparisonMode ComparisonMode { get; init; }

    public required DiffRenderMode RenderMode { get; init; }

    public IReadOnlyList<DiffRow> Rows { get; init; } = [];

    /// <summary>The right pane's text as loaded — what the editor is initialised with.</summary>
    public required FileText Right { get; init; }

    /// <summary>The base side. <see cref="FileText.Empty"/> for an untracked or newly added file.</summary>
    public required FileText Left { get; init; }

    /// <summary>
    /// Set for <see cref="DiffRenderMode.UnifiedReadOnly"/>: raw `git diff` output, shown
    /// as-is because re-diffing a file this size on every keystroke is not something to
    /// attempt.
    /// </summary>
    public string? UnifiedText { get; init; }

    /// <summary>Why the viewer refused, when it did. Shown in the header, never as a dialog.</summary>
    public string? Notice { get; init; }

    /// <summary>
    /// The commit range this diff is of, or null when the right side is the working tree.
    ///
    /// Non-null means both sides came out of Git's object store, so there is no file on disk the
    /// right pane corresponds to and nothing to save — which is why <see cref="IsEditable"/>
    /// consults it before anything else. Carrying the range rather than a bare flag also gives the
    /// viewer its header (<see cref="History.CommitRange.Label"/>) with no second field to keep in
    /// step: a diff cannot then be rendered read-only under a label saying "Working tree ↔ HEAD",
    /// which — given that the whole staged-versus-worktree section exists because a mislabelled
    /// header is how users lose work — is exactly the mistake not to make possible.
    /// </summary>
    public History.CommitRange? Range { get; init; }

    /// <summary>True when the right pane may be edited: a working-tree diff, not binary, and small enough to re-diff live.</summary>
    public bool IsEditable =>
        Range is null
        && RenderMode is DiffRenderMode.SideBySideWithWordDiff or DiffRenderMode.SideBySideLinesOnly
        && !Right.IsBinary;
}
