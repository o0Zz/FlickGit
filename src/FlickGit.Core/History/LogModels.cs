namespace FlickGit.History;

/// <summary>
/// One commit, as the log window lists it.
///
/// Every field comes from one <c>--format</c> placeholder, and the shape is decided by what a
/// list row and a range need rather than by what Git can emit: there is no committer, no tree
/// and no signature here, because nothing shows them.
/// </summary>
public sealed record LogCommit
{
    public required string Sha { get; init; }

    /// <summary>%h. Git's own abbreviation, so it is as short as the repository allows and still unique.</summary>
    public required string ShortSha { get; init; }

    /// <summary>
    /// From %P. Empty for the root commit, two or more for a merge.
    ///
    /// Load-bearing rather than informational: <see cref="CommitRange"/> reads the first entry to
    /// build the base of a range, and its emptiness is what makes the repository's first commit
    /// diff against the empty tree instead of erroring.
    /// </summary>
    public required IReadOnlyList<string> Parents { get; init; }

    public required string Author { get; init; }
    public required DateTimeOffset When { get; init; }

    /// <summary>%D: "HEAD -&gt; main, origin/main, tag: v1.0", or empty when undecorated.</summary>
    public required string Refs { get; init; }

    /// <summary>%B with its trailing newline removed — the whole message, subject line included.</summary>
    public required string Message { get; init; }

    /// <summary>
    /// The first line, derived rather than asked for separately.
    ///
    /// %s exists, and using it alongside %B would let the two disagree about where the message
    /// starts — Git's own subject rules unwrap a folded first line, so a message could show a
    /// subject the body does not begin with.
    /// </summary>
    public string Subject
    {
        get
        {
            int end = Message.IndexOf('\n');
            return end < 0 ? Message : Message[..end].TrimEnd('\r');
        }
    }

    /// <summary>Everything after the subject and the blank line that follows it. Empty for a one-line message.</summary>
    public string Body
    {
        get
        {
            int end = Message.IndexOf('\n');
            return end < 0 ? string.Empty : Message[(end + 1)..].Trim('\n', '\r');
        }
    }

    public bool IsMerge => Parents.Count > 1;

    /// <summary>No parent, so nothing to diff against but the empty tree.</summary>
    public bool IsRoot => Parents.Count == 0;

    public override string ToString() => $"{ShortSha} {Subject}";
}

/// <param name="Commits">One page, newest first — `git log` order, kept.</param>
/// <param name="HasMore">
/// True when Git had at least one more commit to give. Known rather than guessed: the request asks
/// for one row past the page and drops it, so "Load more" cannot appear on an exhausted history.
/// </param>
public sealed record LogPage(IReadOnlyList<LogCommit> Commits, bool HasMore)
{
    public static readonly LogPage Empty = new([], false);
}
