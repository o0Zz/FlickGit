using FlickGit.Branches;
using FlickGit.Commits;
using FlickGit.Config;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Pulls;
using FlickGit.Remotes;
using FlickGit.Repositories;
using FlickGit.Status;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The commit sequence, end to end, with Git faked.
///
/// These are the behavioural tests CLAUDE.md lists under "Testing" for the branch ComboBox, and
/// until this sequence moved out of the commit window there was nowhere to write them:
///
/// <list type="bullet">
/// <item><description>"typing the current branch performs no switch at all"</description></item>
/// <item><description>"an existing branch switches, refreshes the file list, and aborts when a
/// selected file changed as a result of the switch"</description></item>
/// <item><description>"an invalid ref name is rejected before any Git command runs"</description></item>
/// <item><description>"a new branch is created, committed to, and pushed with `-u`"</description></item>
/// </list>
///
/// Each one is about <b>ordering</b> or about <b>not doing something</b>, which is exactly what a
/// click-through cannot demonstrate.
/// </summary>
public sealed class CommitFlowTests : IDisposable
{
    private readonly string _root;
    private readonly RepositoryInfo _repository;

    public CommitFlowTests()
    {
        //A real directory, because CommitService writes its message file into .git.
        _root = Path.Combine(Path.GetTempPath(), $"flickgit-flow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, ".git"));

        _repository = new RepositoryInfo(_root, Path.GetFileName(_root), HasSubmodules: false, IsBare: false);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static string Stream(params string[] records) =>
        string.Concat(records.Select(r => r + '\0'));

    /// <summary>One modified, staged file — the ordinary case.</summary>
    private const string OneStagedFile = "1 M. N... 100644 100644 100644 aa bb src/a.cs";

    private CommitFlow Create(FakeGitRunner git)
    {
        var repositories = new RepositoryService(git);

        return new CommitFlow(
            new StatusService(git, new UntrackedFileMeasurer()),
            new CommitService(git, repositories, NullLog.Instance),
            new BranchService(git, new RepositoryConfigService(git)),
            new SwitchService(git, repositories, NullLog.Instance),
            new PushService(git, repositories),
            new PullService(git, repositories),
            NullLog.Instance);
    }

    /// <summary>A runner that answers everything the happy path asks for.</summary>
    private static FakeGitRunner HappyPath(string statusStream = OneStagedFile) =>
        new FakeGitRunner()
            .Returns(["status"], Stream("# branch.head main", statusStream))
            .Returns(["diff", "--numstat"], string.Empty)
            .Returns(["diff", "--cached", "--quiet"], exitCode: 1)   // something is staged
            .Returns(["add"])
            .Returns(["restore"])
            .Returns(["commit"])
            .Returns(["rev-parse", "--short"], stdout: "abc1234\n")
            .Returns(["remote"], stdout: "origin\n")
            .Returns(["for-each-ref"], stdout: "main\n")
            .Returns(["check-ref-format"])
            .Returns(["switch"])
            .Returns(["push"]);

    private CommitRequest Request(
        FakeGitRunner git,
        string? targetBranch = null,
        bool create = false,
        bool push = false,
        bool consent = true) =>
        new()
        {
            Repository = _repository,
            Message = "feat: a thing",
            SelectedPaths = ["src/a.cs"],
            TargetBranch = targetBranch,
            CreateBranch = create,
            Push = push,
            Confirm = (_, _) => Task.FromResult(consent),
        };

    // ---- staging ------------------------------------------------------------------

    [Fact]
    public async Task TheSelectionIsStagedBeforeAnythingElseHappens()
    {
        //Staging is index-based and survives a switch, which is the whole reason it goes first.
        var git = HappyPath();

        await Create(git).RunAsync(Request(git), CancellationToken.None);

        int add = git.Invocations.FindIndex(i => i.Args.Contains("add"));
        int commit = git.Invocations.FindIndex(i => i.Args.Contains("commit"));

        Assert.True(add >= 0 && add < commit, $"add at {add}, commit at {commit}");
    }

    [Fact]
    public async Task UntickedButStagedFilesComeBackOutOfTheIndex()
    {
        //`git commit` commits the index. Leaving an unticked file staged would commit a file the
        //user excluded, and the unticking would have done nothing.
        var git = HappyPath();

        CommitRequest request = Request(git) with { PathsToUnstage = ["src/excluded.cs"] };

        await Create(git).RunAsync(request, CancellationToken.None);

        FakeGitRunner.Invocation restore = Assert.Single(git.Invocations, i => i.Args.Contains("restore"));
        Assert.Contains("src/excluded.cs", restore.Args);
    }

    [Fact]
    public async Task NothingStagedIsReportedRatherThanCommittingAnEmptyTree()
    {
        //A file whose only change was already staged and then reverted on disk stages to nothing.
        var git = HappyPath().Returns(["diff", "--cached", "--quiet"]);   // exit 0: nothing staged

        CommitFlowResult result = await Create(git).RunAsync(Request(git), CancellationToken.None);

        Assert.Equal(CommitFlowOutcome.NothingToCommit, result.Outcome);
        Assert.True(git.NeverCalledWith("commit"));
    }

    // ---- the branch ComboBox ------------------------------------------------------

    [Fact]
    public async Task StayingOnTheCurrentBranchPerformsNoSwitchAtAll()
    {
        //CLAUDE.md, "Testing": "typing the current branch performs no switch at all". The caller
        //signals that by leaving TargetBranch null, and the flow must not spend a process on it.
        var git = HappyPath();

        CommitFlowResult result = await Create(git).RunAsync(Request(git), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(git.NeverCalledWith("switch"));
        Assert.True(git.NeverCalledWith("checkout"));
        Assert.True(git.NeverCalledWith("check-ref-format"));
    }

    [Fact]
    public async Task AnExistingBranchIsSwitchedToBeforeTheCommit()
    {
        var git = HappyPath();

        CommitFlowResult result = await Create(git)
            .RunAsync(Request(git, targetBranch: "develop"), CancellationToken.None);

        Assert.True(result.Succeeded);

        int switched = git.Invocations.FindIndex(i => i.Args.Contains("switch"));
        int committed = git.Invocations.FindIndex(i => i.Args.Contains("commit"));

        //Order matters: the other way round commits to the branch the user was leaving.
        Assert.True(switched >= 0 && switched < committed, $"switch at {switched}, commit at {committed}");

        //An existing branch is not validated and not created.
        Assert.True(git.NeverCalledWith("check-ref-format"));
        Assert.DoesNotContain("-c", git.Invocations.First(i => i.Args.Contains("switch")).Args);
    }

    [Fact]
    public async Task ANewBranchIsValidatedThenCreatedThenCommittedThenPushedWithUpstream()
    {
        //CLAUDE.md, "Testing": "a new branch is created, committed to, and pushed with `-u`".
        var git = HappyPath()
            //No upstream on the new branch, so the push has to create one.
            .Returns(["status"], Stream("# branch.head feature/new", OneStagedFile));

        CommitFlowResult result = await Create(git)
            .RunAsync(Request(git, targetBranch: "feature/new", create: true, push: true), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(result.Pushed);

        int validate = git.Invocations.FindIndex(i => i.Args.Contains("check-ref-format"));
        int create = git.Invocations.FindIndex(i => i.Args.Contains("switch") && i.Args.Contains("-c"));
        int commit = git.Invocations.FindIndex(i => i.Args.Contains("commit"));
        int push = git.Invocations.FindIndex(i => i.Args.Contains("push") && !i.Args.Contains("stash"));

        Assert.True(validate < create, "validated before created");
        Assert.True(create < commit, "created before committed");
        Assert.True(commit < push, "committed before pushed");

        string[] pushArgs = git.Invocations[push].Args;
        Assert.Contains("-u", pushArgs);
        Assert.Contains("HEAD", pushArgs);
    }

    [Fact]
    public async Task AnInvalidBranchNameStopsBeforeAnythingIsCreatedOrCommitted()
    {
        var git = HappyPath()
            .Returns(["check-ref-format"], exitCode: 1, stderr: "fatal: 'bad..name' is not a valid branch name");

        CommitFlowResult result = await Create(git)
            .RunAsync(Request(git, targetBranch: "bad..name", create: true), CancellationToken.None);

        Assert.Equal(CommitFlowOutcome.InvalidBranchName, result.Outcome);
        Assert.Contains("not a valid branch name", result.Detail);

        //Nothing was created and nothing was committed.
        Assert.True(git.NeverCalledWith("-c"));
        Assert.True(git.NeverCalledWith("commit"));
    }

    [Fact]
    public async Task ARefusedSwitchStopsTheCommitAndNamesTheBlockingFiles()
    {
        var git = HappyPath().Returns(["switch"], exitCode: 1, stderr:
            "error: Your local changes to the following files would be overwritten by checkout:\n" +
            "\tsrc/a.cs\n" +
            "Please commit your changes or stash them before you switch branches.");

        CommitFlowResult result = await Create(git)
            .RunAsync(Request(git, targetBranch: "develop"), CancellationToken.None);

        Assert.Equal(CommitFlowOutcome.SwitchRefused, result.Outcome);
        Assert.Contains("src/a.cs", result.Files);

        //Nothing committed, and nothing stashed on the user's behalf.
        Assert.True(git.NeverCalledWith("commit"));
        Assert.True(git.NeverCalledWith("stash"));
    }

    // ---- the abort-on-change rule -------------------------------------------------

    [Fact]
    public async Task ASelectedFileThatChangedBecauseOfTheSwitchAbortsTheCommit()
    {
        //THE rule this whole ordering exists for. The diff the user reviewed was computed against
        //the old branch's HEAD; if the switch moved a file they ticked, committing would commit
        //content they never saw.
        //
        //The two status calls straddle the switch, so the fake answers differently each time.
        FakeGitRunner git = SwitchChangesFile();

        CommitFlowResult result = await Create(git)
            .RunAsync(Request(git, targetBranch: "develop"), CancellationToken.None);

        Assert.Equal(CommitFlowOutcome.AbortedSelectionChanged, result.Outcome);
        Assert.Contains("src/a.cs", result.Files);

        //Nothing was committed, and the caller was handed the list it must now show.
        Assert.True(git.NeverCalledWith("commit"));
        Assert.NotNull(result.RefreshedStatus);
    }

    [Fact]
    public async Task AnUnselectedFileChangingDoesNotAbortTheCommit()
    {
        //It is not going into the commit, so it cannot make the commit wrong.
        FakeGitRunner git = SwitchChangesFile(changedPath: "src/untouched.cs");

        CommitFlowResult result = await Create(git)
            .RunAsync(Request(git, targetBranch: "develop"), CancellationToken.None);

        Assert.True(result.Succeeded);
    }

    // ---- push guardrails ----------------------------------------------------------

    [Fact]
    public async Task ADivergedBranchIsCommittedButNotPushed()
    {
        //The commit stands; the push is refused. Both facts are reported, because telling the user
        //only that the push failed would leave them wondering whether the commit happened.
        var git = HappyPath()
            .Returns(["status"], Stream(
                "# branch.head main",
                "# branch.upstream origin/main",
                "# branch.ab +1 -1",
                OneStagedFile));

        CommitFlowResult result = await Create(git)
            .RunAsync(Request(git, push: true), CancellationToken.None);

        Assert.Equal(CommitFlowOutcome.PushRefused, result.Outcome);
        Assert.NotNull(result.Commit);
        Assert.Contains("diverged", result.Detail);
        Assert.True(git.NeverCalledWith("push"));
    }

    [Fact]
    public async Task DecliningTheUpstreamQuestionCommitsAndPushesNothing()
    {
        var git = HappyPath();

        CommitFlowResult result = await Create(git)
            .RunAsync(Request(git, push: true, consent: false), CancellationToken.None);

        Assert.Equal(CommitFlowOutcome.Cancelled, result.Outcome);
        Assert.NotNull(result.Commit);
        Assert.True(git.NeverCalledWith("push"));
    }

    [Fact]
    public async Task AnUnansweredGuardrailIsTreatedAsNo()
    {
        //No Confirm callback at all. A guardrail that treated silence as consent would not be one.
        var git = HappyPath();

        CommitRequest request = Request(git, push: true) with { Confirm = null };

        CommitFlowResult result = await Create(git).RunAsync(request, CancellationToken.None);

        Assert.Equal(CommitFlowOutcome.Cancelled, result.Outcome);
        Assert.True(git.NeverCalledWith("push"));
    }

    [Fact]
    public async Task NotAskingToPushRunsNoPushAndAsksNothing()
    {
        var git = HappyPath();
        bool asked = false;

        CommitRequest request = Request(git) with
        {
            Confirm = (_, _) =>
            {
                asked = true;
                return Task.FromResult(true);
            },
        };

        CommitFlowResult result = await Create(git).RunAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.False(result.Pushed);
        Assert.False(asked);
        Assert.True(git.NeverCalledWith("push"));
        Assert.True(git.NeverCalledWith("remote"));
    }

    /// <summary>
    /// A runner whose <b>second</b> `status` reports a different file, standing in for a switch
    /// that changed the working tree under the user.
    ///
    /// The two status calls straddle the switch — that is the whole point of taking a baseline
    /// before it — so the fake answers by counting how many it has been asked so far.
    /// </summary>
    private static FakeGitRunner SwitchChangesFile(string changedPath = "src/a.cs")
    {
        var git = new FakeGitRunner()
            .Returns(["diff", "--numstat"], string.Empty)
            .Returns(["diff", "--cached", "--quiet"], exitCode: 1)
            .Returns(["add"])
            .Returns(["commit"])
            .Returns(["rev-parse", "--short"], stdout: "abc1234\n")
            .Returns(["switch"])
            .Returns(["for-each-ref"], stdout: "main\ndevelop\n");

        //After the switch, src/a.cs is reported exactly as before and `changedPath` is reported as
        //having moved. When changedPath *is* src/a.cs the selected file changed; when it is another
        //file, the selected one is untouched and the commit must go ahead.
        const string unchanged = "1 M. N... 100644 100644 100644 aa bb src/a.cs";
        string moved = $"1 MM N... 100644 100644 100644 aa cc {changedPath}";

        return git.ReturnsFrom(["status"], self =>
            self.Invocations.Count(i => i.Args.Contains("status")) <= 1
                ? Stream("# branch.head main", unchanged)
                : changedPath == "src/a.cs"
                    ? Stream("# branch.head develop", moved)
                    : Stream("# branch.head develop", unchanged, moved));
    }
}
