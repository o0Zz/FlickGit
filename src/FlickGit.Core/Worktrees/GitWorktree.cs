namespace FlickGit.Worktrees;

/// <summary>
/// One checkout of a repository. The main working tree is one of these; every linked worktree
/// added with <c>git worktree add</c> is another.
///
/// <b>Five fields, which is every field something reads.</b> The parser also used to keep the
/// record's <c>HEAD</c> sha, and a <c>bare</c> and a <c>detached</c> flag; nothing anywhere read any
/// of the three, and the only reference to <c>detached</c> was a test asserting the parser that set
/// it. A detached or bare worktree is recognised by having no <see cref="Branch"/>, which is what
/// the Branches window actually asks -- so per Hard Requirement 2 the three went rather than waiting
/// for a caller.
/// </summary>
/// <param name="Path">
/// Absolute path to the worktree's root, in the Windows spelling. Normalised through the same
/// function <c>RepositoryService</c> uses, so a worktree path and a resolved repository root are
/// comparable as strings -- which is what lets the Branches window tell "this branch is checked
/// out somewhere else" from "this branch is checked out here".
/// </param>
/// <param name="Branch">
/// The short branch name, or null for a detached or bare worktree. Null is the reason a detached
/// worktree has no row in the Branches window: there is no branch to hang it on.
/// </param>
/// <param name="IsMain">
/// The repository's original working tree, which is always the first record <c>worktree list</c>
/// emits. It cannot be removed -- Git refuses, and so do we, before asking.
/// </param>
/// <param name="IsLocked">
/// Somebody ran <c>git worktree lock</c>, usually because the worktree lives on removable media.
/// Removing one needs <c>--force</c> twice, which nothing here offers: the lock is a statement of
/// intent by whoever set it, and a picker is not the place to overrule it.
/// </param>
/// <param name="IsPrunable">
/// Git's bookkeeping entry survives but the directory is gone -- the ordinary result of deleting a
/// worktree folder in Explorer. <b>This is the trap worth surfacing:</b> until it is pruned, Git
/// still considers the branch checked out and refuses to switch to it, with a message naming a
/// directory that no longer exists.
/// </param>
public sealed record GitWorktree(
    string Path,
    string? Branch,
    bool IsMain,
    bool IsLocked,
    bool IsPrunable);

/// <summary>
/// What the new worktree should have checked out. Three cases, because <c>git worktree add</c>
/// spells them three different ways and the caller knows which row was clicked.
/// </summary>
/// <param name="Branch">An existing local branch to check out. Null unless that is the case.</param>
/// <param name="NewBranch">A branch to create. Null unless that is the case.</param>
/// <param name="StartPoint">
/// What <see cref="NewBranch"/> starts from -- a remote-tracking ref, when the row clicked was a
/// remote one. Null means HEAD, which is the same rule the Branches window already follows for
/// creating a branch.
/// </param>
public sealed record WorktreeStart(string? Branch, string? NewBranch, string? StartPoint)
{
    /// <summary>Check out a branch that already exists.</summary>
    public static WorktreeStart Existing(string branch) => new(branch, null, null);

    /// <summary>Create a branch at HEAD and check it out there.</summary>
    public static WorktreeStart Create(string branch) => new(null, branch, null);

    /// <summary>Create a local branch tracking <paramref name="remoteRef"/> and check it out there.</summary>
    public static WorktreeStart Track(string branch, string remoteRef) => new(null, branch, remoteRef);
}

/// <summary>Why a worktree operation was refused before Git was asked anything.</summary>
public enum WorktreeRefusal
{
    None,

    /// <summary>A relative path. See <c>WorktreeService.CheckTarget</c> for why that is refused.</summary>
    NotAbsolute,

    /// <summary>Inside the repository's own working tree, where it would show up as untracked files.</summary>
    InsideRepository,

    /// <summary>The directory exists and has something in it. Git would refuse too; we name the fix.</summary>
    NotEmpty,

    /// <summary>The main working tree, which is not a thing that can be removed.</summary>
    IsMainWorktree,

    /// <summary>Locked by whoever created it. Overruling that needs a command line, not a menu.</summary>
    IsLocked,
}

/// <param name="Refusal">Set when nothing was asked of Git, so nothing can have changed.</param>
/// <param name="HasLocalChanges">
/// Git refused to remove a worktree holding modified or untracked files.
///
/// <b>Unlike <c>BranchDeleteOutcome.NotMerged</c> this is not a route to a forced spelling</b>, and
/// <c>WorktreeService.RemoveAsync</c> says why: the forced removal deletes files Git has never seen,
/// with no reflog and no Recycle Bin behind them. It exists so the message can name the two ways out
/// that destroy nothing.
/// </param>
public sealed record WorktreeOutcome(
    bool Succeeded,
    string? GitError,
    WorktreeRefusal Refusal = WorktreeRefusal.None,
    bool HasLocalChanges = false)
{
    public static WorktreeOutcome Ok { get; } = new(true, null);

    public static WorktreeOutcome Refused(WorktreeRefusal refusal) => new(false, null, refusal);
}
