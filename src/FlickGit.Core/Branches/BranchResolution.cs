namespace FlickGit.Branches;

/// <summary>What the branch ComboBox's current text means.</summary>
public enum BranchIntent
{
    /// <summary>The branch already checked out. Committing involves no switch at all.</summary>
    Current,

    /// <summary>An existing local branch. Committing switches to it first.</summary>
    ExistingBranch,

    /// <summary>Not an existing branch, and a legal ref name. Committing creates it.</summary>
    NewBranch,

    /// <summary>Git would reject the name. No command runs.</summary>
    Invalid,

    /// <summary>Empty. Treated as "the current branch" but shown as nothing.</summary>
    Empty,
}

/// <summary>
/// What committing would do to the branch, given what the user has typed.
///
/// CLAUDE.md, "Branch Selector": "The ComboBox shows the resolution inline as the user types, so
/// the consequence is visible before Enter." That is why this is a type rather than a bool — the
/// user needs to see <i>which</i> of three quite different things pressing Enter will do.
///
/// <b>In Core rather than beside the view models, because it decides whether Git switches
/// branches.</b> This value becomes <c>CommitRequest.TargetBranch</c> and
/// <c>CommitRequest.CreateBranch</c>, so getting it wrong creates a branch by accident or commits
/// onto the wrong one — and there are now two commit surfaces asking. It carries no wording for the
/// same reason: the hint text is presentation, and lives in the App's language file.
///
/// Computed with no Git process at all: the branch list is already in memory from the status
/// refresh, and name validity is an offline check. Authoritative validation with
/// <c>check-ref-format</c> happens once, at commit time, before anything is created.
/// </summary>
/// <param name="Intent">What committing would do.</param>
/// <param name="Branch">The trimmed branch name.</param>
public sealed record BranchResolution(BranchIntent Intent, string Branch)
{
    /// <summary>True when committing is possible at all.</summary>
    public bool IsCommittable => Intent is not BranchIntent.Invalid;

    /// <summary>True when a switch or a create has to happen before the commit.</summary>
    public bool RequiresBranchChange => Intent is BranchIntent.ExistingBranch or BranchIntent.NewBranch;

    /// <summary>
    /// Resolves the typed text against the current branch and the known local branches.
    /// </summary>
    public static BranchResolution Resolve(
        string? typed,
        string? currentBranch,
        IEnumerable<string> localBranches)
    {
        string branch = (typed ?? string.Empty).Trim();

        if (branch.Length == 0)
            return new BranchResolution(BranchIntent.Empty, currentBranch ?? string.Empty);

        //Ordinal, not case-insensitive: Git branch names are case-sensitive, so "Main" typed on a
        //repository whose branch is "main" is a *different* branch and must read as one. Treating
        //them as equal would silently skip the switch and commit to the wrong branch.
        if (currentBranch is not null && string.Equals(branch, currentBranch, StringComparison.Ordinal))
            return new BranchResolution(BranchIntent.Current, branch);

        if (localBranches.Contains(branch, StringComparer.Ordinal))
            return new BranchResolution(BranchIntent.ExistingBranch, branch);

        //Offline check only. It is enough for live feedback per keystroke, and CLAUDE.md requires
        //an invalid name to be "rejected before any Git command runs".
        return BranchService.LooksValid(branch)
            ? new BranchResolution(BranchIntent.NewBranch, branch)
            : new BranchResolution(BranchIntent.Invalid, branch);
    }
}
