namespace FlickGit.Merges;

/// <summary>
/// The Git operation a repository is part-way through, when it is part-way through one.
///
/// All four stop the same way -- a commit could not be applied, the working tree holds conflict
/// markers, and Git is waiting -- and all four are left the same way, with <c>--continue</c> or
/// <c>--abort</c> spelt with the operation's own name. That symmetry is why this is one enum rather
/// than four booleans: the surface asks "which word goes before --continue".
/// </summary>
public enum MergeOperation
{
    None,
    Merge,
    Rebase,
    CherryPick,
    Revert,
}

/// <summary>
/// What <see cref="MergeStateService"/> found, and everything the commit window's resolution bar
/// needs to describe it.
/// </summary>
/// <param name="Operation">The one in progress, or <see cref="MergeOperation.None"/>.</param>
/// <param name="Step">
/// Which commit of the sequence is being applied, 1-based. Only a rebase counts: a merge, a
/// cherry-pick and a revert of a single commit are one step by construction, and Git records no
/// counter for them.
/// </param>
/// <param name="Total">How many there are, when <paramref name="Step"/> is known.</param>
public sealed record MergeState(MergeOperation Operation, int? Step, int? Total)
{
    public static readonly MergeState None = new(MergeOperation.None, null, null);

    public bool InProgress => Operation != MergeOperation.None;

    /// <summary>True only when both halves of the counter came back, so a caller can format "3 of 7".</summary>
    public bool HasProgress => Step is > 0 && Total is > 0;

    /// <summary>
    /// The word Git wants in front of <c>--continue</c> and <c>--abort</c>.
    ///
    /// A pure function of the enum, here rather than in the service, because it is what the tests
    /// assert the argument vectors against.
    /// </summary>
    public static string Verb(MergeOperation operation) => operation switch
    {
        MergeOperation.Merge => "merge",
        MergeOperation.Rebase => "rebase",
        MergeOperation.CherryPick => "cherry-pick",
        MergeOperation.Revert => "revert",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "No operation is in progress."),
    };
}

/// <summary>
/// Which stage of an unmerged path to take.
///
/// <b>The names are Git's, and they are not swapped for a rebase.</b> During a rebase <c>--ours</c>
/// is the branch being rebased *onto* and <c>--theirs</c> is the user's own commit being replayed --
/// the opposite of what both words suggest. Renaming them to "mine" and "theirs" here would bake the
/// wrong half of that into the code; the surface says which is which in words, per operation.
/// </summary>
public enum ConflictSide
{
    Ours,
    Theirs,
}
