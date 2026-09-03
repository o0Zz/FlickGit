using FlickGit.Branches;
using FlickGit.Commits;
using FlickGit.Config;
using FlickGit.Git;
using FlickGit.Logging;
using FlickGit.Merges;
using FlickGit.Models;
using FlickGit.Pulls;
using FlickGit.Repositories;
using FlickGit.Status;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Back to the primary branch, end to end, with Git faked.
///
/// Every test here is about <b>ordering</b> or about <b>not doing something</b> — which is exactly
/// what clicking the menu entry cannot demonstrate. CLAUDE.md, Hard Requirement 4: "The sequences",
/// and "The safety rules" for the two that assert nothing happened.
/// </summary>
public sealed class PrimaryBranchFlowTests : IDisposable
{
    private readonly string _root;
    private readonly RepositoryInfo _repository;

    public PrimaryBranchFlowTests()
    {
        //A real directory, because MergeStateService answers from file probes over the Git directory
        //rather than from git.exe — so the operation-in-progress test needs a real rebase-merge.
        _root = Path.Combine(Path.GetTempPath(), $"flickgit-primary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, ".git"));

        _repository = Repository(hasSubmodules: false);
    }

    private RepositoryInfo Repository(bool hasSubmodules) =>
        new(_root,
            Path.GetFileName(_root),
            HasSubmodules: hasSubmodules,
            IsBare: false,
            GitDirectory: Path.Combine(_root, ".git"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static string Stream(params string[] records) =>
        string.Concat(records.Select(r => r + '\0'));

    private static PrimaryBranchFlow Create(FakeGitRunner git)
    {
        var repositories = new RepositoryService(git);
        var merges = new MergeStateService();

        return new PrimaryBranchFlow(
            new StatusService(git, new UntrackedFileMeasurer(), merges, new PreparedMessageService()),
            new BranchService(git, new RepositoryConfigService(git)),
            new SwitchService(git, repositories, NullLog.Instance),
            new PullService(git, repositories, merges),
            NullLog.Instance);
    }

    /// <summary>
    /// A runner answering the status read and the pull. The primary branch is supplied on the request
    /// as the user's setting, so resolution costs one `config --get` that finds nothing.
    /// </summary>
    private static FakeGitRunner Runner(string currentBranch) =>
        new FakeGitRunner()
            .Returns(["status"], Stream($"# branch.head {currentBranch}"))
            .Returns(["diff", "--numstat"], string.Empty)
            .Returns(["diff", "--cached", "--numstat"], string.Empty)
            .Returns(["pull", "--rebase", "--autostash"]);

    private PrimaryBranchRequest Request(
        Func<PrimaryBranchQuestion, CancellationToken, Task<bool>>? confirm = null,
        bool hasSubmodules = false) =>
        new()
        {
            Repository = hasSubmodules ? Repository(hasSubmodules: true) : _repository,
            ConfiguredPrimaryBranch = "develop",
            Confirm = confirm,
        };

    /// <summary>What Git prints when local changes block a switch. Tab-indented, which is the marker.</summary>
    private const string Blocked =
        "error: Your local changes to the following files would be overwritten by checkout:\n" +
        "\tsrc/GatewayClient.cs\n" +
        "Please commit your changes or stash them before you switch branches.\n";

    /// <summary>
    /// Refuses the plain switch and lets the one inside stash/switch/restore succeed.
    ///
    /// Both carry the same arguments, so the answer has to depend on which call it is — which is the
    /// whole reason the flow's ordering is testable at all.
    /// </summary>
    private static void RefuseTheFirstSwitch(FakeGitRunner git)
    {
        int attempts = 0;

        git.ReturnsResultFrom(["switch", "develop"], _ =>
            ++attempts == 1
                ? new GitResult(1, string.Empty, Blocked, TimeSpan.Zero)
                : new GitResult(0, string.Empty, string.Empty, TimeSpan.Zero));
    }

    /// <summary>
    /// "The sequences" — already on the primary branch performs no switch at all, and still pulls.
    /// The entry is *and pull*, not *switch and maybe pull*.
    /// </summary>
    [Fact]
    public async Task Already_on_the_primary_branch_runs_no_switch()
    {
        FakeGitRunner git = Runner("develop");

        PrimaryBranchResult result = await Create(git).RunAsync(Request(), CancellationToken.None);

        Assert.Equal(PrimaryBranchOutcome.Done, result.Outcome);
        Assert.Equal("develop", result.Branch);
        Assert.False(result.Switched);

        Assert.True(git.NeverCalledWith("switch"));
        Assert.Single(git.Invocations, i => i.Args.Contains("pull"));
    }

    /// <summary>
    /// "The sequences" — a rebase in progress is refused before Git is asked to do anything. Not
    /// left to `git switch` to refuse, and above all never abandoned on the user's behalf.
    /// </summary>
    [Fact]
    public async Task A_rebase_in_progress_refuses_before_any_command_runs()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "rebase-merge"));

        FakeGitRunner git = Runner("feature/storage-gw");

        PrimaryBranchResult result = await Create(git).RunAsync(Request(), CancellationToken.None);

        Assert.Equal(PrimaryBranchOutcome.OperationInProgress, result.Outcome);
        Assert.Equal(MergeOperation.Rebase, result.InProgress);

        //Neither the switch nor the pull, and not even the config read the resolution would have cost.
        Assert.True(git.NeverCalledWith("switch"));
        Assert.True(git.NeverCalledWith("pull"));
        Assert.True(git.NeverCalledWith("config"));
    }

