using FlickGit.Models;
using FlickGit.Status;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// `status --porcelain=v2 -z` parsing.
///
/// The fixtures are written as real NUL-separated streams rather than through a helper that
/// joins lines, because the format's one genuine trap is *how many fields a record
/// consumes* — and a helper that hides the NULs would hide exactly the bug these tests
/// exist to catch.
/// </summary>
public class PorcelainV2ParserTests
{
    /// <summary>Builds a NUL-terminated stream, the way Git writes one.</summary>
    private static string Stream(params string[] records) =>
        string.Concat(records.Select(r => r + '\0'));

    [Fact]
    public void ReadsBranchUpstreamAndAheadBehind()
    {
        string stdout = Stream(
            "# branch.oid 8f9ab42c1d3e4f5a6b7c8d9e0f1a2b3c4d5e6f70",
            "# branch.head feature/storage-gw",
            "# branch.upstream origin/feature/storage-gw",
            "# branch.ab +2 -3");

        PorcelainStatus status = PorcelainV2Parser.Parse(stdout);

        Assert.Equal("feature/storage-gw", status.Branch);
        Assert.Equal("origin/feature/storage-gw", status.Upstream);
        Assert.Equal(2, status.Ahead);
        Assert.Equal(3, status.Behind);
        Assert.False(status.IsDetachedHead);
        Assert.False(status.IsUnborn);
    }

    [Fact]
    public void DetachedHeadIsReportedRatherThanTreatedAsABranchNamed_detached()
    {
        PorcelainStatus status = PorcelainV2Parser.Parse(Stream("# branch.head (detached)"));

        Assert.True(status.IsDetachedHead);
        Assert.Null(status.Branch);
    }

    [Fact]
    public void UnbornHeadIsReportedRatherThanTreatedAsACommitNamed_initial()
    {
        //A fresh repository with no commit. Every diff's left side is empty, and a caller
        //that took "(initial)" for a commit hash would ask `git show (initial):file`.
        PorcelainStatus status = PorcelainV2Parser.Parse(Stream("# branch.oid (initial)"));

        Assert.True(status.IsUnborn);
        Assert.Null(status.HeadCommit);
    }

    [Fact]
    public void MissingBranchAbHeaderLeavesAheadBehindAtZero()
    {
        //Git omits branch.ab entirely when the branch has no upstream. Ahead/Behind must
        //read as 0, not as "unknown", or the header would show ↑↓ on a branch that has no
        //remote to compare against.
        PorcelainStatus status = PorcelainV2Parser.Parse(
            Stream("# branch.head main"));

        Assert.Equal(0, status.Ahead);
        Assert.Equal(0, status.Behind);
        Assert.Null(status.Upstream);
    }

    [Fact]
    public void ParsesOrdinaryChangeAndSplitsTheXyColumns()
    {
        //"MM" -- staged as modified, then modified again in the working tree. The two sides
        //have to stay separate or the tooltip's split is a fiction.
        string stdout = Stream("1 MM N... 100644 100644 100644 aaaa bbbb src/GatewayClient.cs");

        GitFileChange file = Assert.Single(PorcelainV2Parser.Parse(stdout).Files);

        Assert.Equal("src/GatewayClient.cs", file.Path);
        Assert.Equal(GitChangeType.Modified, file.IndexStatus);
        Assert.Equal(GitChangeType.Modified, file.WorkTreeStatus);
        Assert.True(file.IsStaged);
    }

    [Fact]
    public void UnstagedChangeIsNotReportedAsStaged()
    {
        //"." in the index column means nothing is staged. Reading that as staged would make
        //the commit reconciliation unstage files that were never in the index.
        string stdout = Stream("1 .M N... 100644 100644 100644 aaaa bbbb src/Options.cs");

        GitFileChange file = Assert.Single(PorcelainV2Parser.Parse(stdout).Files);

        Assert.False(file.IsStaged);
        Assert.Equal(GitChangeType.None, file.IndexStatus);
        Assert.Equal(GitChangeType.Modified, file.WorkTreeStatus);
    }

