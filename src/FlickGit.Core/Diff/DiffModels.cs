namespace FlickGit.Diff;

/// <summary>
/// The two object-store sides a read-only diff is of, and what to call them on screen.
///
/// Three strings rather than the <see cref="History.CommitRange"/> this started as, because a range
/// of commits is only one of the things a historical diff can be of. A stash is a commit too, and
/// the untracked half of one is a tree against the empty tree — neither has an oldest and a newest
/// commit to name, and both read better labelled <c>a1b2c3d ↔ stash@{0}</c> than as two bare
/// hashes. These three fields are all <c>DiffService.ComputeRangeAsync</c> ever read of a range, so
/// these three fields are what it takes.
/// </summary>
/// <param name="BaseSpec">The left side. A bare object id, never revision syntax.</param>
/// <param name="TipSpec">The right side, also a bare object id.</param>
/// <param name="Label">
/// The viewer's header, as <c>&lt;left&gt; ↔ &lt;right&gt;</c>. Not localised and it does not need to
/// be: two abbreviated hashes and an arrow read the same in every language.
/// </param>
public sealed record DiffRange(string BaseSpec, string TipSpec, string Label);

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
    /// The two revisions this diff is of, or null when the right side is the working tree.
    ///
    /// Non-null means both sides came out of Git's object store, so there is no file on disk the
    /// right pane corresponds to and nothing to save — which is why <see cref="IsEditable"/>
    /// consults it before anything else. Carrying the range rather than a bare flag also gives the
    /// viewer its header (<see cref="DiffRange.Label"/>) with no second field to keep in
    /// step: a diff cannot then be rendered read-only under a label saying "Working tree ↔ HEAD",
    /// which — given that the whole staged-versus-worktree section exists because a mislabelled
    /// header is how users lose work — is exactly the mistake not to make possible.
    /// </summary>
    public DiffRange? Range { get; init; }

    /// <summary>True when the right pane may be edited: a working-tree diff, not binary, and small enough to re-diff live.</summary>
    public bool IsEditable =>
        Range is null
        && RenderMode is DiffRenderMode.SideBySideWithWordDiff or DiffRenderMode.SideBySideLinesOnly
        && !Right.IsBinary;
}