    /// <summary>
    /// "The safety rules" — a blocked switch changes nothing. With nobody to ask, the stash is not
    /// taken, and the pull does not run either: the repository is still on the branch it started on.
    /// </summary>
    [Fact]
    public async Task A_blocked_switch_stashes_nothing_when_there_is_nobody_to_ask()
    {
        FakeGitRunner git = Runner("feature/storage-gw")
            .Returns(["switch", "develop"], exitCode: 1, stderr: Blocked);

        PrimaryBranchResult result = await Create(git)
            .RunAsync(Request(confirm: null), CancellationToken.None);

        Assert.Equal(PrimaryBranchOutcome.SwitchRefused, result.Outcome);
        Assert.Equal(["src/GatewayClient.cs"], result.Files);

        Assert.True(git.NeverCalledWith("stash"));
        Assert.True(git.NeverCalledWith("pull"));
    }

    /// <summary>
    /// "The sequences" — a restore that conflicted stops before the pull. The user is on the primary
    /// branch with their work in a stash, and `pull --rebase` there rebases onto a tree they have not
    /// got back, with `--autostash` finding nothing dirty to protect.
    /// </summary>
    [Fact]
    public async Task A_conflicted_stash_restore_stops_before_the_pull()
    {
        FakeGitRunner git = Runner("feature/storage-gw")
            .Returns(["stash", "push"])
            //Echo the message back, so the service finds its own stash by message rather than by index.
            .ReturnsFrom(["stash", "list"], self => $"stash@{{0}}\tOn develop: {self.ArgumentAfter("-m")}\n")
            .Returns(["stash", "pop"], exitCode: 1, stderr: "CONFLICT (content): Merge conflict in src/GatewayClient.cs\n");

        //Refused first, then the same command has to succeed inside the stash path. Both calls carry
        //identical arguments, so only a stateful answer can tell them apart.
        RefuseTheFirstSwitch(git);

        PrimaryBranchResult result = await Create(git)
            .RunAsync(Request(confirm: (_, _) => Task.FromResult(true)), CancellationToken.None);

        Assert.Equal(PrimaryBranchOutcome.StashSwitchFailed, result.Outcome);
        Assert.Equal("stash@{0}", result.StashRef);
        Assert.True(result.RestoreConflicted);
        Assert.Equal(SwitchStep.Restore, result.FailedStep);

        Assert.True(git.NeverCalledWith("pull"));
    }

    /// <summary>
    /// "The sequences" — a submodule failure is a warning on a successful run. Reporting it as a
    /// failure would invite the user to try to undo a pull that worked.
    /// </summary>
    [Fact]
    public async Task A_submodule_failure_is_a_warning_on_a_successful_run()
    {
        FakeGitRunner git = Runner("feature/storage-gw")
            .Returns(["switch", "develop"])
            .Returns(["submodule", "update"], exitCode: 1, stderr: "fatal: could not read Username\n");

        PrimaryBranchResult result = await Create(git)
            .RunAsync(Request(hasSubmodules: true), CancellationToken.None);

        Assert.Equal(PrimaryBranchOutcome.Done, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.True(result.Switched);
        Assert.Contains("Username", result.SubmoduleError);
    }

    /// <summary>
    /// "The safety rules" — no command this flow can issue ever carries force, on any of its vectors.
    /// Driven down the longest path it has, so the switch, the stash, the restore and the pull are all
    /// in the log.
    /// </summary>
    [Fact]
    public async Task No_command_this_flow_issues_ever_carries_force()
    {
        FakeGitRunner git = Runner("feature/storage-gw")
            .Returns(["stash", "push"])
            .ReturnsFrom(["stash", "list"], self => $"stash@{{0}}\tOn develop: {self.ArgumentAfter("-m")}\n")
            .Returns(["stash", "pop"]);

        RefuseTheFirstSwitch(git);

        PrimaryBranchResult result = await Create(git)
            .RunAsync(Request(confirm: (_, _) => Task.FromResult(true)), CancellationToken.None);

        Assert.Equal(PrimaryBranchOutcome.Done, result.Outcome);
        Assert.True(result.Stashed);

        Assert.True(git.NeverCalledWith("--force"));
        Assert.True(git.NeverCalledWith("-f"));
        Assert.True(git.NeverCalledWith("--hard"));
        Assert.DoesNotContain(git.Invocations, i => i.Args.Any(a => a.StartsWith('+')));
    }
}