    [Fact]
    public void RenameConsumesTheFollowingFieldAsTheOldPath()
    {
        //THE porcelain v2 -z trap. The original path is a separate NUL-terminated field, not
        //a tab-appended suffix. A parser that reads one record per field treats
        //"src/LegacyPool.cs" as the next entry -- and every record after it shifts by one.
        string stdout = Stream(
            "2 R. N... 100644 100644 100644 aaaa bbbb R100 src/PgBouncerPool.cs",
            "src/LegacyPool.cs",
            "1 .M N... 100644 100644 100644 cccc dddd src/Options.cs");

        IReadOnlyList<GitFileChange> files = PorcelainV2Parser.Parse(stdout).Files;

        Assert.Equal(2, files.Count);

        Assert.Equal("src/PgBouncerPool.cs", files[0].Path);
        Assert.Equal("src/LegacyPool.cs", files[0].OldPath);
        Assert.Equal(GitChangeType.Renamed, files[0].IndexStatus);

        //The proof that the stream did not shift: the record after the rename is intact.
        Assert.Equal("src/Options.cs", files[1].Path);
        Assert.Null(files[1].OldPath);
    }

    [Fact]
    public void UnmergedEntryIsConflictedOnBothSidesAndNotStaged()
    {
        string stdout = Stream("u UU N... 100644 100644 100644 100644 aaaa bbbb cccc src/Conflict.cs");

        GitFileChange file = Assert.Single(PorcelainV2Parser.Parse(stdout).Files);

        Assert.True(file.IsConflicted);
        Assert.Equal(GitChangeType.Conflicted, file.IndexStatus);
        Assert.Equal(GitChangeType.Conflicted, file.WorkTreeStatus);

        //Staging a file with conflict markers in it is the one thing this window must never
        //do by accident.
        Assert.False(file.IsStaged);
    }

    [Fact]
    public void UntrackedFileIsUnselectedByDefault()
    {
        //CLAUDE.md, "Staging Defaults": the single most valuable safety default in the
        //product. This is the assertion that keeps .env out of a hurried commit.
        string stdout = Stream("? scratch/dump.json");

        GitFileChange file = Assert.Single(PorcelainV2Parser.Parse(stdout).Files);

        Assert.True(file.IsUntracked);
        Assert.False(file.IsSelected);
        Assert.False(file.IsStaged);
        Assert.Equal(GitChangeType.Untracked, file.WorkTreeStatus);
    }

    [Theory]
    [InlineData("src/file with spaces.cs")]
    [InlineData("src/Ünïcödé/Ω/файл.cs")]
    [InlineData("src/weird => name.cs")]
    [InlineData("src/one\ttwo.cs")]
    public void PathsAreTakenWholeRatherThanSplitOnAnyDelimiter(string path)
    {
        //CLAUDE.md, "Parsing traps": "Paths may contain any byte except NUL. Never split on
        //spaces." A literal "=>" is in here because that sequence is the rename separator in
        //the *non*-z numstat format, and a parser that ever looks for it breaks on this file.
        string stdout = Stream($"1 .M N... 100644 100644 100644 aaaa bbbb {path}");

        GitFileChange file = Assert.Single(PorcelainV2Parser.Parse(stdout).Files);

        Assert.Equal(path, file.Path);
    }

    [Fact]
    public void UnknownRecordKindIsSkippedRatherThanDiscardingTheList()
    {
        //A future Git that adds a record type must not cost the user their file list.
        string stdout = Stream(
            "z something entirely new",
            "1 .M N... 100644 100644 100644 aaaa bbbb src/Options.cs");

        GitFileChange file = Assert.Single(PorcelainV2Parser.Parse(stdout).Files);

        Assert.Equal("src/Options.cs", file.Path);
    }

    [Fact]
    public void EmptyOutputIsACleanRepositoryRatherThanAnError()
    {
        PorcelainStatus status = PorcelainV2Parser.Parse(string.Empty);

        Assert.Empty(status.Files);
        Assert.Null(status.Branch);
    }
}
