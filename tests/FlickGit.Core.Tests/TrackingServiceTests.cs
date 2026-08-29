using FlickGit.Files;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The Explorer menu's Add and Remove, on a file and on a folder.
///
/// In scope under "the safety rules": <c>git rm</c> is one flag away from deleting the user's files,
/// so what its argument list contains is worth pinning byte for byte. <b><c>--cached</c> is the whole
/// safety of this operation</b> — every promise the surfaces make about it (nothing deleted, no
/// confirmation, Add as the way back) is true only while that flag is on the vector, and the same flag
/// is what disarms the <c>-r</c> that lets one call serve a folder. <c>-f</c> is what would take away
/// Git's own refusal to strand an index entry nothing else holds.
///
/// The pathspec is the other half: an ordinary Windows name like <c>a[1].txt</c> is a glob after
/// <c>--</c>, and a glob is how a removal reaches something nobody clicked.
///
/// The counts are in scope under "parsers and the pure functions beside them": they split
/// <c>-z</c> output, and a path may contain any byte but NUL.
/// </summary>
public class TrackingServiceTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\dev\repo", "repo", HasSubmodules: false, IsBare: false, GitDirectory: @"C:\dev\repo\.git");

    private static TrackingService Create(FakeGitRunner git) =>
        new(git, new RepositoryService(git), NullLog.Instance);

    [Fact]
    public async Task AddStagesEveryLiteralPathInOneCommand()
    {
        //One process for the whole selection, each path its own non-globbing pathspec. Explorer hands
        //over everything that was selected, and staging the first and dropping the rest is what this
        //used to do.
        var git = new FakeGitRunner().Returns(["add"]);

        TrackingResult result = await Create(git)
            .AddAsync(Repository, ["src/Thing.cs", "docs/notes with a space.md"], default);

        Assert.True(result.Succeeded);

        Assert.Equal(
            ["add", "--", ":(literal)src/Thing.cs", ":(literal)docs/notes with a space.md"],
            Assert.Single(git.Invocations).Args);
    }

    [Fact]
    public async Task AddingAFolderIsTheSameVectorAndGrowsNoRecursionFlag()
    {
        //`git add` walks into a directory pathspec on its own, so the folder half of Add needs no
        //flag at all -- and must not grow one. -A and `.` are the two spellings CLAUDE.md forbids
        //outright, and both would reach past the folder that was clicked.
        var git = new FakeGitRunner().Returns(["add"]);

        await Create(git).AddAsync(Repository, ["src/Thing.cs", "src/Legacy"], default);

        string[] args = Assert.Single(git.Invocations).Args;

        Assert.Equal(["add", "--", ":(literal)src/Thing.cs", ":(literal)src/Legacy"], args);
        Assert.DoesNotContain("-r", args);
        Assert.DoesNotContain("-A", args);
        Assert.DoesNotContain(".", args);
    }

    [Fact]
    public async Task UntrackingCarriesCachedSoItCanNeverReachTheWorkingTree()
    {
        //The one rule this operation stands on. `git rm` without --cached deletes the file; with it,
        //the working tree is untouched whatever the pathspec turns out to match. Every other guarantee
        //the surfaces make -- no confirmation, no Recycle Bin, Add as the way back -- is only true
        //while this flag is on the vector.
        var git = new FakeGitRunner().Returns(["rm"]);

        TrackingResult result = await Create(git)
            .UntrackAsync(Repository, ["src/Thing.cs", "docs/notes with a space.md"], default);

        Assert.True(result.Succeeded);

        string[] args = Assert.Single(git.Invocations).Args;

        Assert.Equal(
            ["rm", "-r", "--cached", "--", ":(literal)src/Thing.cs", ":(literal)docs/notes with a space.md"],
            args);

        //-f would take away the one guard that is Git's own: without it, an index entry differing from
        //both HEAD and the file on disk is refused rather than dropped, which is the single state where
        //untracking could strand content nothing else holds.
        Assert.DoesNotContain("-f", args);
        Assert.DoesNotContain("--force", args);
        Assert.DoesNotContain(".", args);
        Assert.DoesNotContain("-A", args);

        //The separator sits immediately before the *first* pathspec, so a file named like an option
        //cannot become one -- whatever the count.
        Assert.Equal(
            Array.FindIndex(args, a => a.StartsWith(":(literal)", StringComparison.Ordinal)) - 1,
            Array.IndexOf(args, "--"));
    }

    [Fact]
    public async Task NoRemovalCarriesRecursionWithoutDisarmingIt()
    {
        //The rule -r exists under here, asserted over every vector this service can issue: it never
        //appears without --cached beside it. A file and a folder go through the same call, so the flag
        //is always present -- and the assertion is what says a second, bare vector was never added.
        var git = new FakeGitRunner().Returns(["rm"]);

        TrackingService tracking = Create(git);

        await tracking.UntrackAsync(Repository, ["src/Thing.cs"], default);
        await tracking.UntrackAsync(Repository, ["src/Legacy"], default);

        Assert.All(
            git.Invocations,
            invocation => Assert.True(
                !invocation.Args.Contains("-r") || invocation.Args.Contains("--cached")));

        Assert.True(git.NeverCalledWith("-f"));
        Assert.True(git.NeverCalledWith("--force"));
    }

    [Fact]
    public async Task ABracketedNameIsNotAGlob()
    {
        //THE case the pathspec prefix exists for. `git rm --cached -- a[1].txt` reads the brackets as a
        //character class and untracks `a1.txt` instead -- a file the user never clicked, out of the
        //index, exit code 0. No less true of a folder: `dumps/a[1]` and `dumps/a1` are two directories.
        var git = new FakeGitRunner().Returns(["rm"]);

        TrackingService tracking = Create(git);

        await tracking.UntrackAsync(Repository, ["dumps/a[1].txt"], default);
        await tracking.UntrackAsync(Repository, ["dumps/a[1]"], default);

        Assert.Equal(["rm", "-r", "--cached", "--", ":(literal)dumps/a[1].txt"], git.Invocations[0].Args);
        Assert.Equal(["rm", "-r", "--cached", "--", ":(literal)dumps/a[1]"], git.Invocations[1].Args);
    }

    [Fact]
    public async Task BothWritesTakeTheIndexLock()
    {
        //--no-optional-locks is for reads. Both of these are supposed to write the index.
        var git = new FakeGitRunner().Returns(["add"]).Returns(["rm"]);

        TrackingService files = Create(git);

        await files.AddAsync(Repository, ["src/Thing.cs"], default);
        await files.UntrackAsync(Repository, ["src/Thing.cs"], default);

        Assert.All(git.Invocations, invocation => Assert.False(invocation.ReadOnly));
    }

    [Fact]
    public async Task AFailureReportsGitsOwnWords()
    {
        var git = new FakeGitRunner().Returns(
            ["rm"],
            exitCode: 1,
            stderr: "error: the following file has local modifications:\n    src/Thing.cs");

        TrackingResult result = await Create(git)
            .UntrackAsync(Repository, ["src/Thing.cs"], default);

        Assert.False(result.Succeeded);
        Assert.Contains("local modifications", result.Error);
    }

    [Fact]
    public async Task AnEmptyListRunsNoCommandAtAll()
    {
        //In scope under "the safety rules": `add -A` never appears in an argument list, and neither
        //may anything that comes to mean the same thing. `git add --` with no pathspec after it is
        //exactly that shape -- the one a plural signature could produce that means something other
        //than what was asked -- so an empty list has to stop before the process starts.
        var git = new FakeGitRunner().Returns(["add"]).Returns(["rm"]);

        TrackingService tracking = Create(git);

        Assert.True((await tracking.AddAsync(Repository, [], default)).Succeeded);
        Assert.True((await tracking.UntrackAsync(Repository, [], default)).Succeeded);

        Assert.Empty(git.Invocations);
    }

    [Fact]
    public async Task TheTrackedCountIsAReadThatAsksAboutTheOnePath()
    {
        var git = new FakeGitRunner().Returns(["ls-files"], stdout: "src/Thing.cs\0");

        Assert.Equal(1, await Create(git).TrackedCountAsync(Repository, "src/Thing.cs", default));

        FakeGitRunner.Invocation call = Assert.Single(git.Invocations);

        Assert.Equal(["ls-files", "-z", "--", ":(literal)src/Thing.cs"], call.Args);
        Assert.True(call.ReadOnly);
    }

    [Fact]
    public async Task AnUntrackedPathCountsZeroRatherThanFailing()
    {
        //`ls-files` succeeds and says nothing at all for a path the index does not have, which is
        //what lets the removal be refused with a sentence instead of with `fatal: pathspec …`.
        var git = new FakeGitRunner().Returns(["ls-files"], stdout: string.Empty);

        Assert.Equal(0, await Create(git).TrackedCountAsync(Repository, "scratch/dump.json", default));
        Assert.True(git.NeverCalledWith("rm"));
    }

    [Fact]
    public async Task TheCountsSplitOnNulAndSurviveSpacesAndNonAscii()
    {
        //Three entries, and only the NUL separates them: one holds a space, one is non-ASCII, and
        //one carries the trailing newline some Git versions add after the last record. Splitting on
        //anything else -- or counting that terminator -- gets the number in the question wrong.
        var git = new FakeGitRunner().Returns(
            ["ls-files"],
            stdout: "src/My Folder/a.cs\0src/naïve/é.cs\0src/Third.cs\0\n");

        Assert.Equal(3, await Create(git).TrackedCountAsync(Repository, "src", default));
    }

}
