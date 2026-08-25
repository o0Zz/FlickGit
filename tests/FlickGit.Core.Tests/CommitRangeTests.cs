using FlickGit.History;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Turning a selection of rows into <c>oldest^..newest</c>.
///
/// In scope under Hard Requirement 4's parser bullet, which covers the pure functions beside the
/// parsers: this is where a wrong index becomes a diff of the wrong commits. It is the log
/// window's entire correctness surface, both ends of an inverted range are plausible hashes, and
/// it lives in Core precisely so it can be exercised without clicking.
/// </summary>
public class CommitRangeTests
{
    /// <summary>Five commits, newest first, each parented on the next — the list's own order.</summary>
    private static IReadOnlyList<LogCommit> Chain() =>
    [
        Commit("e5", "d4"),
        Commit("d4", "c3"),
        Commit("c3", "b2"),
        Commit("b2", "a1"),
        Commit("a1"),
    ];

    private static LogCommit Commit(string sha, params string[] parents) => new()
    {
        Sha = sha,
        ShortSha = sha,
        Parents = parents,
        Author = "Ana",
        When = DateTimeOffset.UnixEpoch,
        Refs = string.Empty,
        Message = $"commit {sha}",
    };

    [Fact]
    public void OneCommitDiffsAgainstItsOwnParent()
    {
        CommitRange range = Assert.IsType<CommitRange>(
            CommitRange.Resolve(Chain(), new HashSet<string> { "c3" }));

        Assert.Equal("b2", range.BaseSpec);
        Assert.Equal("c3", range.TipSpec);
        Assert.Equal(1, range.SelectedCount);
        Assert.Equal(0, range.ImplicitCount);
    }

    [Fact]
    public void SeveralCommitsDiffTheOldestsParentToTheNewest()
    {
        //The list is newest-first, so the oldest selection is the *highest* index. This is the one
        //place in the feature where the arithmetic reads backwards.
        CommitRange range = Assert.IsType<CommitRange>(
            CommitRange.Resolve(Chain(), new HashSet<string> { "e5", "d4", "c3" }));

        Assert.Equal("b2", range.BaseSpec);
        Assert.Equal("e5", range.TipSpec);
        Assert.Equal(3, range.SelectedCount);
        Assert.Equal(3, range.SpannedCount);
        Assert.Equal(0, range.ImplicitCount);
    }

    [Fact]
    public void AGappedSelectionSpansTheCommitsBetweenAndCountsThem()
    {
        //Picking the ends of a five-commit list sweeps in the three in the middle. That is
        //TortoiseGit's rule and it is what the user chose -- but the window has to be able to say
        //so, which is the only reason ImplicitCount exists.
        CommitRange range = Assert.IsType<CommitRange>(
            CommitRange.Resolve(Chain(), new HashSet<string> { "e5", "a1" }));

        Assert.Equal(2, range.SelectedCount);
        Assert.Equal(5, range.SpannedCount);
        Assert.Equal(3, range.ImplicitCount);
    }

    [Fact]
    public void TheSpannedCommitsAreTheRangeAndNotTheSelection()
    {
        //What the changelog is written over, and what makes ImplicitCount a claim about a list that
        //exists rather than a number nothing can be checked against. The slice runs from the newest
        //index to the oldest, which is the one piece of arithmetic in this file that reads backwards.
        CommitRange range = Assert.IsType<CommitRange>(
            CommitRange.Resolve(Chain(), new HashSet<string> { "e5", "c3" }));

        //Newest first, the list's own order, and holding d4 -- which nobody picked.
        Assert.Equal(["e5", "d4", "c3"], range.Commits.Select(c => c.Sha));
        Assert.Equal(2, range.SelectedCount);
        Assert.Equal(1, range.ImplicitCount);
    }

    [Fact]
    public void TheRootCommitDiffsAgainstTheEmptyTree()
    {
        //"<root>^" is not a revision, it is an error -- so without this the repository's first
        //commit would be the one commit in the list that cannot be viewed.
        CommitRange range = Assert.IsType<CommitRange>(
            CommitRange.Resolve(Chain(), new HashSet<string> { "a1" }));

        Assert.Equal(CommitRange.EmptyTree, range.BaseSpec);
        Assert.Equal("a1", range.TipSpec);
    }

    [Fact]
    public void AMergeAsTheOldestSelectionTakesItsFirstParent()
    {
        //The second parent would invert the diff, showing every change from the merged branch as a
        //deletion.
        IReadOnlyList<LogCommit> commits = [Commit("tip", "merge"), Commit("merge", "mainline", "topic")];

        CommitRange range = Assert.IsType<CommitRange>(
            CommitRange.Resolve(commits, new HashSet<string> { "tip", "merge" }));

        Assert.Equal("mainline", range.BaseSpec);
        Assert.Equal("tip", range.TipSpec);
    }

    [Fact]
    public void NothingSelectedResolvesToNoRange()
    {
        Assert.Null(CommitRange.Resolve(Chain(), new HashSet<string>()));
        Assert.Null(CommitRange.Resolve(Chain(), new HashSet<string> { "not-in-the-list" }));
    }
}
