using FlickGit.Models;
using FlickGit.Status;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The comparison that decides whether a commit may go ahead after a branch switch.
///
/// In scope as one of the sequences: <c>CommitFlow</c> calls this <b>after</b> the switch, so
/// anything that throws here leaves the user with files staged, the branch changed, no commit and
/// no explanation — the exact half-finished state the flow's ordering exists to prevent.
/// </summary>
public class StatusComparerTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\repos\alpha", "alpha", HasSubmodules: false, IsBare: false, GitDirectory: @"C:\repos\alpha\.git");

    private static GitFileChange File(
        string path,
        GitChangeType index = GitChangeType.None,
        GitChangeType worktree = GitChangeType.Modified,
        bool selected = true,
        bool untracked = false) =>
        new()
        {
            Path = path,
            IndexStatus = index,
            WorkTreeStatus = worktree,
            IsUntracked = untracked,
            IsSelected = selected,
        };

    private static RepositoryStatus Status(params GitFileChange[] files) =>
        new() { Repository = Repository, Branch = "main", Files = files };

    /// <summary>
    /// Porcelain v2 reports one path twice, and that must not throw.
    ///
    /// After <c>git rm --cached foo</c> the same path comes back as a staged deletion
    /// (<c>1 D. … foo</c>) <i>and</i> as untracked (<c>? foo</c>). A dictionary built with
    /// <c>ToDictionary</c> throws on the duplicate key, uncaught, from inside the commit sequence.
    /// </summary>
    [Fact]
    public void ADuplicatePathInTheRefreshedStatusIsNotAnException()
    {
        RepositoryStatus before = Status(File("scratch/dump.json"));

        RepositoryStatus after = Status(
            File("scratch/dump.json", index: GitChangeType.Deleted, worktree: GitChangeType.None),
            File("scratch/dump.json", worktree: GitChangeType.Untracked, untracked: true));

        IReadOnlyList<string> changed = StatusComparer.SelectedFilesThatChanged(before, after);

        //It genuinely did change under the user -- the tracked entry now says deleted -- so the flow
        //stops and shows the refreshed list. What matters is that it stops by *reporting* rather than
        //by throwing.
        Assert.Equal(["scratch/dump.json"], changed);
    }

    /// <summary>
    /// An unchanged selection is not reported as changed, which is what keeps the guard from
    /// refusing every commit that involves a branch switch.
    /// </summary>
    [Fact]
    public void AnIdenticalSnapshotReportsNothing()
    {
        RepositoryStatus before = Status(File("src/A.cs"), File("src/B.cs", selected: false));
        RepositoryStatus after = Status(File("src/A.cs"), File("src/B.cs", selected: false));

        Assert.Empty(StatusComparer.SelectedFilesThatChanged(before, after));
    }
}
