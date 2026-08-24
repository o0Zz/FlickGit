namespace FlickGit.Blame;

/// <summary>
/// The commit behind one or more blamed lines.
///
/// Everything here comes from one <c>git blame --porcelain</c> metadata block, which Git emits only
/// the <b>first</b> time a commit appears in the output — so instances are shared by every line the
/// commit is responsible for, which is also what lets the gutter draw an annotation once per run.
/// </summary>
public sealed record BlameCommit
{
    /// <summary>Forty zeros for a line that is not committed yet. See <see cref="IsUncommitted"/>.</summary>
    public required string Sha { get; init; }

    public required string Author { get; init; }
    public required DateTimeOffset When { get; init; }

    /// <summary>The commit's subject line, from <c>summary</c>. Arbitrary user text.</summary>
    public required string Summary { get; init; }

    /// <summary>The path the file had at this commit. Differs from the current path across a rename.</summary>
    public required string Filename { get; init; }

    /// <summary>
    /// The commit to blame next when walking back, from <c>previous &lt;sha&gt; &lt;path&gt;</c>.
    ///
    /// <b>This is the whole walk-back feature, and Git computes it.</b> Nothing here appends
    /// <c>^</c> or resolves a parent: Git names the commit the file came from and the path it had
    /// there, so a rename is followed by using <see cref="PreviousPath"/> rather than the path we
    /// arrived with. Null when <see cref="IsBoundary"/>.
    /// </summary>
    public string? PreviousSha { get; init; }

    /// <summary>The path the file had at <see cref="PreviousSha"/>. Null with it.</summary>
    public string? PreviousPath { get; init; }

    /// <summary>
    /// Git marked this commit the edge of the walk — the first commit that touched the file, or the
    /// limit of a range. There is nothing before it to blame.
    /// </summary>
    public bool IsBoundary { get; init; }

    /// <summary>
    /// The line is in the working tree and not in any commit.
    ///
    /// Git reports these under a sha of forty zeros with the author "Not Committed Yet". Blaming the
    /// working tree is the ordinary case, so this is a normal state rather than an error.
    /// </summary>
    public bool IsUncommitted => Sha.Length > 0 && Sha.All(static c => c == '0');

    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;

    /// <summary>True when the walk can continue from here.</summary>
    public bool HasPrevious => PreviousSha is { Length: > 0 };

    public override string ToString() => $"{ShortSha} {Author} {Summary}";
}

/// <param name="Number">1-based line number in the blamed revision of the file.</param>
/// <param name="Commit">Shared with every other line the same commit is responsible for.</param>
/// <param name="Text">The line without its terminator.</param>
public sealed record BlameLine(int Number, BlameCommit Commit, string Text);

/// <param name="Succeeded">False when Git refused. <paramref name="Error"/> then carries its words.</param>
/// <param name="Lines">In file order, 1..n. Empty on failure and for an empty file.</param>
/// <param name="Error">Git's own stderr, shown as-is — CLAUDE.md, "Error Handling".</param>
/// <param name="IsBinary">
/// The content Git handed back is not text. Git does not refuse to blame a binary file, it blames it
/// into nonsense, so this is detected here rather than relied on to fail.
/// </param>
public sealed record BlameOutcome(
    bool Succeeded,
    IReadOnlyList<BlameLine> Lines,
    string? Error,
    bool IsBinary)
{
    public static BlameOutcome Failed(string error) => new(false, [], error, false);

    public static readonly BlameOutcome Binary = new(false, [], null, true);

    /// <summary>
    /// The blamed revision's file content, which the viewer renders.
    ///
    /// Reconstructed from the lines rather than fetched with a second <c>git show</c>: the porcelain
    /// output already carries every line's text, so asking again would be a process for something we
    /// have — and a second read could disagree with the annotations if the file changed between them.
    /// </summary>
    public string Text() => string.Join('\n', Lines.Select(l => l.Text));
}
