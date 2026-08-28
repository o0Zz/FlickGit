using FlickGit.Files;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The folder removal sequence.
///
/// In scope under "the sequences", and for the reason that bullet exists: the order here <i>is</i>
/// the safety rule. The Recycle Bin is the destructive step and Git is not performing it, so Git's
/// refusal has to be collected in advance — run the two the other way round and a folder holding
/// uncommitted work is gone before anything objects. Every step still reports success, so the bug is
/// invisible from the outside and a click would never reveal it.
/// </summary>
public class FolderRemovalFlowTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\dev\repo", "repo", HasSubmodules: false, IsBare: false, GitDirectory: @"C:\dev\repo\.git");

    /// <summary>A runner where the folder holds two tracked files and one Git has never seen.</summary>
    private static FakeGitRunner Populated() =>
        new FakeGitRunner()
            .Returns(["ls-files", "-z", "--"], stdout: "src/Legacy/a.cs\0src/Legacy/b.cs\0")
            .Returns(["--others"], stdout: "src/Legacy/dump.json\0")
            .Returns(["rm"]);

    private static FolderRemovalFlow Create(FakeGitRunner git) =>
        new(new TrackingService(git, new RepositoryService(git), NullLog.Instance), NullLog.Instance);

    [Fact]
    public async Task TheHappyPathGatesFirstBinsSecondAndRecordsLast()
    {
        FakeGitRunner git = Populated();
        var order = new List<string>();

        FolderRemoval result = await Create(git).RunAsync(
            Repository,
            "src/Legacy",
            _ => { order.Add("ask"); return Task.FromResult(true); },
            () => { order.Add("bin"); return Task.FromResult(TrackingResult.Ok); },
            default);

        Assert.Equal(FolderRemovalOutcome.Removed, result.Outcome);
        Assert.Equal(2, result.TrackedFiles);

        //The dry run before the bin, and the recording after it. Nothing else is a safe ordering.
        int gate = git.Invocations.FindIndex(i => i.Args.Contains("--dry-run"));
        int record = git.Invocations.FindIndex(i => i.Args.Contains("--cached"));

        Assert.True(gate >= 0);
        Assert.True(record > gate);
        Assert.Equal(["ask", "bin"], order);
    }

    [Fact]
    public async Task AGateThatRefusesLeavesTheFolderOnDiskAndAsksNothing()
    {
        //The single most important behaviour in this file. Git says a tracked file inside holds
        //uncommitted work, so the user is never asked and the Recycle Bin is never reached.
        FakeGitRunner git = Populated().Returns(
            ["--dry-run"],
            exitCode: 1,
            stderr: "error: the following file has local modifications:\n    src/Legacy/a.cs");

        bool asked = false;
        bool binned = false;

        FolderRemoval result = await Create(git).RunAsync(
            Repository,
            "src/Legacy",
            _ => { asked = true; return Task.FromResult(true); },
            () => { binned = true; return Task.FromResult(TrackingResult.Ok); },
            default);

        Assert.Equal(FolderRemovalOutcome.Refused, result.Outcome);
        Assert.Contains("src/Legacy/a.cs", result.Error);

        Assert.False(asked);
        Assert.False(binned);
        Assert.True(git.NeverCalledWith("--cached"));
    }

    [Fact]
    public async Task DecliningTheQuestionReachesNeitherTheBinNorTheIndex()
    {
        FakeGitRunner git = Populated();
        bool binned = false;

        FolderRemoval result = await Create(git).RunAsync(
            Repository,
            "src/Legacy",
            _ => Task.FromResult(false),
            () => { binned = true; return Task.FromResult(TrackingResult.Ok); },
            default);

        Assert.Equal(FolderRemovalOutcome.Declined, result.Outcome);
        Assert.False(binned);
        Assert.True(git.NeverCalledWith("--cached"));
    }

    [Fact]
    public async Task AFolderGitHasNothingUnderIsRefusedBeforeTheGateRuns()
    {
        //A question about an operation that cannot happen is worse than the refusal it precedes --
        //and `git rm` on it would answer `fatal: pathspec … did not match any files`, which is
        //accurate about something nobody asked.
        var git = new FakeGitRunner().Returns(["ls-files"], stdout: string.Empty);
        bool asked = false;

        FolderRemoval result = await Create(git).RunAsync(
            Repository,
            "scratch",
            _ => { asked = true; return Task.FromResult(true); },
            () => Task.FromResult(TrackingResult.Ok),
            default);

        Assert.Equal(FolderRemovalOutcome.NotTracked, result.Outcome);
        Assert.False(asked);
        Assert.True(git.NeverCalledWith("--dry-run"));
        Assert.True(git.NeverCalledWith("rm"));
    }

    [Fact]
    public async Task ABinThatFailsLeavesTheIndexAlone()
    {
        FakeGitRunner git = Populated();

        FolderRemoval result = await Create(git).RunAsync(
            Repository,
            "src/Legacy",
            _ => Task.FromResult(true),
            () => Task.FromResult(TrackingResult.Failed("The folder is open in another program.")),
            default);

        Assert.Equal(FolderRemovalOutcome.BinFailed, result.Outcome);
        Assert.Contains("another program", result.Error);

        //Nothing was deleted, so nothing may be recorded as deleted.
        Assert.True(git.NeverCalledWith("--cached"));
    }

    [Fact]
    public async Task TheQuestionCarriesBothCounts()
    {
        //The counts are the whole reason the folder half asks at all: the number of files is the one
        //part of the blast radius the user cannot see before answering.
        FakeGitRunner git = Populated();
        FolderRemovalPlan? seen = null;

        await Create(git).RunAsync(
            Repository,
            "src/Legacy",
            plan => { seen = plan; return Task.FromResult(false); },
            () => Task.FromResult(TrackingResult.Ok),
            default);

        Assert.NotNull(seen);
        Assert.Equal(2, seen.TrackedFiles);
        Assert.Equal(1, seen.UntrackedFiles);
    }

    [Fact]
    public async Task AFailedRecordingSaysSoWhileTheFolderIsAlreadyInTheBin()
    {
        //The one outcome after which the working tree and the index disagree. It has its own value
        //so the verb can name the Recycle Bin, which is the way back.
        FakeGitRunner git = Populated().Returns(["--cached"], exitCode: 1, stderr: "fatal: index file corrupt");

        FolderRemoval result = await Create(git).RunAsync(
            Repository,
            "src/Legacy",
            _ => Task.FromResult(true),
            () => Task.FromResult(TrackingResult.Ok),
            default);

        Assert.Equal(FolderRemovalOutcome.RecordFailed, result.Outcome);
        Assert.Contains("index file corrupt", result.Error);
    }
}
