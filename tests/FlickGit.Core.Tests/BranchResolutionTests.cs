using FlickGit.Branches;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// What the branch box's text means.
///
/// In scope under Hard Requirement 4 as <b>the commit sequence</b>: this value becomes
/// <c>CommitRequest.TargetBranch</c> and <c>CommitRequest.CreateBranch</c>, so it is what decides
/// whether step 2 of <see cref="Commits.CommitFlow"/> switches branches, creates one, or does
/// nothing at all.
/// </summary>
public class BranchResolutionTests
{
    private static readonly string[] Local = ["main", "feature/pool", "fix/leak"];

    [Fact]
    public void Empty_text_means_the_current_branch_and_no_switch()
    {
        BranchResolution resolution = BranchResolution.Resolve(string.Empty, "main", Local);

        Assert.Equal(BranchIntent.Empty, resolution.Intent);
        Assert.Equal("main", resolution.Branch);
        Assert.False(resolution.RequiresBranchChange);
        Assert.True(resolution.IsCommittable);
    }

    /// <summary>
    /// CLAUDE.md, "Branch Selector": naming the branch already checked out "performs no switch at
    /// all". That is a performance claim and a safety one — a needless switch can fail on a dirty
    /// tree and turn an ordinary commit into a refusal.
    /// </summary>
    [Fact]
    public void The_current_branch_by_name_requires_no_change()
    {
        BranchResolution resolution = BranchResolution.Resolve("main", "main", Local);

        Assert.Equal(BranchIntent.Current, resolution.Intent);
        Assert.False(resolution.RequiresBranchChange);
    }

    [Fact]
    public void A_known_local_branch_switches()
    {
        BranchResolution resolution = BranchResolution.Resolve("fix/leak", "main", Local);

        Assert.Equal(BranchIntent.ExistingBranch, resolution.Intent);
        Assert.True(resolution.RequiresBranchChange);
    }

    [Fact]
    public void An_unknown_legal_name_creates()
    {
        BranchResolution resolution = BranchResolution.Resolve("feature/new-thing", "main", Local);

        Assert.Equal(BranchIntent.NewBranch, resolution.Intent);
        Assert.True(resolution.RequiresBranchChange);
    }

    /// <summary>
    /// An invalid name is rejected before any Git command runs, which CLAUDE.md requires
    /// explicitly. <c>IsCommittable</c> is false, so the buttons are disabled rather than the
    /// failure arriving from Git.
    /// </summary>
    [Theory]
    [InlineData("feature/bad..name")]
    [InlineData("-leading-dash")]
    [InlineData("has space")]
    [InlineData("ends.lock")]
    public void An_illegal_ref_name_is_not_committable(string typed)
    {
        BranchResolution resolution = BranchResolution.Resolve(typed, "main", Local);

        Assert.Equal(BranchIntent.Invalid, resolution.Intent);
        Assert.False(resolution.IsCommittable);
    }

    /// <summary>
    /// Git branch names are case-sensitive, so "Main" on a repository sitting on "main" is a
    /// different branch. Treating them as equal would skip the switch and commit to the wrong
    /// branch; treating them as the same *new* name would create one by accident.
    /// </summary>
    [Fact]
    public void Branch_names_are_case_sensitive()
    {
        BranchResolution resolution = BranchResolution.Resolve("Main", "main", Local);

        Assert.Equal(BranchIntent.NewBranch, resolution.Intent);
        Assert.Equal("Main", resolution.Branch);
    }

    /// <summary>Whitespace is trimmed, so a trailing space does not create a second branch.</summary>
    [Fact]
    public void Surrounding_whitespace_is_trimmed()
    {
        Assert.Equal(BranchIntent.Current, BranchResolution.Resolve("  main  ", "main", Local).Intent);
    }
}
