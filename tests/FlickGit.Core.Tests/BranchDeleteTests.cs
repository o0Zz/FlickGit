using FlickGit.Branches;
using FlickGit.Config;
using FlickGit.Models;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Deleting branches.
///
/// In scope as a <b>safety rule</b>: CLAUDE.md lists <c>git branch -D</c> among the operations that
/// must never run without explicit intent expressed in the moment, and a remote deletion is the only
/// thing in the product that destroys state other people share. What is asserted here is almost
/// entirely what does <i>not</i> happen — that <c>-D</c> never appears unless it was asked for, that
/// the current branch costs no Git call at all, and that a push carries a fully qualified ref.
/// </summary>
public class BranchDeleteTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\dev\repo", "repo", HasSubmodules: false, IsBare: false);

    private static BranchService Create(FakeGitRunner git) => new(git, new RepositoryConfigService(git));

    /// <summary>What Git prints when the branch holds commits that are nowhere else.</summary>
    private const string UnmergedStderr =
        "error: the branch 'feature/x' is not fully merged.\n" +
        "hint: If you are sure you want to delete it, run 'git branch -D feature/x'.";

    [Fact]
    public async Task AnOrdinaryDeleteUsesTheSafeSpelling()
    {
        var git = new FakeGitRunner().Returns(["branch", "-d"]);

        BranchDeleteOutcome outcome = await Create(git)
            .DeleteLocalAsync(Repository, "feature/x", currentBranch: "main", force: false, CancellationToken.None);

        Assert.True(outcome.Succeeded);

        //The whole rule in one assertion: the destructive spelling is not reachable by default.
        Assert.True(git.NeverCalledWith("-D"));
    }

    [Fact]
    public async Task AnUnmergedBranchIsReportedRatherThanForced()
    {
        //Git refuses, and the service stops there. Escalating on the user's behalf is exactly what
        //"explicit user intent, expressed in the moment" forbids -- the window asks, and only then
        //calls again with force.
        var git = new FakeGitRunner().Returns(["branch", "-d"], exitCode: 1, stderr: UnmergedStderr);

        BranchDeleteOutcome outcome = await Create(git)
            .DeleteLocalAsync(Repository, "feature/x", currentBranch: "main", force: false, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.NotMerged);
        Assert.True(git.NeverCalledWith("-D"));
    }

    [Fact]
    public async Task ForceIsUsedOnlyWhenItWasAskedFor()
    {
        var git = new FakeGitRunner().Returns(["branch", "-D"]);

        BranchDeleteOutcome outcome = await Create(git)
            .DeleteLocalAsync(Repository, "feature/x", currentBranch: "main", force: true, CancellationToken.None);

        Assert.True(outcome.Succeeded);

        string[] args = Assert.Single(git.Invocations).Args;
        Assert.Equal(["branch", "-D", "feature/x"], args);
    }

    [Fact]
    public async Task TheCurrentBranchIsRefusedBeforeAnyCommandRuns()
    {
        var git = new FakeGitRunner();

        BranchDeleteOutcome outcome = await Create(git)
            .DeleteLocalAsync(Repository, "main", currentBranch: "main", force: true, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.WasCurrentBranch);

        //Not even with force. Nothing was asked of Git, so nothing can have changed.
        Assert.Empty(git.Invocations);
    }

    [Fact]
    public async Task ARemoteDeleteNamesTheRefInFull()
    {
        //`git push origin --delete release` would be ambiguous if a *tag* called release existed,
        //and Git resolves that ambiguity in a direction nobody wants -- a tag has no reflog.
        var git = new FakeGitRunner().Returns(["push"]);

        BranchDeleteOutcome outcome = await Create(git)
            .DeleteRemoteAsync(Repository, "origin", "release", CancellationToken.None);

        Assert.True(outcome.Succeeded);

        string[] args = Assert.Single(git.Invocations).Args;
        Assert.Equal(["push", "origin", "--delete", "refs/heads/release"], args);

        //No force, no lease, no second spelling: this pushes a deletion and nothing else.
        Assert.True(git.NeverCalledWith("--force"));
        Assert.True(git.NeverCalledWith("--force-with-lease"));
    }

    [Fact]
    public async Task ARemoteBranchIsSplitAgainstTheConfiguredRemotes()
    {
        //Not at the first slash: a branch name may contain slashes, so `origin/feature/x` is only
        //resolvable by knowing that `origin` is a remote and `feature/x` is not.
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\nupstream\n");

        RemoteBranch? resolved = await Create(git)
            .ResolveRemoteBranchAsync(Repository, "origin/feature/x", CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal("origin", resolved.Remote);
        Assert.Equal("feature/x", resolved.Branch);
    }

    [Fact]
    public async Task AnUnknownRemoteResolvesToNothing()
    {
        //The guard that stops a deletion being pushed at a remote that is not configured: a row
        //reading `fork/x` in a repository with only `origin` yields null, and the caller offers
        //nothing rather than guessing `fork`.
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n");

        Assert.Null(await Create(git)
            .ResolveRemoteBranchAsync(Repository, "fork/x", CancellationToken.None));
    }
}
