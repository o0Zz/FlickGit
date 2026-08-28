using FlickGit.Files;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The removal sequence, over a selection.
///
/// In scope under "the sequences", and for the reason that bullet exists: the order here <i>is</i>
/// the safety rule. The Recycle Bin is the destructive step and Git is not performing it, so Git's
/// refusal has to be collected in advance — run the two the other way round and a folder holding
/// uncommitted work is gone before anything objects. Every step still reports success, so the bug is
/// invisible from the outside and a click would never reveal it.
///
/// A selection adds a second half to that rule: <b>every</b> target is gated before the one question,
/// not each as its turn comes. Asking per item would run the gate for the fifth only after the first
/// four had already gone, which is the same bug wearing a different hat.
/// </summary>
public class RemovalFlowTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\dev\repo", "repo", HasSubmodules: false, IsBare: false, GitDirectory: @"C:\dev\repo\.git");

    /// <summary>A runner where every target holds two tracked files and one Git has never seen.</summary>
    private static FakeGitRunner Populated() =>
        new FakeGitRunner()
            .Returns(["ls-files", "-z", "--"], stdout: "src/Legacy/a.cs\0src/Legacy/b.cs\0")
            .Returns(["--others"], stdout: "src/Legacy/dump.json\0")
            .Returns(["rm"]);

    private static RemovalFlow Create(FakeGitRunner git) =>
        new(new TrackingService(git, new RepositoryService(git), NullLog.Instance), NullLog.Instance);

    private static RemovalTarget Folder(string path) => new(path, IsFolder: true);

    private static RemovalTarget File(string path) => new(path, IsFolder: false);

    [Fact]
    public async Task TheHappyPathGatesFirstBinsSecondAndRecordsLast()
    {
        FakeGitRunner git = Populated();
        var order = new List<string>();

        Removal result = await Create(git).RunAsync(
            Repository,
            [Folder("src/Legacy")],
            _ => { order.Add("ask"); return Task.FromResult(true); },
            _ => { order.Add("bin"); return Task.FromResult(TrackingResult.Ok); },
            default);

        Assert.Equal(RemovalOutcome.Removed, result.Outcome);
        Assert.Equal(2, result.TrackedFiles);

        //The dry run before the bin, and the recording after it. Nothing else is a safe ordering.
        int gate = git.Invocations.FindIndex(i => i.Args.Contains("--dry-run"));
        int record = git.Invocations.FindIndex(i => i.Args.Contains("--cached"));

        Assert.True(gate >= 0);
        Assert.True(record > gate);
        Assert.Equal(["ask", "bin"], order);
    }

    [Fact]
    public async Task AGateThatRefusesLeavesTheSelectionOnDiskAndAsksNothing()
    {
        //The single most important behaviour in this file. Git says a tracked file inside holds
        //uncommitted work, so the user is never asked and the Recycle Bin is never reached.
        FakeGitRunner git = Populated().Returns(
            ["-r", "--dry-run"],
            exitCode: 1,
            stderr: "error: the following file has local modifications:\n    src/Legacy/a.cs");

        bool asked = false;
        bool binned = false;

        Removal result = await Create(git).RunAsync(
            Repository,
            [Folder("src/Legacy")],
            _ => { asked = true; return Task.FromResult(true); },
            _ => { binned = true; return Task.FromResult(TrackingResult.Ok); },
            default);

        Assert.Equal(RemovalOutcome.Refused, result.Outcome);
        Assert.Contains("src/Legacy/a.cs", result.Error);

        Assert.False(asked);
        Assert.False(binned);
        Assert.True(git.NeverCalledWith("--cached"));
    }

    [Fact]
    public async Task OneRefusedTargetRefusesTheWholeSelection()
    {
        //In scope under "the sequences". Half a removal is the state the user cannot reason about:
        //nothing on disk says which half happened, and the question they answered described all of it.
        //The second folder is the one Git objects to, and the first must not go anyway.
        FakeGitRunner git = Populated().Returns(
            ["-r", "--dry-run", "--", ":(literal)src/Old"],
            exitCode: 1,
            stderr: "error: the following file has local modifications:\n    src/Old/c.cs");

        bool asked = false;
        bool binned = false;

        Removal result = await Create(git).RunAsync(
            Repository,
            [Folder("src/Legacy"), Folder("src/Old"), File("src/a.cs")],
            _ => { asked = true; return Task.FromResult(true); },
            _ => { binned = true; return Task.FromResult(TrackingResult.Ok); },
            default);

        Assert.Equal(RemovalOutcome.Refused, result.Outcome);
        Assert.Equal("src/Old", result.Path);
        Assert.Equal(0, result.Done);

        Assert.False(asked);
        Assert.False(binned);
        Assert.True(git.NeverCalledWith("--cached"));

        //Nothing was removed either -- not the file, and not the folder Git was happy about.
        Assert.DoesNotContain(git.Invocations, i => i.Args is ["rm", "--", ..]);
    }

    [Fact]
    public async Task EveryGateRunsBeforeTheOneQuestion()
    {
        //In scope under "the sequences", and the invariant the whole selection feature turns on:
        //gate-before-ask-before-destroy across the batch, not per member.
        FakeGitRunner git = Populated();

        int callsWhenAsked = -1;
        int callsWhenFirstBinned = -1;

        Removal result = await Create(git).RunAsync(
            Repository,
            [Folder("src/Legacy"), Folder("src/Old"), File("src/a.cs"), File("src/b.cs")],
            _ =>
            {
                callsWhenAsked = git.Invocations.Count;
                return Task.FromResult(true);
            },
            _ =>
            {
                if (callsWhenFirstBinned < 0)
                    callsWhenFirstBinned = git.Invocations.Count;

                return Task.FromResult(TrackingResult.Ok);
            },
            default);

        Assert.Equal(RemovalOutcome.Removed, result.Outcome);
        Assert.True(callsWhenAsked >= 0);

        //`rm --dry-run` is the file gate; `rm -r --dry-run` is a folder's. Both spellings, all three
        //targets' worth, before the question.
        int fileGate = git.Invocations.FindIndex(i => i.Args is ["rm", "--dry-run", ..]);

        int[] folderGates =
        [
            .. git.Invocations
                .Select((invocation, at) => (invocation, at))
                .Where(x => x.invocation.Args is ["rm", "-r", "--dry-run", ..])
                .Select(x => x.at),
        ];

        Assert.True(fileGate >= 0);
        Assert.Equal(2, folderGates.Length);
        Assert.True(fileGate < callsWhenAsked);
        Assert.All(folderGates, at => Assert.True(at < callsWhenAsked));

        //And the question before anything was destroyed.
        Assert.True(callsWhenAsked <= callsWhenFirstBinned);
    }

    [Fact]
    public async Task TheFilesGoInOneCommandBeforeAnyFolderReachesTheBin()
    {
        //In scope under "the sequences". `git rm` is all-or-nothing over its pathspecs and only ever
        //removes what HEAD still has, so it is the recoverable half -- and it runs while the folders
        //are all still on disk. Stopping part-way should stop before the irreversible half.
        FakeGitRunner git = Populated();
        int callsWhenFirstBinned = -1;

        await Create(git).RunAsync(
            Repository,
            [Folder("src/Legacy"), File("src/a.cs"), File("src/b.cs")],
            _ => Task.FromResult(true),
            _ =>
            {
                if (callsWhenFirstBinned < 0)
                    callsWhenFirstBinned = git.Invocations.Count;

                return Task.FromResult(TrackingResult.Ok);
            },
            default);

        int removal = git.Invocations.FindIndex(i => i.Args is ["rm", "--", ..]);

        Assert.True(removal >= 0);

        //One command for both files, not one command each.
        Assert.Equal(
            ["rm", "--", ":(literal)src/a.cs", ":(literal)src/b.cs"],
            git.Invocations[removal].Args);

        Assert.Single(git.Invocations, i => i.Args is ["rm", "--", ..]);
        Assert.True(removal < callsWhenFirstBinned);
    }

    [Fact]
    public async Task DecliningTheQuestionReachesNeitherTheBinNorTheIndex()
    {
        FakeGitRunner git = Populated();
        bool binned = false;

        Removal result = await Create(git).RunAsync(
            Repository,
            [Folder("src/Legacy"), File("src/a.cs")],
            _ => Task.FromResult(false),
            _ => { binned = true; return Task.FromResult(TrackingResult.Ok); },
            default);

        Assert.Equal(RemovalOutcome.Declined, result.Outcome);
        Assert.False(binned);
        Assert.True(git.NeverCalledWith("--cached"));

        //The file half is a write too, and declining must not have reached it either.
        Assert.DoesNotContain(git.Invocations, i => i.Args is ["rm", "--", ..]);
    }

    [Fact]
    public async Task ATargetGitHasNothingUnderIsRefusedBeforeTheGateRuns()
    {
        //A question about an operation that cannot happen is worse than the refusal it precedes --
        //and `git rm` on it would answer `fatal: pathspec … did not match any files`, which is
        //accurate about something nobody asked.
        var git = new FakeGitRunner().Returns(["ls-files"], stdout: string.Empty);
        bool asked = false;

        Removal result = await Create(git).RunAsync(
            Repository,
            [Folder("scratch")],
            _ => { asked = true; return Task.FromResult(true); },
            _ => Task.FromResult(TrackingResult.Ok),
            default);

        Assert.Equal(RemovalOutcome.NotTracked, result.Outcome);
        Assert.Equal("scratch", result.Path);
        Assert.False(asked);
        Assert.True(git.NeverCalledWith("--dry-run"));
        Assert.True(git.NeverCalledWith("rm"));
    }

    [Fact]
    public async Task ABinThatFailsLeavesTheIndexAlone()
    {
        FakeGitRunner git = Populated();

        Removal result = await Create(git).RunAsync(
            Repository,
            [Folder("src/Legacy")],
            _ => Task.FromResult(true),
            _ => Task.FromResult(TrackingResult.Failed("The folder is open in another program.")),
            default);

        Assert.Equal(RemovalOutcome.BinFailed, result.Outcome);
        Assert.Contains("another program", result.Error);

        //Nothing was deleted, so nothing may be recorded as deleted.
        Assert.True(git.NeverCalledWith("--cached"));
    }

    [Fact]
    public async Task AFolderThatFailsToBinStopsWithACountOfWhatWentBefore()
    {
        //In scope under "the sequences". Once a folder is in the bin the batch can no longer be
        //all-or-nothing, so what is left is to stop at the first failure and say how much went first
        //-- the same shape the commit window's own loops report.
        FakeGitRunner git = Populated();

        Removal result = await Create(git).RunAsync(
            Repository,
            [Folder("src/Legacy"), Folder("src/Old")],
            _ => Task.FromResult(true),
            target => Task.FromResult(
                target.Relative == "src/Old"
                    ? TrackingResult.Failed("The folder is open in another program.")
                    : TrackingResult.Ok),
            default);

        Assert.Equal(RemovalOutcome.BinFailed, result.Outcome);
        Assert.Equal("src/Old", result.Path);
        Assert.Equal(1, result.Done);

        //Only the first folder's deletion reached the index.
        Assert.Single(git.Invocations, i => i.Args.Contains("--cached"));
    }

    [Fact]
    public async Task TheQuestionCarriesTheCombinedTotals()
    {
        //The counts are the whole reason this asks at all: the number of files is the one part of the
        //blast radius the user cannot see before answering. Over a selection they have to be the
        //totals, because the question is asked once for all of it.
        FakeGitRunner git = Populated();
        RemovalPlan? seen = null;

        await Create(git).RunAsync(
            Repository,
            [Folder("src/Legacy"), File("src/a.cs")],
            plan => { seen = plan; return Task.FromResult(false); },
            _ => Task.FromResult(TrackingResult.Ok),
            default);

        Assert.NotNull(seen);
        Assert.Equal(1, seen.Files);
        Assert.Equal(1, seen.Folders);

        //Two tracked under each of the two targets, and the untracked count is the folders' alone --
        //a file has nothing "inside" it for the Recycle Bin to take with it.
        Assert.Equal(4, seen.TrackedFiles);
        Assert.Equal(1, seen.UntrackedFiles);
    }

    [Fact]
    public async Task AFailedRecordingSaysSoWhileTheFolderIsAlreadyInTheBin()
    {
        //The one outcome after which the working tree and the index disagree. It has its own value
        //so the verb can name the Recycle Bin, which is the way back.
        FakeGitRunner git = Populated().Returns(["--cached"], exitCode: 1, stderr: "fatal: index file corrupt");

        Removal result = await Create(git).RunAsync(
            Repository,
            [Folder("src/Legacy")],
            _ => Task.FromResult(true),
            _ => Task.FromResult(TrackingResult.Ok),
            default);

        Assert.Equal(RemovalOutcome.RecordFailed, result.Outcome);
        Assert.Equal("src/Legacy", result.Path);
        Assert.Contains("index file corrupt", result.Error);
    }
}
