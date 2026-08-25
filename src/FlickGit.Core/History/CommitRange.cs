namespace FlickGit.History;

/// <summary>
/// What a selection of commits means as a diff.
///
/// The rule is TortoiseGit's, and it is the whole point of the log window: selecting N commits
/// shows <c>git diff &lt;oldest&gt;^ &lt;newest&gt;</c>. One command, always fast, and it cannot fail —
/// unlike replaying only the picked patches onto a temporary tree, which can refuse when a
/// selected commit does not apply without a skipped one.
///
/// The price is that a <b>gapped</b> selection quietly spans the commits in between, so
/// <see cref="ImplicitCount"/> exists to be shown. That number is computed here, where it can be
/// tested, rather than in the window's string formatting.
/// </summary>
public sealed record CommitRange
{
    /// <summary>
    /// Git's empty tree — the same object id in every repository ever created, being the hash of a
    /// zero-byte tree.
    ///
    /// It is the base when the oldest selected commit is the root: <c>&lt;root&gt;^</c> is not a
    /// revision, it is an error, so without this the repository's first commit would be the one
    /// commit in the list that cannot be viewed.
    /// </summary>
    public const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    /// <summary>
    /// The left side, always a bare object id and never revision syntax.
    ///
    /// This is the oldest selected commit's <b>first parent</b>, which is exactly what
    /// <c>&lt;oldest&gt;^</c> means. Resolving it here rather than appending "^" means everything
    /// downstream hands Git a plain sha, so nothing else has to know Git's revision grammar and a
    /// test can assert an exact string.
    /// </summary>
    public required string BaseSpec { get; init; }

    /// <summary>
    /// The right side: the newest selected commit's own sha. A merge needs no special case — it
    /// has one tree like any other commit.
    /// </summary>
    public required string TipSpec { get; init; }

    public required LogCommit Oldest { get; init; }
    public required LogCommit Newest { get; init; }

    /// <summary>How many rows the user actually picked.</summary>
    public required int SelectedCount { get; init; }

    /// <summary>
    /// Every commit the range spans, newest first: the rows between the two ends, including the
    /// ones a gapped selection passed over.
    ///
    /// Sliced here rather than by the caller for the reason <see cref="Resolve"/> is here at all --
    /// the list is newest-first, so the slice runs from the <i>newest</i> index to the <i>oldest</i>,
    /// and an off-by-one at either end silently drops the commit somebody asked about.
    ///
    /// It is the spanned set and not the selected one on purpose: the diff, the patch and the
    /// changelog all describe the same range, and one of the three quietly describing a narrower one
    /// is worse than <see cref="ImplicitCount"/>, which at least says so out loud.
    /// </summary>
    public required IReadOnlyList<LogCommit> Commits { get; init; }

    /// <summary>How many commits the range spans, gaps included.</summary>
    public int SpannedCount => Commits.Count;

    /// <summary>
    /// Commits dragged in by a gapped selection.
    ///
    /// The window states this whenever it is non-zero. It is the user's only warning that the diff
    /// on screen is wider than the rows they highlighted.
    /// </summary>
    public int ImplicitCount => SpannedCount - SelectedCount;

    /// <summary>
    /// What the diff viewer's header shows: <c>a1b2c3d^ ↔ e4f5g6h</c>.
    ///
    /// Spelled with "^" even though <see cref="BaseSpec"/> is a raw sha, because that is the range
    /// the user would type. Not localised and it does not need to be — two abbreviated hashes and
    /// an arrow read the same in every language.
    /// </summary>
    public string Label => Oldest.IsRoot
        ? $"⌀ ↔ {Newest.ShortSha}"
        : $"{Oldest.ShortSha}^ ↔ {Newest.ShortSha}";

    /// <summary>
    /// Which commits a selection means, given the list it was made in.
    ///
    /// Pure, and it takes the whole list rather than just the chosen commits, because
    /// <see cref="SpannedCount"/> is a property of the <i>positions</i>: the list is newest-first,
    /// so the newest selected commit is the lowest selected index and the oldest is the highest,
    /// and everything between them is in the range whether it was picked or not.
    ///
    /// In Core rather than in the window for the reason CLAUDE.md gives for <c>CommitFlow</c>: a
    /// view model can only be exercised by clicking, and "the range came out the wrong way round"
    /// is exactly the bug clicking does not reveal — both ends are plausible hashes either way.
    /// </summary>
    /// <returns>Null when nothing in the list is selected.</returns>
    public static CommitRange? Resolve(
        IReadOnlyList<LogCommit> newestFirst,
        IReadOnlySet<string> selectedShas)
    {
        int newest = -1;
        int oldest = -1;
        int selected = 0;

        for (int i = 0; i < newestFirst.Count; i++)
        {
            if (!selectedShas.Contains(newestFirst[i].Sha))
                continue;

            if (newest < 0)
                newest = i;

            oldest = i;
            selected++;
        }

        if (newest < 0)
            return null;

        LogCommit tip = newestFirst[newest];
        LogCommit basis = newestFirst[oldest];

        return new CommitRange
        {
            //A merge as the oldest selection takes Parents[0] like anything else, and that is
            //deliberate: the first parent is the branch being merged *into*, so the diff reads as
            //"what this merge brought in". The second parent would invert it, showing every change
            //from the other side as a deletion.
            BaseSpec = basis.IsRoot ? EmptyTree : basis.Parents[0],
            TipSpec = tip.Sha,
            Oldest = basis,
            Newest = tip,
            SelectedCount = selected,
            Commits = [.. newestFirst.Skip(newest).Take(oldest - newest + 1)],
        };
    }
}
