using FlickGit.Models;
using FlickGit.Remotes;
using FlickGit.Repositories;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Push, and the guardrails that stop it.
///
/// CLAUDE.md requires the guardrails to be "checked <b>before</b> executing", which is why
/// planning is a separate call that touches no network — and why every refusal below is
/// asserted to have issued no `git push` at all.
/// </summary>
public class PushServiceTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\dev\repo", "repo", HasSubmodules: false, IsBare: false);

    private static PushService Create(FakeGitRunner git) => new(git, new RepositoryService(git));

    private static RepositoryStatus Status(
        string? branch = "feature/x",
        string? upstream = "origin/feature/x",
        int ahead = 1,
        int behind = 0,
        bool detached = false,
        bool unborn = false) =>
        new()
        {
            Repository = Repository,
            Branch = branch,
            Upstream = upstream,
            Ahead = ahead,
            Behind = behind,
            IsDetachedHead = detached,
            IsUnborn = unborn,
            HeadCommit = unborn ? null : "abc1234",
        };

    [Fact]
    public async Task AnOrdinaryPushIsPlannedWhenAheadOfAnExistingUpstream()
    {
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n");

        PushPlan plan = await Create(git).PlanAsync(Repository, Status(), CancellationToken.None);

        Assert.Equal(PushAction.Push, plan.Action);
    }

    [Fact]
    public async Task PlanningTouchesNoNetwork()
    {
        //CLAUDE.md: "Explorer integration must never block on network operations." `git remote`
        //is a config read; fetch and ls-remote are not.
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n");

        await Create(git).PlanAsync(Repository, Status(), CancellationToken.None);

        Assert.True(git.NeverCalledWith("fetch"));
        Assert.True(git.NeverCalledWith("ls-remote"));
        Assert.True(git.NeverCalledWith("push"));
    }

    [Fact]
    public async Task ABranchWithNoUpstreamNeedsConsentAndPlansPushDashU()
    {
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n");

        PushPlan plan = await Create(git)
            .PlanAsync(Repository, Status(upstream: null, ahead: 0), CancellationToken.None);

        Assert.Equal(PushAction.SetUpstream, plan.Action);
        Assert.Equal("origin", plan.Remote);
    }

    [Fact]
    public async Task OriginIsPreferredWhenSeveralRemotesExist()
    {
        var git = new FakeGitRunner().Returns(["remote"], stdout: "upstream\norigin\nfork\n");

        PushPlan plan = await Create(git)
            .PlanAsync(Repository, Status(upstream: null), CancellationToken.None);

        Assert.Equal("origin", plan.Remote);
    }

    [Fact]
    public async Task ADivergedBranchIsRefusedAndNothingIsPushed()
    {
        //CLAUDE.md, "Testing": "Push is refused, with no state change, when the branch has
        //diverged." Reconciling means a rebase or a force-push, and force-push is never offered.
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n");
        PushService service = Create(git);

        PushPlan plan = await service.PlanAsync(Repository, Status(ahead: 2, behind: 3), CancellationToken.None);

        Assert.Equal(PushAction.Refuse, plan.Action);
        Assert.True(plan.HasDiverged);
        Assert.Contains("diverged", plan.Reason);

        //And executing the refusal anyway still pushes nothing.
        PushOutcome outcome = await service.ExecuteAsync(Repository, plan, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.Refused);
        Assert.True(git.NeverCalledWith("push"));
    }

    [Fact]
    public async Task ARefusalNeverMentionsForcePushing()
    {
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n");

        PushPlan plan = await Create(git).PlanAsync(Repository, Status(ahead: 1, behind: 1), CancellationToken.None);

        Assert.DoesNotContain("--force", plan.Reason);
        Assert.DoesNotContain("force-push", plan.Reason);
    }

    [Fact]
    public async Task BeingBehindPlansPullThenPushRatherThanAFailingPush()
    {
        //"Do not push and let it fail."
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n");

        PushPlan plan = await Create(git).PlanAsync(Repository, Status(ahead: 0, behind: 4), CancellationToken.None);

        Assert.Equal(PushAction.PullThenPush, plan.Action);
    }

    [Fact]
    public async Task ExecutingAPullThenPushPlanDoesNotSecretlyPull()
    {
        //A network operation and a possible rebase conflict must not hide behind a button
        //labelled Push. The UI runs the pull explicitly, through PullService.
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n");
        PushService service = Create(git);

        PushPlan plan = await service.PlanAsync(Repository, Status(ahead: 0, behind: 4), CancellationToken.None);
        PushOutcome outcome = await service.ExecuteAsync(Repository, plan, CancellationToken.None);

        Assert.True(outcome.Refused);
        Assert.True(git.NeverCalledWith("pull"));
        Assert.True(git.NeverCalledWith("push"));
    }

    [Fact]
    public async Task ARepositoryWithNoRemoteIsRefusedWithSomethingActionable()
    {
        var git = new FakeGitRunner().Returns(["remote"], stdout: string.Empty);

        PushPlan plan = await Create(git).PlanAsync(Repository, Status(upstream: null), CancellationToken.None);

        Assert.Equal(PushAction.Refuse, plan.Action);
        Assert.Contains("git remote add", plan.Reason);
    }

    [Fact]
    public async Task ADetachedHeadIsRefused()
    {
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n");

        PushPlan plan = await Create(git)
            .PlanAsync(Repository, Status(branch: null, detached: true), CancellationToken.None);

        Assert.Equal(PushAction.Refuse, plan.Action);
        Assert.Contains("detached", plan.Reason);
    }

    [Fact]
    public async Task AnUnbornBranchIsRefused()
    {
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n");

        PushPlan plan = await Create(git)
            .PlanAsync(Repository, Status(unborn: true, upstream: null), CancellationToken.None);

        Assert.Equal(PushAction.Refuse, plan.Action);
    }

    [Fact]
    public async Task AnUpToDateBranchReportsNothingToPush()
    {
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n");

        PushPlan plan = await Create(git).PlanAsync(Repository, Status(ahead: 0), CancellationToken.None);

        Assert.Equal(PushAction.NothingToPush, plan.Action);
        Assert.Contains("up to date", plan.Reason);
    }

    [Fact]
    public async Task ExecutingASetUpstreamPlanPushesWithDashUAndHead()
    {
        //`-u origin HEAD` rather than the branch name: HEAD needs no second lookup after the
        //commit surface has just created the branch, and a branch name that looks like a path
        //cannot be misread as one.
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n").Returns(["push"]);
        PushService service = Create(git);

        PushPlan plan = await service.PlanAsync(Repository, Status(upstream: null), CancellationToken.None);
        PushOutcome outcome = await service.ExecuteAsync(Repository, plan, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.CreatedUpstream);

        string[] args = Assert.Single(git.Invocations, i => i.Args.Contains("push")).Args;
        Assert.Contains("-u", args);
        Assert.Contains("origin", args);
        Assert.Contains("HEAD", args);
    }

    [Fact]
    public async Task ExecutingAnOrdinaryPlanPushesWithNoExtraArguments()
    {
        var git = new FakeGitRunner().Returns(["remote"], stdout: "origin\n").Returns(["push"]);
        PushService service = Create(git);

        PushPlan plan = await service.PlanAsync(Repository, Status(), CancellationToken.None);
        await service.ExecuteAsync(Repository, plan, CancellationToken.None);

        string[] args = Assert.Single(git.Invocations, i => i.Args.Contains("push")).Args;
        Assert.DoesNotContain("-u", args);
        Assert.DoesNotContain("--force", args);
        Assert.DoesNotContain("--force-with-lease", args);
    }

    [Fact]
    public async Task AFailedPushCarriesGitsOwnWordsAndIsNotReportedAsARefusal()
    {
        //The distinction matters to the user: refused means nothing was attempted.
        var git = new FakeGitRunner()
            .Returns(["remote"], stdout: "origin\n")
            .Returns(["push"], exitCode: 128, stderr: "fatal: Authentication failed for 'https://example.com/x.git/'");

        PushService service = Create(git);
        PushPlan plan = await service.PlanAsync(Repository, Status(), CancellationToken.None);
        PushOutcome outcome = await service.ExecuteAsync(Repository, plan, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.False(outcome.Refused);
        Assert.Contains("Authentication failed", outcome.Error);
    }
}
