using FlickGit.Commits;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The argument list <see cref="CommitService"/> stages with.
///
/// In scope under Hard Requirement 4 as <b>the safety rules</b>: "<c>add -A</c> never appears in an
/// argument list", and "untracked and secret-matching files are not staged by default".
///
/// This is the one code path that stages the commit, and it was the only service in Core whose
/// arguments nothing asserted -- <see cref="CommitFlowTests"/> pins the <i>order</i> of the calls
/// and never their content. A <c>--force</c> lived here behind a plausible comment for exactly that
/// reason, so the assertion is on the whole list rather than on the absence of one flag.
/// </summary>
public class CommitStagingTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\repos\alpha", "alpha", HasSubmodules: false, IsBare: false, GitDirectory: @"C:\repos\alpha\.git");

    private static CommitService Create(FakeGitRunner git) =>
        new(git, new RepositoryService(git), NullLog.Instance);

    [Fact]
    public async Task StagingPassesTheResolvedPathsAndNothingThatCouldWidenThem()
    {
        var git = new FakeGitRunner().Returns(["add"]);

        await Create(git).StageAsync(
            Repository,
            ["src/Kept.cs", "docs/notes with a space.md"],
            CancellationToken.None);

        string[] args = Assert.Single(git.Invocations).Args;

        //Every path carries `:(literal)`. `--` alone still leaves the argument a pathspec, so a
        //ticked `report[final].xlsx` would stage `reportf.xlsx` instead -- a commit that is not the
        //one the user reviewed.
        Assert.Equal(
            ["add", "--", ":(literal)src/Kept.cs", ":(literal)docs/notes with a space.md"],
            args);

        //-A and . stage whatever appeared in the working tree since the status refresh, which is
        //not what the user ticked. CLAUDE.md forbids both outright, anywhere in the product.
        Assert.DoesNotContain("-A", args);
        Assert.DoesNotContain("--all", args);
        Assert.DoesNotContain(".", args);

        //--force stages a path a .gitignore rule covers. No ignored file can even reach this method
        //-- StatusService passes no --ignored -- so the flag has no case to serve here and would only
        //remove the backstop that keeps .env and bin/ out of a hurried commit.
        Assert.DoesNotContain("--force", args);
        Assert.DoesNotContain("-f", args);

        //Before the paths, so a file named like an option cannot become one.
        Assert.Equal(1, Array.IndexOf(args, "--"));
    }

    [Fact]
    public async Task UnstagingTouchesTheIndexOnlyAndCarriesNoPathspecThatCanWiden()
    {
        var git = new FakeGitRunner().Returns(["restore"]);

        await Create(git).UnstageAsync(
            Repository,
            ["src/Unticked.cs"],
            isUnborn: false,
            CancellationToken.None);

        string[] args = Assert.Single(git.Invocations).Args;

        //`restore --staged`, never `reset`: it is unambiguous about leaving the working tree alone.
        Assert.Equal(["restore", "--staged", "--", ":(literal)src/Unticked.cs"], args);
        Assert.DoesNotContain("--worktree", args);
        Assert.DoesNotContain("reset", args);

        //No --source when HEAD exists. Restoring the index from the empty tree here records a
        //*deletion* of the path instead -- the row comes back `1 D.` with the file on disk
        //untracked, and the next commit deletes a file the user only unticked. The unborn test
        //below is the one case that may name it.
        Assert.DoesNotContain(args, a => a.StartsWith("--source", StringComparison.Ordinal));
    }

    /// <summary>
    /// In scope under Hard Requirement 4 as one of <b>the sequences</b>: this is step 1 of
    /// <see cref="CommitFlow"/>, and in a clone of an empty remote it failed outright.
    ///
    /// <c>git restore --staged</c> takes its source from HEAD, so with no commit yet every unstage
    /// answered <c>fatal: could not resolve HEAD</c> and took the whole commit down with it -- which
    /// is every commit in such a repository where the user unticked a single row. The empty tree is
    /// what HEAD would name if it existed.
    /// </summary>
    [Fact]
    public async Task UnstagingInARepositoryWithNoCommitsNamesTheEmptyTreeInsteadOfHead()
    {
        var git = new FakeGitRunner().Returns(["restore"]);

        await Create(git).UnstageAsync(
            Repository,
            ["src/New.cs"],
            isUnborn: true,
            CancellationToken.None);

        string[] args = Assert.Single(git.Invocations).Args;

        Assert.Equal(
            [
                "restore",
                "--source=4b825dc642cb6eb9a060e54bf8d69288fbee4904",
                "--staged",
                "--",
                ":(literal)src/New.cs",
            ],
            args);

        //Still the index alone. The file on disk is the user's only copy of a first-ever addition,
        //and it is precisely what they keep.
        Assert.DoesNotContain("--worktree", args);
    }

    /// <summary>
    /// An empty selection runs no command at all. `git add --` with no paths is not a no-op to Git;
    /// it is an error, and it would surface as a failed commit for a user who had simply unticked
    /// everything.
    /// </summary>
    [Fact]
    public async Task AnEmptySelectionRunsNothing()
    {
        var git = new FakeGitRunner();

        await Create(git).StageAsync(Repository, [], CancellationToken.None);
        await Create(git).UnstageAsync(Repository, [], isUnborn: false, CancellationToken.None);

        Assert.Empty(git.Invocations);
    }

    /// <summary>
    /// The message file is written into the repository's <i>real</i> Git directory.
    ///
    /// In scope as one of the sequences: in a submodule and in a linked worktree <c>.git</c> is a
    /// <b>file</b>, so composing <c>&lt;root&gt;\.git\...</c> throws DirectoryNotFoundException --
    /// after StageAsync has already mutated the index, and with no commit possible from FlickGit in
    /// either kind of repository. RepositoryInfo carries the answer, read from the same rev-parse
    /// that resolved the root.
    /// </summary>
    [Fact]
    public async Task TheCommitMessageFileGoesWhereTheGitDirectoryActuallyIs()
    {
        string gitDirectory = Path.Combine(Path.GetTempPath(), $"flickgit-gitdir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(gitDirectory);

        try
        {
            var repository = new RepositoryInfo(
                @"C:\repos\alpha\sub", "sub", HasSubmodules: false, IsBare: false, GitDirectory: gitDirectory);

            //The commit itself is what reads the file, so the fake captures the path it was given.
            var git = new FakeGitRunner()
                .Returns(["commit"])
                .Returns(["rev-parse", "--short"], "abc1234\n");

            await Create(git).CommitAsync(repository, "a message", CancellationToken.None);

            string? messageFile = git.ArgumentAfter("-F");

            Assert.NotNull(messageFile);
            Assert.Equal(gitDirectory, Path.GetDirectoryName(messageFile));

            //Deleted afterwards, including on the path that succeeded.
            Assert.False(File.Exists(messageFile));
        }
        finally
        {
            Directory.Delete(gitDirectory, recursive: true);
        }
    }
}
