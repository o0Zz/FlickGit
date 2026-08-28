namespace FlickGit.Models;

/// <summary>
/// Everything the commit surface needs in one object: which branch, how far from the
/// upstream, and what changed.
///
/// Assembled by <see cref="Status.StatusService"/> from one porcelain v2 call and two
/// numstat calls run in parallel. The branch fields come out of the same porcelain
/// call (`--branch` headers), which is why no separate `rev-parse` is needed to draw
/// the header.
/// </summary>
public sealed record RepositoryStatus
{
    public required RepositoryInfo Repository { get; init; }

    /// <summary>Short branch name, or null when HEAD is detached.</summary>
    public string? Branch { get; init; }

    /// <summary>Short upstream name (`origin/main`), or null when the branch has no upstream.</summary>
    public string? Upstream { get; init; }

    /// <summary>Commits on the branch that the upstream does not have. 0 when there is no upstream.</summary>
    public int Ahead { get; init; }

    /// <summary>Commits on the upstream that the branch does not have.</summary>
    public int Behind { get; init; }

    /// <summary>The commit HEAD points at, or null in an empty repository with no commits yet.</summary>
    public string? HeadCommit { get; init; }

    public bool IsDetachedHead { get; init; }

    /// <summary>True in a repository whose HEAD has no commit yet. The left side of every diff is empty.</summary>
    public bool IsUnborn { get; init; }

    public IReadOnlyList<GitFileChange> Files { get; init; } = [];

    /// <summary>
    /// The merge, rebase, cherry-pick or revert this repository is part-way through, or
    /// <see cref="Merges.MergeState.None"/>.
    ///
    /// Here rather than asked for separately because it costs nothing -- a few file probes over a
    /// path the repository already carries -- and because every surface that refreshes the status
    /// then learns about it without a second call to remember.
    /// </summary>
    public Merges.MergeState Merge { get; init; } = Merges.MergeState.None;

    /// <summary>
    /// Diverged: local and remote each hold commits the other does not. CLAUDE.md,
    /// "Commit &amp; Push" — this is the case where the tool stops rather than
    /// offering a force-push.
    /// </summary>
    public bool HasDiverged => Ahead > 0 && Behind > 0;

    public bool HasConflicts => Files.Any(f => f.IsConflicted);

    public int UntrackedCount => Files.Count(f => f.IsUntracked);

    public int TrackedChangeCount => Files.Count(f => !f.IsUntracked);
}
