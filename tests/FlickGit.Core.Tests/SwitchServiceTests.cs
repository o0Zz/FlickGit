using FlickGit.Branches;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Branch switching.
///
/// The tests that matter here are the ones asserting that nothing happened: a refused switch
/// must not stash, must not force, and must leave the working tree alone. CLAUDE.md, "Testing":
/// "A blocked `git switch` leaves the working tree and index byte-identical."
/// </summary>
public class SwitchServiceTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\dev\repo", "repo", HasSubmodules: false, IsBare: false, GitDirectory: @"C:\dev\repo\.git");

    private static SwitchService Create(FakeGitRunner git) =>
        new(git, new RepositoryService(git), NullLog.Instance);

    /// <summary>What Git actually prints when local changes block a switch.</summary>
    private const string BlockedStderr = """
        error: Your local changes to the following files would be overwritten by checkout:
        	src/GatewayClient.cs
        	src/Options.cs
        Please commit your changes or stash them before you switch branches.
        Aborting
        """;

    [Fact]
    public async Task APlainSwitchIsTriedFirst()
    {
        //Git carries uncommitted changes across when there is no conflict, which is usually what
        //the user wants. Anything cleverer than trying it is a worse default.
        var git = new FakeGitRunner().Returns(["switch"]);

        SwitchOutcome outcome = await Create(git).SwitchAsync(Repository, "main", CancellationToken.None);

        Assert.True(outcome.Succeeded);

        string[] args = Assert.Single(git.Invocations, i => i.Args.Contains("switch")).Args;
        Assert.Contains("main", args);
        Assert.DoesNotContain("--force", args);
        Assert.DoesNotContain("-f", args);
    }

    [Fact]
    public async Task ARefusedSwitchNeverStashesAndNeverForces()
    {
        //THE rule. CLAUDE.md: "If it fails, **stop** -- do not stash, do not force."
        var git = new FakeGitRunner().Returns(["switch"], exitCode: 1, stderr: BlockedStderr);

        SwitchOutcome outcome = await Create(git).SwitchAsync(Repository, "main", CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.True(git.NeverCalledWith("stash"));
        Assert.True(git.NeverCalledWith("--force"));
        Assert.True(git.NeverCalledWith("reset"));
        Assert.True(git.NeverCalledWith("checkout"));
    }

    [Fact]
    public async Task ARefusedSwitchReportsTheBlockingFiles()
    {
        //The user has to be told which files are in the way; that is the whole content of the
        //decision they now have to make.
        var git = new FakeGitRunner().Returns(["switch"], exitCode: 1, stderr: BlockedStderr);

        SwitchOutcome outcome = await Create(git).SwitchAsync(Repository, "main", CancellationToken.None);

        Assert.True(outcome.RefusedByLocalChanges);
        Assert.Equal(2, outcome.BlockingFiles.Count);
        Assert.Contains("src/GatewayClient.cs", outcome.BlockingFiles);
        Assert.Contains("src/Options.cs", outcome.BlockingFiles);
    }

    [Fact]
    public void BlockingFileParsingDropsGitsHintSentences()
    {
        //Git's trailing advice is indented like the paths are. Including it would put "Please
        //commit your changes..." in a list captioned "these files would be overwritten".
        IReadOnlyList<string> files = SwitchService.ParseBlockingFiles(BlockedStderr);

        Assert.Equal(2, files.Count);
        Assert.DoesNotContain(files, f => f.StartsWith("Please", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusalWithNoNamedFilesIsNotOfferedTheStashPath()
    {
        //A switch can fail for reasons a stash cannot fix -- an unknown branch, a broken index.
        //Offering "Stash, switch, restore" there would be a button that cannot work.
        var git = new FakeGitRunner()
            .Returns(["switch"], exitCode: 128, stderr: "fatal: invalid reference: nope");

        SwitchOutcome outcome = await Create(git).SwitchAsync(Repository, "nope", CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.RefusedByLocalChanges);
        Assert.Empty(outcome.BlockingFiles);
    }

    [Fact]
    public async Task CheckingOutATagDetachesRatherThanSwitching()
    {
        //A tag is not a branch, so `git switch <tag>` refuses outright. --detach is the one spelling
        //that means "go there", and the one place in the product that asks for it -- CLAUDE.md,
        //"Blame"/"Log" hold the line that reading history changes nothing; this crosses it on purpose
        //and does so with a confirmation, not with a force.
        var git = new FakeGitRunner().Returns(["switch", "--detach"]);

        SwitchOutcome outcome = await Create(git).DetachAsync(Repository, "v1.4.0", CancellationToken.None);

        Assert.True(outcome.Succeeded);

        string[] args = Assert.Single(git.Invocations).Args;
        Assert.Equal(["switch", "--detach", "v1.4.0"], args);

        //The three ways this could have become destructive, none of which is reachable from here.
        Assert.True(git.NeverCalledWith("--force"));
        Assert.True(git.NeverCalledWith("-f"));
        Assert.True(git.NeverCalledWith("checkout"));
    }

    [Fact]
    public async Task ABlockedCheckoutChangesNothing()
    {
        //The same rule the branch path has, reached through the same code: refused, with the files
        //named and the working tree byte-identical. No stash is attempted -- that sequence belongs to
        //the Branches window and cannot switch to a tag anyway.
        var git = new FakeGitRunner().Returns(["switch", "--detach"], exitCode: 1, stderr: BlockedStderr);

        SwitchOutcome outcome = await Create(git).DetachAsync(Repository, "v1.4.0", CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.RefusedByLocalChanges);
        Assert.Contains("src/GatewayClient.cs", outcome.BlockingFiles);

        Assert.Single(git.Invocations);
        Assert.True(git.NeverCalledWith("stash"));
        Assert.True(git.NeverCalledWith("reset"));
    }

    [Fact]
    public async Task CreatingABranchUsesSwitchDashCWithNoFallbackToCheckout()
    {
        var git = new FakeGitRunner().Returns(["switch", "-c"]);

        Assert.True((await Create(git).CreateAsync(Repository, "feature/new", CancellationToken.None)).Succeeded);

        //One spelling only. Git 2.23 is the stated minimum, so `checkout -b` would be a second
        //code path for a Git nobody runs -- CLAUDE.md, "Hard Requirements".
        Assert.True(git.NeverCalledWith("checkout"));
        Assert.Single(git.Invocations);
    }

    [Fact]
    public async Task AFailedCreateIsNotRetriedUnderTheOlderSpelling()
    {
        var git = new FakeGitRunner()
            .Returns(["switch", "-c"], exitCode: 128, stderr: "fatal: a branch named 'x' already exists");

        SwitchOutcome outcome = await Create(git).CreateAsync(Repository, "x", CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.True(git.NeverCalledWith("checkout"));
    }

    [Fact]
    public async Task AFailedCreateIsReportedRatherThanSwallowed()
    {
        //The caller must not commit after this. Returning an outcome rather than throwing is
        //what lets the commit flow stop cleanly.
        var git = new FakeGitRunner()
            .Returns(["switch", "-c"], exitCode: 128, stderr: "fatal: a branch named 'x' already exists");

        SwitchOutcome outcome = await Create(git).CreateAsync(Repository, "x", CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("already exists", outcome.GitError);
    }

    [Fact]
    public async Task StashSwitchRestoreLocatesItsOwnStashByMessage()
    {
        //CLAUDE.md, "Testing": "Stash-switch-restore restores only the stash it created."
        //stash@{0} here is somebody else's, deliberately: an implementation that popped by index
        //would take it and this test would fail.
        //The stash list is built from the message the service actually passed to `stash push`,
        //because the service generates it fresh. stash@{0} is a decoy: an implementation that
        //popped by index would take it, and that is the bug this test exists to catch.
        var git = new FakeGitRunner()
            .Returns(["stash", "push"])
            .ReturnsFrom(["stash", "list"], f =>
                "stash@{0}\tOn main: unrelated work from last week\n" +
                $"stash@{{1}}\tOn main: {f.ArgumentAfter("-m")}\n")
            .Returns(["switch"])
            .Returns(["stash", "pop"]);

        SwitchOutcome outcome = await Create(git)
            .StashSwitchRestoreAsync(Repository, "main", CancellationToken.None);

        Assert.True(outcome.Succeeded);

        string[] pop = Assert.Single(git.Invocations, i => i.Args.Contains("pop")).Args;
        Assert.Contains("stash@{1}", pop);
        Assert.DoesNotContain("stash@{0}", pop);
    }

    [Fact]
    public async Task StashSwitchRestoreCreatesAUniquelyIdentifiableStash()
    {
        var git = new FakeGitRunner()
            .Returns(["stash", "push"])
            .Returns(["stash", "list"], stdout: string.Empty)
            .Returns(["switch"]);

        await Create(git).StashSwitchRestoreAsync(Repository, "main", CancellationToken.None);

        string[] push = Assert.Single(git.Invocations, i => i.Args.Contains("push") && i.Args.Contains("stash")).Args;

        //-m with a marker the finder can recognise, and --include-untracked so the stash is
        //actually a snapshot of the working tree rather than half of one.
        Assert.Contains("-m", push);
        Assert.Contains(push, a => a.StartsWith(SwitchService.StashMessagePrefix, StringComparison.Ordinal));
        Assert.Contains("--include-untracked", push);
    }

    [Fact]
    public async Task NothingToStashStillSwitchesAndPopsNothing()
    {
        //"No local changes to save" leaves no stash of ours. Popping stash@{0} here would take an
        //unrelated one -- the case that makes looking the reference up non-negotiable.
        var git = new FakeGitRunner()
            .Returns(["stash", "push"])
            .Returns(["stash", "list"], stdout: "stash@{0}\tOn main: somebody else's work\n")
            .Returns(["switch"]);

        SwitchOutcome outcome = await Create(git)
            .StashSwitchRestoreAsync(Repository, "main", CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.True(git.NeverCalledWith("pop"));
    }

    [Fact]
    public async Task AConflictingRestoreReportsWhereTheStashStillIs()
    {
        //CLAUDE.md: "If the restore conflicts, stop and tell the user the stash still exists and
        //how to reach it." The reference is the actionable part.
        var git = new FakeGitRunner()
            .Returns(["stash", "push"])
            .ReturnsFrom(["stash", "list"], f => $"stash@{{0}}\tOn main: {f.ArgumentAfter("-m")}\n")
            .Returns(["switch"])
            .Returns(["stash", "pop"], exitCode: 1, stderr: "CONFLICT (content): Merge conflict in src/a.cs");

        SwitchOutcome outcome = await Create(git)
            .StashSwitchRestoreAsync(Repository, "main", CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.RestoreConflicted);
        Assert.Equal("stash@{0}", outcome.StashRef);
        Assert.Equal(SwitchStep.Restore, outcome.FailedStep);
    }

    [Fact]
    public async Task AFailedSwitchAfterStashingPutsTheWorkBack()
    {
        //Leaving the user on the old branch with their changes in a stash they did not ask for
        //would be the worst outcome of the three.
        var git = new FakeGitRunner()
            .Returns(["stash", "push"])
            .ReturnsFrom(["stash", "list"], f => $"stash@{{0}}\tOn main: {f.ArgumentAfter("-m")}\n")
            .Returns(["switch"], exitCode: 128, stderr: "fatal: invalid reference")
            .Returns(["stash", "pop"]);

        SwitchOutcome outcome = await Create(git)
            .StashSwitchRestoreAsync(Repository, "nope", CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(SwitchStep.Switch, outcome.FailedStep);
        Assert.Contains(git.Invocations, i => i.Args.Contains("pop"));
    }

    [Fact]
    public async Task RemoteBranchesCreateALocalTrackingBranch()
    {
        var git = new FakeGitRunner().Returns(["switch", "--track"]);

        await Create(git).SwitchTrackingAsync(Repository, "origin/feature/x", CancellationToken.None);

        string[] args = Assert.Single(git.Invocations).Args;
        Assert.Contains("--track", args);
        Assert.Contains("origin/feature/x", args);
    }

    [Fact]
    public async Task CandidateListingSeparatesLocalFromRemoteAndDropsOriginHead()
    {
        //origin/HEAD is a symbolic ref, not a branch. Switching to it detaches HEAD, so it must
        //not appear in a picker.
        var git = new FakeGitRunner().Returns(["for-each-ref"], stdout:
            "main\trefs/heads/main\n" +
            "feature/x\trefs/heads/feature/x\n" +
            "origin/main\trefs/remotes/origin/main\n" +
            "origin/HEAD\trefs/remotes/origin/HEAD\n");

        SwitchCandidates candidates = await Create(git).ListCandidatesAsync(Repository, CancellationToken.None);

        Assert.Equal(2, candidates.Local.Count);
        Assert.Contains("main", candidates.Local);
        Assert.Equal(["origin/main"], candidates.Remote);
    }

    [Fact]
    public async Task AFailedPutBackAfterARefusedSwitchStillNamesTheStash()
    {
        //"A stash restores only the one it created" -- and says where it is when it cannot.
        //
        //Two failures at once: the switch is refused for a second reason, and the pop that would put
        //the user back conflicts. The outcome the switch produced carries a null StashRef, so
        //returning it unchanged showed the switch error over an emptied working tree and said nothing
        //at all about the stash holding the work.
        var git = new FakeGitRunner()
            .Returns(["stash", "push"])
            .ReturnsFrom(["stash", "list"], f => $"stash@{{0}}\tOn main: {f.ArgumentAfter("-m")}\n")
            .Returns(["switch"], exitCode: 128, stderr: "fatal: invalid reference: main")
            .Returns(["stash", "pop"], exitCode: 1, stderr: "CONFLICT (content): Merge conflict in src/A.cs");

        SwitchOutcome outcome = await Create(git)
            .StashSwitchRestoreAsync(Repository, "main", CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.RestoreConflicted);
        Assert.Equal("stash@{0}", outcome.StashRef);
        Assert.Equal(SwitchStep.Restore, outcome.FailedStep);

        //Both halves, because recovering needs to know the switch did not happen either.
        Assert.Contains("invalid reference", outcome.GitError);
        Assert.Contains("CONFLICT", outcome.GitError);
    }

    [Fact]
    public async Task AStashListThatCannotBeReadStopsBeforeTheSwitch()
    {
        //The same rule, from the other end. `stash push` succeeded and `stash list` did not, so
        //nothing knows where the stash is -- and switching then would leave no way to put it back.
        //Read as "nothing was stashed", this reported plain success while the user's work sat in a
        //stash nobody had named.
        var git = new FakeGitRunner()
            .Returns(["stash", "push"])
            .Returns(["stash", "list"], exitCode: 128, stderr: "fatal: not a git repository")
            .Returns(["switch"]);

        SwitchOutcome outcome = await Create(git)
            .StashSwitchRestoreAsync(Repository, "main", CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(SwitchStep.Stash, outcome.FailedStep);

        //The switch never ran, which is the whole point: it is what would have made this unrecoverable.
        Assert.DoesNotContain(git.Invocations, i => i.Args.Contains("switch"));
    }
}
