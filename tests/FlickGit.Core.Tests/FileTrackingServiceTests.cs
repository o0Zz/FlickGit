using FlickGit.Files;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The Explorer file menu's Add and Remove.
///
/// In scope under "the safety rules": <c>git rm</c> deletes a file from the working tree, so what
/// its argument list contains is worth pinning byte for byte. Two things it must never grow are
/// <c>-f</c>, which is what makes Git's own refusal to destroy uncommitted work the guard, and
/// <c>-r</c>, which is what keeps one click acting on one file. The third is the pathspec: an
/// ordinary Windows file name like <c>a[1].txt</c> is a glob after <c>--</c>, and a glob is how a
/// deletion reaches a file nobody clicked.
/// </summary>
public class FileTrackingServiceTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\dev\repo", "repo", HasSubmodules: false, IsBare: false);

    private static FileTrackingService Create(FakeGitRunner git) =>
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
    public async Task ABracketedFileNameIsNotAGlob()
    {
        //THE case the pathspec prefix exists for. `git rm -- a[1].txt` reads the brackets as a
        //character class and deletes `a1.txt` instead -- a file the user never clicked, gone, exit
        //code 0.
        var git = new FakeGitRunner().Returns(["rm"]);

        await Create(git).RemoveAsync(Repository, "dumps/a[1].txt", default);

        Assert.Equal(
            ["rm", "--", ":(literal)dumps/a[1].txt"],
            Assert.Single(git.Invocations).Args);
    }

    [Fact]
    public async Task BothWritesTakeTheIndexLock()
    {
        //--no-optional-locks is for reads. Both of these are supposed to write the index.
        var git = new FakeGitRunner().Returns(["add"]).Returns(["rm"]);

        FileTrackingService files = Create(git);

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
    public async Task TheTrackedCheckIsAReadThatAsksAboutTheOneFile()
    {
        var git = new FakeGitRunner().Returns(["ls-files"], stdout: "src/Thing.cs\0");

        Assert.True(await Create(git).IsTrackedAsync(Repository, "src/Thing.cs", default));

        FakeGitRunner.Invocation call = Assert.Single(git.Invocations);

        Assert.Equal(["ls-files", "-z", "--", ":(literal)src/Thing.cs"], call.Args);
        Assert.True(call.ReadOnly);
    }

    [Fact]
    public async Task AnUntrackedFileAnswersNoRatherThanFailing()
    {
        //`ls-files` succeeds and says nothing at all for a path the index does not have, which is
        //what lets the removal be refused with a sentence instead of with `fatal: pathspec …`.
        var git = new FakeGitRunner().Returns(["ls-files"], stdout: string.Empty);

        Assert.False(await Create(git).IsTrackedAsync(Repository, "scratch/dump.json", default));
        Assert.True(git.NeverCalledWith("rm"));
    }
}
