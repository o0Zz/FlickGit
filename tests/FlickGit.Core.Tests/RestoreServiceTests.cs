using FlickGit.Diff;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Reverting one file to HEAD.
///
/// In scope under "the safety rules": this is the only place in the product that asks Git to
/// discard uncommitted work, so what may be handed to it and what its argument list contains are
/// both worth pinning. <see cref="RestoreService.CanRevert"/> is the guard, and the case it exists
/// for is not hypothetical — <c>git restore --source=HEAD --staged --worktree</c> on a path HEAD
/// does not have deletes the file and exits 0.
/// </summary>
public class RestoreServiceTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\dev\repo", "repo", HasSubmodules: false, IsBare: false);

    private static RestoreService Create(FakeGitRunner git) =>
        new(git, new RepositoryService(git), NullLog.Instance);

    private static GitFileChange File(
        GitChangeType index = GitChangeType.None,
        GitChangeType workTree = GitChangeType.Modified,
        bool untracked = false) =>
        new()
        {
            Path = "src/Thing.cs",
            IndexStatus = index,
            WorkTreeStatus = workTree,
            IsUntracked = untracked,
        };

    [Fact]
    public async Task TheRestoreNamesOnePathAndTakesBothSidesFromHead()
    {
        //`--source=HEAD` because the default restores the working tree from the *index*, which
        //would leave a staged change standing on a file the row now says is unmodified. `--staged`
        //and `--worktree` together are what makes the row's letter go away.
        var git = new FakeGitRunner().Returns(["restore"]);

        RestoreResult result = await Create(git).RevertAsync(Repository, "src/Thing.cs", default);

        Assert.True(result.Succeeded);

        string[] args = Assert.Single(git.Invocations).Args;

        Assert.Equal(
            ["restore", "--source=HEAD", "--staged", "--worktree", "--", "src/Thing.cs"],
            args);
    }

    [Fact]
    public async Task ThePathIsNeverAPathspecStandingForEverything()
    {
        //CLAUDE.md's Safety Rules name `git restore .` and `git checkout -- .` outright. What makes
        //this call a different thing is that the pathspec is one file the user right-clicked, so a
        //list that had come to contain "." or "-A" would be the forbidden command wearing this
        //one's name.
        var git = new FakeGitRunner().Returns(["restore"]);

        await Create(git).RevertAsync(Repository, "src/Thing.cs", default);

        string[] args = Assert.Single(git.Invocations).Args;

        Assert.DoesNotContain(".", args);
        Assert.DoesNotContain("-A", args);
        Assert.DoesNotContain("--hard", args);

        //After the separator, so a file named like an option cannot become one.
        Assert.Equal(args.Length - 2, Array.IndexOf(args, "--"));
    }

    [Fact]
    public async Task AWriteDoesNotCarryNoOptionalLocks()
    {
        //It is supposed to take the index lock. The flag is for reads.
        var git = new FakeGitRunner().Returns(["restore"]);

        await Create(git).RevertAsync(Repository, "src/Thing.cs", default);

        Assert.False(Assert.Single(git.Invocations).ReadOnly);
    }

    [Fact]
    public async Task AFailedRestoreReportsGitsOwnWords()
    {
        var git = new FakeGitRunner().Returns(["restore"], exitCode: 1, stderr: "error: unable to unlink");

        RestoreResult result = await Create(git).RevertAsync(Repository, "src/Thing.cs", default);

        Assert.False(result.Succeeded);
        Assert.Equal("error: unable to unlink", result.Error);
    }

    [Fact]
    public void AnOrdinaryModifiedFileMayBeReverted()
    {
        Assert.True(RestoreService.CanRevert(File()));
    }

    [Fact]
    public void ADeletedFileMayBeReverted()
    {
        //Both spellings of a D row: gone from the working tree, and removed with `git rm`. HEAD has
        //the path in either case, which is the only question, and the revert is what brings it back.
        Assert.True(RestoreService.CanRevert(File(workTree: GitChangeType.Deleted)));
        Assert.True(RestoreService.CanRevert(File(index: GitChangeType.Deleted, workTree: GitChangeType.None)));
    }

    [Fact]
    public void AStagedModificationMayBeReverted()
    {
        Assert.True(RestoreService.CanRevert(File(index: GitChangeType.Modified, workTree: GitChangeType.Modified)));
    }

    [Fact]
    public void AnAddedFileMayNotBeReverted()
    {
        //THE case this guard exists for. The path is in the index and not in HEAD, and Git's answer
        //to `restore --source=HEAD --staged --worktree` on such a path is to delete the file, exit
        //0, and say nothing -- uncommitted work destroyed by a command that reported success.
        Assert.False(RestoreService.CanRevert(File(index: GitChangeType.Added, workTree: GitChangeType.None)));
    }

    [Fact]
    public void AnUntrackedFileMayNotBeReverted()
    {
        //HEAD has nothing to put back. Delete is the item for this row, and it goes to the Recycle
        //Bin because Git could not bring the file back either.
        Assert.False(RestoreService.CanRevert(File(workTree: GitChangeType.Untracked, untracked: true)));
    }

    [Fact]
    public void ARenameMayNotBeReverted()
    {
        //HEAD has the *old* path. Restoring this one alone is the Added case again -- it would
        //delete the renamed file -- and doing it correctly is two operations with two ways to fail
        //half way.
        Assert.False(RestoreService.CanRevert(File(index: GitChangeType.Renamed, workTree: GitChangeType.None)));
        Assert.False(RestoreService.CanRevert(File(index: GitChangeType.Copied, workTree: GitChangeType.None)));
    }

    [Fact]
    public void AConflictedFileMayNotBeReverted()
    {
        //Resolving a merge by taking HEAD's side is a merge decision wearing a revert's label, and
        //conflict resolution is out of scope.
        Assert.False(RestoreService.CanRevert(
            File(index: GitChangeType.Conflicted, workTree: GitChangeType.Conflicted)));
    }
}
