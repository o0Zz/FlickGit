using FlickGit.Commits;
using FlickGit.Merges;
using FlickGit.Models;
using FlickGit.Status;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The merge of `status` with the two `numstat` calls, and the staging defaults that come
/// out of it.
///
/// These are the behavioural tests CLAUDE.md lists under "Testing": untracked files excluded
/// by default, secret-matching files excluded even when tracked, binary files showing no line
/// count, and the staged/working-tree counts kept apart.
/// </summary>
public class StatusServiceTests : IDisposable
{
    private readonly string _root;
    private readonly RepositoryInfo _repository;

    public StatusServiceTests()
    {
        //A real directory, because untracked line counts are read from disk. Created under
        //the temp path -- CLAUDE.md: "Never run integration tests against the developer's
        //actual repository."
        _root = Path.Combine(Path.GetTempPath(), $"flickgit-status-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        _repository = new RepositoryInfo(
            _root,
            Path.GetFileName(_root),
            HasSubmodules: false,
            IsBare: false,
            GitDirectory: Path.Combine(_root, ".git"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static string Stream(params string[] records) =>
        string.Concat(records.Select(r => r + '\0'));

    private Task<RepositoryStatus> Run(FakeGitRunner git) =>
        new StatusService(git, new UntrackedFileMeasurer(), new MergeStateService(), new PreparedMessageService())
            .GetStatusAsync(_repository, CancellationToken.None);

    [Fact]
    public async Task CountsFromBothNumstatCallsAreSummedForDisplayAndKeptApartForTheTooltip()
    {
        //A file staged with +8 -2 and then modified again with +3 -1. The row shows 11 and 3;
        //the tooltip still knows which half was which.
        var git = new FakeGitRunner()
            .Returns(["status"], Stream("1 MM N... 100644 100644 100644 aa bb src/Options.cs"))
            .Returns(["diff", "--numstat"], Stream("3\t1\tsrc/Options.cs"))
            .Returns(["diff", "--cached", "--numstat"], Stream("8\t2\tsrc/Options.cs"));

        GitFileChange file = Assert.Single((await Run(git)).Files);

        Assert.Equal(11, file.AddedLines);
        Assert.Equal(3, file.RemovedLines);
        Assert.Equal(8, file.StagedAddedLines);
        Assert.Equal(2, file.StagedRemovedLines);
    }

    [Fact]
    public async Task TrackedModificationsAreSelectedByDefault()
    {
        //The fast path: the user must be able to commit without reading the list.
        var git = new FakeGitRunner()
            .Returns(["status"], Stream("1 .M N... 100644 100644 100644 aa bb src/Options.cs"))
            .Returns(["diff", "--numstat"], Stream("3\t1\tsrc/Options.cs"))
            .Returns(["diff", "--cached", "--numstat"], string.Empty);

        Assert.True(Assert.Single((await Run(git)).Files).IsSelected);
    }

    [Fact]
    public async Task UntrackedFilesAreNeverSelectedByDefaultEvenWhenTheyAreTheOnlyChange()
    {
        //CLAUDE.md, "Testing": "Untracked files are excluded from the default staging set,
        //including when they are the only changes." This is the assertion that keeps bin/,
        //obj/ and a stray heap dump out of a hurried commit.
        File.WriteAllText(Path.Combine(_root, "dump.json"), "{\n}\n");

        var git = new FakeGitRunner()
            .Returns(["status"], Stream("? dump.json"))
            .Returns(["diff", "--numstat"], string.Empty)
            .Returns(["diff", "--cached", "--numstat"], string.Empty);

        RepositoryStatus status = await Run(git);
        GitFileChange file = Assert.Single(status.Files);

        Assert.True(file.IsUntracked);
        Assert.False(file.IsSelected);
        Assert.Equal(1, status.UntrackedCount);
        Assert.Equal(0, status.TrackedChangeCount);
    }

    [Fact]
    public async Task UntrackedLineCountsComeFromDiskBecauseNumstatDoesNotReportThem()
    {
        File.WriteAllText(Path.Combine(_root, "new.txt"), "one\ntwo\nthree\n");

        var git = new FakeGitRunner()
            .Returns(["status"], Stream("? new.txt"))
            .Returns(["diff", "--numstat"], string.Empty)
            .Returns(["diff", "--cached", "--numstat"], string.Empty);

        GitFileChange file = Assert.Single((await Run(git)).Files);

        Assert.Equal(3, file.AddedLines);
        Assert.Equal(0, file.RemovedLines);
    }

    [Fact]
    public async Task SecretMatchingFilesAreExcludedEvenWhenTrackedAndModified()
    {
        //CLAUDE.md, "Testing": "Secret-matching files are excluded even when tracked and
        //modified."
        var git = new FakeGitRunner()
            .Returns(["status"], Stream("1 .M N... 100644 100644 100644 aa bb .env"))
            .Returns(["diff", "--numstat"], Stream("2\t0\t.env"))
            .Returns(["diff", "--cached", "--numstat"], string.Empty);

        GitFileChange file = Assert.Single((await Run(git)).Files);

        Assert.True(file.LooksLikeSecret);
        Assert.False(file.IsSelected);
    }

    [Fact]
    public async Task ConflictedFilesAreNeverSelected()
    {
        //Committing a file with conflict markers in it is the worst thing this tool could do
        //silently.
        var git = new FakeGitRunner()
            .Returns(["status"], Stream("u UU N... 100644 100644 100644 100644 aa bb cc src/Conflict.cs"))
            .Returns(["diff", "--numstat"], string.Empty)
            .Returns(["diff", "--cached", "--numstat"], string.Empty);

        RepositoryStatus status = await Run(git);

        Assert.True(status.HasConflicts);
        Assert.False(Assert.Single(status.Files).IsSelected);
    }

    [Fact]
    public async Task BinaryFilesCarryNoLineCounts()
    {
        //CLAUDE.md, "Testing": "Binary files show `bin`, never a line count, and never open
        //a text diff."
        var git = new FakeGitRunner()
            .Returns(["status"], Stream("1 .M N... 100644 100644 100644 aa bb assets/logo.png"))
            .Returns(["diff", "--numstat"], Stream("-\t-\tassets/logo.png"))
            .Returns(["diff", "--cached", "--numstat"], string.Empty);

        GitFileChange file = Assert.Single((await Run(git)).Files);

        Assert.True(file.IsBinary);
        Assert.Null(file.AddedLines);
        Assert.Null(file.RemovedLines);
    }

    [Fact]
    public async Task RenameKeepsBothPaths()
    {
        var git = new FakeGitRunner()
            .Returns(["status"], Stream(
                "2 R. N... 100644 100644 100644 aa bb R100 src/New.cs",
                "src/Old.cs"))
            .Returns(["diff", "--numstat"], string.Empty)
            .Returns(["diff", "--cached", "--numstat"], Stream("5\t3\t", "src/Old.cs", "src/New.cs"));

        GitFileChange file = Assert.Single((await Run(git)).Files);

        Assert.Equal("src/New.cs", file.Path);
        Assert.Equal("src/Old.cs", file.OldPath);
    }

    [Fact]
    public async Task FilesAreSortedConflictedFirstAndUntrackedLast()
    {
        File.WriteAllText(Path.Combine(_root, "z.txt"), "x\n");

        var git = new FakeGitRunner()
            .Returns(["status"], Stream(
                "? z.txt",
                "1 .D N... 100644 100644 000000 aa bb b-deleted.cs",
                "1 .M N... 100644 100644 100644 aa bb c-modified.cs",
                "u UU N... 100644 100644 100644 100644 aa bb cc a-conflict.cs"))
            .Returns(["diff", "--numstat"], string.Empty)
            .Returns(["diff", "--cached", "--numstat"], string.Empty);

        IReadOnlyList<GitFileChange> files = (await Run(git)).Files;

        //The rows that need a decision at the top; the rows that are unticked by default at
        //the bottom.
        Assert.Equal("a-conflict.cs", files[0].Path);
        Assert.Equal("c-modified.cs", files[1].Path);
        Assert.Equal("b-deleted.cs", files[2].Path);
        Assert.Equal("z.txt", files[3].Path);
    }

    [Fact]
    public async Task AllThreeCallsAreReadOnly()
    {
        //THE flag, on all three. A background scan across ten repositories that took index
        //locks would fight the user's IDE in every one of them.
        var git = new FakeGitRunner()
            .Returns(["status"], string.Empty)
            .Returns(["diff"], string.Empty);

        await Run(git);

        Assert.Equal(3, git.Invocations.Count);
        Assert.All(git.Invocations, i => Assert.True(i.ReadOnly));
    }

    [Fact]
    public async Task AFailedNumstatStillYieldsAFileList()
    {
        //The status letters are the load-bearing half. Losing the whole window over a
        //missing "+42" would be the wrong trade.
        var git = new FakeGitRunner()
            .Returns(["status"], Stream("1 .M N... 100644 100644 100644 aa bb src/Options.cs"))
            .Returns(["diff"], exitCode: 128, stderr: "fatal: bad revision");

        GitFileChange file = Assert.Single((await Run(git)).Files);

        Assert.Equal("src/Options.cs", file.Path);
        Assert.Null(file.AddedLines);
    }

    [Fact]
    public async Task AFailedStatusThrowsWithGitsOwnWords()
    {
        //The other half of the same trade: without status there is no list to show, and the
        //user has to be told why in Git's words.
        var git = new FakeGitRunner()
            .Returns(["status"], exitCode: 128, stderr: "fatal: not a git repository")
            .Returns(["diff"], string.Empty);

        var exception = await Assert.ThrowsAsync<FlickGit.Git.GitOperationException>(() => Run(git));

        Assert.Contains("not a git repository", exception.Message);
        Assert.Contains(_root, exception.Message);
    }
}
