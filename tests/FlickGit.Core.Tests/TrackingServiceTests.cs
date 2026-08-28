using FlickGit.Files;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The Explorer menu's Add and Remove, on a file and on a folder.
///
/// In scope under "the safety rules": <c>git rm</c> deletes from the working tree, so what its
/// argument list contains is worth pinning byte for byte. <c>-f</c> is what would take away Git's own
/// refusal to destroy uncommitted work. <c>-r</c> is what turns one click into a tree, and it is
/// allowed here on exactly two vectors — <c>--dry-run</c>, which changes nothing, and
/// <c>--cached</c>, which cannot reach the working tree — so the tests are what say there is no
/// third. The pathspec is the last of the three: an ordinary Windows name like <c>a[1].txt</c> is a
/// glob after <c>--</c>, and a glob is how a deletion reaches something nobody clicked.
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
    public async Task AddStagesExactlyOneLiteralPath()
    {
        var git = new FakeGitRunner().Returns(["add"]);

        TrackingResult result = await Create(git).AddAsync(Repository, "src/Thing.cs", default);

        Assert.True(result.Succeeded);

        Assert.Equal(
            ["add", "--", ":(literal)src/Thing.cs"],
            Assert.Single(git.Invocations).Args);
    }

    [Fact]
    public async Task AddingAFolderIsTheSameVectorAndGrowsNoRecursionFlag()
    {
        //`git add` walks into a directory pathspec on its own, so the folder half of Add needs no
        //flag at all -- and must not grow one. -A and `.` are the two spellings CLAUDE.md forbids
        //outright, and both would reach past the folder that was clicked.
        var git = new FakeGitRunner().Returns(["add"]);

        await Create(git).AddAsync(Repository, "src/Legacy", default);

        string[] args = Assert.Single(git.Invocations).Args;

        Assert.Equal(["add", "--", ":(literal)src/Legacy"], args);
        Assert.DoesNotContain("-r", args);
        Assert.DoesNotContain("-A", args);
        Assert.DoesNotContain(".", args);
    }

    [Fact]
    public async Task RemoveDeletesExactlyOneLiteralPathAndIsNeverForcedOrRecursive()
    {
        var git = new FakeGitRunner().Returns(["rm"]);

        TrackingResult result = await Create(git).RemoveAsync(Repository, "src/Thing.cs", default);

        Assert.True(result.Succeeded);

        string[] args = Assert.Single(git.Invocations).Args;

        Assert.Equal(["rm", "--", ":(literal)src/Thing.cs"], args);

        //-f would take away the one guard that is Git's own: without it, a file whose content
        //differs from both HEAD and the index is refused rather than destroyed. -r would turn a
        //click on one file into a directory tree.
        Assert.DoesNotContain("-f", args);
        Assert.DoesNotContain("--force", args);
        Assert.DoesNotContain("-r", args);
        Assert.DoesNotContain(".", args);
        Assert.DoesNotContain("-A", args);

        //After the separator, so a file named like an option cannot become one.
        Assert.Equal(args.Length - 2, Array.IndexOf(args, "--"));
    }

    [Fact]
    public async Task TheFolderGateIsADryRunAndItIsARead()
    {
        //The only -r in the product that is allowed to see the working tree, and --dry-run is what
        //makes that safe: it performs Git's own check and changes nothing, which is what lets the
        //Recycle Bin run afterwards without anything left to refuse it.
        var git = new FakeGitRunner().Returns(["rm"]);

        TrackingResult result = await Create(git).CanRemoveFolderAsync(Repository, "src/Legacy", default);

        Assert.True(result.Succeeded);

        FakeGitRunner.Invocation call = Assert.Single(git.Invocations);

        Assert.Equal(["rm", "-r", "--dry-run", "--", ":(literal)src/Legacy"], call.Args);
        Assert.True(call.ReadOnly);
        Assert.DoesNotContain("-f", call.Args);
        Assert.DoesNotContain("--force", call.Args);
    }

    [Fact]
    public async Task ARefusedGateReportsGitsOwnWords()
    {
        var git = new FakeGitRunner().Returns(
            ["rm"],
            exitCode: 1,
            stderr: "error: the following file has local modifications:\n    src/Legacy/Old.cs");

        TrackingResult result = await Create(git).CanRemoveFolderAsync(Repository, "src/Legacy", default);

        Assert.False(result.Succeeded);
        Assert.Contains("src/Legacy/Old.cs", result.Error);
    }

    [Fact]
    public async Task TheFolderRecordingIsCachedSoItCannotDeleteAnything()
    {
        //By the time this runs the folder is in the Recycle Bin, so the index is all that is left to
        //update. --cached is what makes that structural: a bare `rm -r` here would be a second thing
        //able to destroy the user's files, reached after the one question has been answered.
        var git = new FakeGitRunner().Returns(["rm"]);

        TrackingResult result = await Create(git).RemoveFolderAsync(Repository, "src/Legacy", default);

        Assert.True(result.Succeeded);

        string[] args = Assert.Single(git.Invocations).Args;

        Assert.Equal(["rm", "-r", "--cached", "--", ":(literal)src/Legacy"], args);
        Assert.DoesNotContain("-f", args);
        Assert.DoesNotContain("--force", args);
    }

    [Fact]
    public async Task NoRemovalCarriesRecursionWithoutDisarmingIt()
    {
        //The rule the two vectors above exist under, asserted over all of them at once: -r never
        //appears in this service without --dry-run or --cached beside it.
        var git = new FakeGitRunner().Returns(["rm"]);

        TrackingService tracking = Create(git);

        await tracking.RemoveAsync(Repository, "src/Thing.cs", default);
        await tracking.CanRemoveFolderAsync(Repository, "src/Legacy", default);
        await tracking.RemoveFolderAsync(Repository, "src/Legacy", default);

        Assert.All(
            git.Invocations,
            invocation => Assert.True(
                !invocation.Args.Contains("-r")
                || invocation.Args.Contains("--dry-run")
                || invocation.Args.Contains("--cached")));

        Assert.True(git.NeverCalledWith("-f"));
        Assert.True(git.NeverCalledWith("--force"));
    }

    [Fact]
    public async Task ABracketedNameIsNotAGlob()
    {
        //THE case the pathspec prefix exists for. `git rm -- a[1].txt` reads the brackets as a
        //character class and deletes `a1.txt` instead -- a file the user never clicked, gone, exit
        //code 0. No less true of a folder: `dumps/a[1]` and `dumps/a1` are two directories.
        var git = new FakeGitRunner().Returns(["rm"]);

        TrackingService tracking = Create(git);

        await tracking.RemoveAsync(Repository, "dumps/a[1].txt", default);
        await tracking.RemoveFolderAsync(Repository, "dumps/a[1]", default);

        Assert.Equal(["rm", "--", ":(literal)dumps/a[1].txt"], git.Invocations[0].Args);
        Assert.Equal(["rm", "-r", "--cached", "--", ":(literal)dumps/a[1]"], git.Invocations[1].Args);
    }

    [Fact]
    public async Task BothWritesTakeTheIndexLock()
    {
        //--no-optional-locks is for reads. Both of these are supposed to write the index.
        var git = new FakeGitRunner().Returns(["add"]).Returns(["rm"]);

        TrackingService files = Create(git);

        await files.AddAsync(Repository, "src/Thing.cs", default);
        await files.RemoveAsync(Repository, "src/Thing.cs", default);

        Assert.All(git.Invocations, invocation => Assert.False(invocation.ReadOnly));
    }

    [Fact]
    public async Task AFailureReportsGitsOwnWords()
    {
        var git = new FakeGitRunner().Returns(
            ["rm"],
            exitCode: 1,
            stderr: "error: the following file has local modifications:\n    src/Thing.cs");

        TrackingResult result = await Create(git).RemoveAsync(Repository, "src/Thing.cs", default);

        Assert.False(result.Succeeded);
        Assert.Contains("local modifications", result.Error);
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

    [Fact]
    public async Task TheUntrackedAndChangedCountsAreDisjointReadsOverTheSameFolder()
    {
        //The folder add asks two questions rather than one, so that neither answer needs
        //de-duplicating: what Git has never seen, and what it has and what has changed.
        var git = new FakeGitRunner()
            .Returns(["ls-files"], stdout: "src/Legacy/new.cs\0")
            .Returns(["diff"], stdout: "src/Legacy/a.cs\0src/Legacy/b.cs\0");

        TrackingService tracking = Create(git);

        Assert.Equal(1, await tracking.UntrackedCountAsync(Repository, "src/Legacy", default));
        Assert.Equal(2, await tracking.ChangedCountAsync(Repository, "src/Legacy", default));

        Assert.Equal(
            ["ls-files", "-z", "--others", "--exclude-standard", "--", ":(literal)src/Legacy"],
            git.Invocations[0].Args);

        Assert.Equal(
            ["diff", "--name-only", "-z", "--no-color", "--no-ext-diff", "--no-textconv", "--", ":(literal)src/Legacy"],
            git.Invocations[1].Args);

        Assert.All(git.Invocations, invocation => Assert.True(invocation.ReadOnly));
    }
}
