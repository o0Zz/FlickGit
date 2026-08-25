using FlickGit.Models;
using FlickGit.Repositories;
using FlickGit.Worktrees;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Worktrees: the <c>worktree list --porcelain</c> parser, and the refusals.
///
/// In scope on two of Hard Requirement 4's five bullets. The parser belongs with the other four --
/// a wrong field here means the Branches window claims a branch is checked out in a directory that
/// is not the one it is in. The refusals belong with the safety rules: <c>--force</c> is never
/// reached by the service deciding to reach it, and a target inside the repository's own working
/// tree is refused before Git is asked, because a nested worktree is a tree full of untracked files
/// that a `clean` can sweep away.
/// </summary>
public class WorktreeServiceTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\dev\repo", "repo", HasSubmodules: false, IsBare: false);

    private static WorktreeService Create(FakeGitRunner git) => new(git, new RepositoryService(git));

    /// <summary>What Git actually prints, including the three records with no branch of their own.</summary>
    private const string ListOutput = """
        worktree C:/dev/repo
        HEAD 1111111111111111111111111111111111111111
        branch refs/heads/main

        worktree C:/dev/repo-feature-storage gw
        HEAD 2222222222222222222222222222222222222222
        branch refs/heads/feature/storage-gw

        worktree C:/dev/repo-detached
        HEAD 3333333333333333333333333333333333333333
        detached

        worktree C:/dev/repo-gone
        HEAD 4444444444444444444444444444444444444444
        branch refs/heads/fix/pool-leak
        prunable gitdir file points to non-existent location

        worktree C:/dev/repo-usb
        HEAD 5555555555555555555555555555555555555555
        branch refs/heads/release
        locked on a removable drive, do not prune

        """;

    [Fact]
    public void TheListParsesEveryRecordShape()
    {
        IReadOnlyList<GitWorktree> worktrees = WorktreeService.ParseList(ListOutput);

        Assert.Equal(5, worktrees.Count);

        //Normalised to the Windows spelling, so a worktree path and a resolved repository root compare
        //as strings.
        Assert.Equal(@"C:\dev\repo", worktrees[0].Path);
        Assert.Equal("main", worktrees[0].Branch);

        //refs/heads/ stripped, and a branch containing a slash keeps it.
        Assert.Equal("feature/storage-gw", worktrees[1].Branch);

        //The value runs to the end of the line: a path containing a space is one field, not two.
        Assert.Equal(@"C:\dev\repo-feature-storage gw", worktrees[1].Path);

        //Detached and bare worktrees have no branch, which is what keeps them off the Branches window --
        //and is the only thing anything asks about one, which is why the record no longer carries a
        //`detached` flag of its own.
        Assert.Null(worktrees[2].Branch);

        Assert.True(worktrees[3].IsPrunable);
        Assert.Equal("fix/pool-leak", worktrees[3].Branch);

        //`locked` carries a free-text reason containing commas and spaces, and is still one flag.
        Assert.True(worktrees[4].IsLocked);
    }

    [Fact]
    public void TheFirstRecordIsTheMainWorktreeAndNothingElseIs()
    {
        //Position, not content: no field in the record says which one is the original working tree, and
        //getting it wrong would offer to remove the repository the user is standing in.
        IReadOnlyList<GitWorktree> worktrees = WorktreeService.ParseList(ListOutput);

        Assert.True(worktrees[0].IsMain);
        Assert.All(worktrees.Skip(1), w => Assert.False(w.IsMain));
    }

    [Fact]
    public async Task ATargetInsideTheRepositoryIsRefusedBeforeGitIsAsked()
    {
        //A worktree nested in another shows up as a directory of untracked files in the outer one's
        //status -- so `clean -fdx` deletes it, and we would offer to stage it.
        var git = new FakeGitRunner();

        WorktreeOutcome outcome = await Create(git).AddAsync(
            Repository,
            @"C:\dev\repo\worktrees\hotfix",
            WorktreeStart.Create("hotfix"),
            CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(WorktreeRefusal.InsideRepository, outcome.Refusal);
        Assert.Empty(git.Invocations);
    }

    [Fact]
    public async Task ARelativeTargetIsRefusedBeforeGitIsAsked()
    {
        //Every call runs `git -C <root>`, so a relative path resolves against the repository root --
        //producing the nested worktree above out of a value that looked like it pointed elsewhere.
        var git = new FakeGitRunner();

        WorktreeOutcome outcome = await Create(git).AddAsync(
            Repository,
            @"..\repo-hotfix",
            WorktreeStart.Create("hotfix"),
            CancellationToken.None);

        Assert.Equal(WorktreeRefusal.NotAbsolute, outcome.Refusal);
        Assert.Empty(git.Invocations);
    }

    [Theory]
    [InlineData(@"C:\dev\repo")]
    [InlineData(@"C:\dev\repo\src")]
    [InlineData(@"C:\dev\repo\..\repo\src")]
    [InlineData(@"c:\DEV\REPO\src")]
    public void ContainmentCatchesTheRootItselfAndEveryWayDownIntoIt(string path) =>
        Assert.True(WorktreeService.IsInside(@"C:\dev\repo", path));

    [Theory]
    [InlineData(@"C:\dev\repo2")]
    [InlineData(@"C:\dev\repo-hotfix")]
    [InlineData(@"C:\dev\other\repo")]
    [InlineData(@"D:\repo")]
    public void ContainmentDoesNotCatchASiblingWhoseNameStartsTheSame(string path) =>
        //"C:\repo2" is not inside "C:\repo", which a bare StartsWith would get wrong -- and a sibling
        //named after the repository is precisely what this feature suggests.
        Assert.False(WorktreeService.IsInside(@"C:\dev\repo", path));

    [Fact]
    public async Task AddingForAnExistingBranchNamesThePathBeforeTheBranch()
    {
        //`worktree add <path> [<commit-ish>]`. Reversed, Git would create a directory named after the
        //branch and try to check out a commit-ish named after a path.
        var git = new FakeGitRunner().Returns(["worktree", "add"]);

        WorktreeOutcome outcome = await Create(git).AddAsync(
            Repository,
            @"C:\dev\repo-main",
            WorktreeStart.Existing("main"),
            CancellationToken.None);

        Assert.True(outcome.Succeeded);

        string[] args = Assert.Single(git.Invocations).Args;

        Assert.Equal(["worktree", "add", @"C:\dev\repo-main", "main"], args);
        Assert.DoesNotContain("--force", args);
    }

    [Fact]
    public async Task AddingFromARemoteRowTracksItSoTheBranchHasAnUpstream()
    {
        //Without --track the new branch has no upstream, so its first push would ask to create one that
        //already exists on the remote.
        var git = new FakeGitRunner().Returns(["worktree", "add"]);

        await Create(git).AddAsync(
            Repository,
            @"C:\dev\repo-gw",
            WorktreeStart.Track("feature/storage-gw", "origin/feature/storage-gw"),
            CancellationToken.None);

        Assert.Equal(
            ["worktree", "add", "--track", "-b", "feature/storage-gw", @"C:\dev\repo-gw", "origin/feature/storage-gw"],
            Assert.Single(git.Invocations).Args);
    }

    [Fact]
    public async Task ADirtyWorktreeIsReportedAndNeverForced()
    {
        //THE rule for this file, and the one place worktrees deliberately differ from `branch -d`/`-D`:
        //forcing a branch deletion leaves the commits in the reflog, while `worktree remove --force`
        //deletes modified and untracked files with no reflog and no Recycle Bin behind them. CLAUDE.md,
        //"Safety Rules": never discard uncommitted work, unconditionally.
        var git = new FakeGitRunner().Returns(
            ["worktree", "remove"],
            exitCode: 1,
            stderr: "fatal: 'C:/dev/repo-gw' contains modified or untracked files, use --force to delete it");

        WorktreeOutcome outcome = await Create(git).RemoveAsync(
            Repository,
            Linked("feature/storage-gw", @"C:\dev\repo-gw"),
            CancellationToken.None);

        Assert.False(outcome.Succeeded);

        //Reported as its own state, so the caller can name the two ways out that destroy nothing --
        //committing the work, or deleting the folder in Explorer, where it goes to the Recycle Bin.
        Assert.True(outcome.HasLocalChanges);
        Assert.True(git.NeverCalledWith("--force"));
    }

    [Fact]
    public async Task NoWorktreeCommandCanEverCarryForce()
    {
        //There is no code path to it: `RemoveAsync` has no force parameter at all, so this pins the
        //absence rather than a caller's choice. The same shape as GitArgumentTests' assertions that
        //`add -A` never appears.
        var git = new FakeGitRunner()
            .Returns(["worktree", "add"])
            .Returns(["worktree", "remove"])
            .Returns(["worktree", "prune"])
            .Returns(["worktree", "list"], stdout: ListOutput);

        WorktreeService worktrees = Create(git);
        GitWorktree linked = Linked("feature/storage-gw", @"C:\dev\repo-gw");

        await worktrees.ListAsync(Repository, CancellationToken.None);
        await worktrees.AddAsync(Repository, @"C:\dev\repo-gw", WorktreeStart.Create("gw"), CancellationToken.None);
        await worktrees.RemoveAsync(Repository, linked, CancellationToken.None);
        await worktrees.PruneAsync(Repository, CancellationToken.None);

        Assert.True(git.NeverCalledWith("--force"));
        Assert.True(git.NeverCalledWith("-f"));
    }

    [Fact]
    public async Task TheMainWorktreeAndALockedOneRunNoCommandAtAll()
    {
        //Both refused by us. Git refuses them too, but its wording is about its own bookkeeping -- and a
        //locked worktree is a statement of intent by whoever locked it, which a picker is not the place
        //to overrule.
        var git = new FakeGitRunner();
        WorktreeService worktrees = Create(git);

        IReadOnlyList<GitWorktree> parsed = WorktreeService.ParseList(ListOutput);

        Assert.Equal(
            WorktreeRefusal.IsMainWorktree,
            (await worktrees.RemoveAsync(Repository, parsed[0], CancellationToken.None)).Refusal);

        Assert.Equal(
            WorktreeRefusal.IsLocked,
            (await worktrees.RemoveAsync(Repository, parsed[4], CancellationToken.None)).Refusal);

        Assert.Empty(git.Invocations);
    }

    [Fact]
    public async Task ListingIsAReadAndCarriesNoOptionalLocks()
    {
        //The window opens on this, so it must not take the index lock while an IDE is in the same tree.
        var git = new FakeGitRunner().Returns(["worktree", "list"], stdout: ListOutput);

        await Create(git).ListAsync(Repository, CancellationToken.None);

        FakeGitRunner.Invocation call = Assert.Single(git.Invocations);

        Assert.True(call.ReadOnly);
        Assert.Contains("--porcelain", call.Args);

        //-z arrived in Git 2.36 and the stated minimum is 2.23.
        Assert.DoesNotContain("-z", call.Args);
    }

    [Theory]
    [InlineData("feature/storage-gw", "repo-feature-storage-gw")]
    [InlineData("hotfix", "repo-hotfix")]
    [InlineData("fix/pool/leak", "repo-fix-pool-leak")]
    public void TheSuggestedFolderNameFlattensTheWholeBranch(string branch, string expected) =>
        //Flattened whole rather than reduced to its last segment: "fix/pool" and "feature/pool" would
        //otherwise suggest one directory, and the second attempt would be refused for a reason that
        //reads like a bug.
        Assert.Equal(expected, WorktreeService.SuggestFolderName("repo", branch));

    private static GitWorktree Linked(string branch, string path) =>
        new(path, branch, IsMain: false, IsLocked: false, IsPrunable: false);
}
