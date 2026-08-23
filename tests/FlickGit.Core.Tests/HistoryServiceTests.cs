using FlickGit.History;
using FlickGit.Models;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The service behind the log window.
/// </summary>
public class HistoryServiceTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\dev\d360-portal", "d360-portal", HasSubmodules: false, IsBare: false);

    private static CommitRange Range() => new()
    {
        BaseSpec = "b2",
        TipSpec = "e5",
        Oldest = Commit("c3", "b2"),
        Newest = Commit("e5", "d4"),
        SelectedCount = 3,
        SpannedCount = 3,
    };

    private static LogCommit Commit(string sha, params string[] parents) => new()
    {
        Sha = sha,
        ShortSha = sha,
        Parents = parents,
        Author = "Ana",
        When = DateTimeOffset.UnixEpoch,
        Refs = string.Empty,
        Message = sha,
    };

    private static string Stream(params string[] fields) => string.Concat(fields.Select(f => f + '\0'));

    /// <summary>
    /// In scope under the parser bullet: both streams keying on the post-image path is the only
    /// reason the merge works at all, and a rename is the one case where they could key
    /// differently — which would produce two rows for one file, or one row with no counts.
    /// </summary>
    [Fact]
    public async Task RangeFileListMergesTheLetterAndTheCountsOnTheRenamedPath()
    {
        var git = new FakeGitRunner()
            .Returns(["diff", "--name-status"], Stream("R100", "src/LegacyPool.cs", "src/PgBouncerPool.cs"))
            .Returns(["diff", "--numstat"], Stream("156\t203\t", "src/LegacyPool.cs", "src/PgBouncerPool.cs"));

        IReadOnlyList<GitFileChange> files = await new HistoryService(git)
            .GetFilesAsync(Repository, Range(), CancellationToken.None);

        GitFileChange file = Assert.Single(files);

        Assert.Equal("src/PgBouncerPool.cs", file.Path);
        Assert.Equal("src/LegacyPool.cs", file.OldPath);
        Assert.Equal(GitChangeType.Renamed, file.DisplayStatus);
        Assert.Equal(156, file.AddedLines);
        Assert.Equal(203, file.RemovedLines);
    }

    /// <summary>
    /// In scope under the parser bullet: the parser is tested against a hand-written stream, so
    /// nothing else pins that Git is actually asked to produce that stream — and
    /// <c>log.decorate = full</c> or <c>color.decorate</c> change the bytes %D emits without
    /// changing a line of our code. Phase 6 records this exact failure mode: hand-built test rows
    /// passed while the feature was broken against real Git.
    /// </summary>
    [Fact]
    public async Task TheLogRequestPinsItsFormatAndDecorationAgainstTheUsersGitconfig()
    {
        var git = new FakeGitRunner().Returns(["log"], string.Empty);

        await new HistoryService(git).GetPageAsync(Repository, skip: 0, CancellationToken.None);

        string[] args = Assert.Single(git.Invocations).Args;

        Assert.Contains("--decorate=short", args);
        Assert.Contains("--no-color", args);
        Assert.Contains("--format=" + CommitLogParser.Format, args);
        Assert.Contains($"--max-count={HistoryService.PageSize + 1}", args);
    }

    /// <summary>
    /// In scope under the safety rules, two of which this covers at once: "every read carries
    /// <c>--no-optional-locks</c>", and the log window's own promise that it performs nothing. The
    /// mechanical form of the second is that no invocation originating here goes through
    /// <c>RunAsync</c> — which is what catches somebody later hanging a checkout off this surface.
    /// </summary>
    [Fact]
    public async Task TheHistorySurfaceOnlyEverReads()
    {
        var git = new FakeGitRunner()
            .Returns(["log"], string.Empty)
            .Returns(["diff"], string.Empty);

        var history = new HistoryService(git);

        await history.GetPageAsync(Repository, skip: 0, CancellationToken.None);
        await history.GetFilesAsync(Repository, Range(), CancellationToken.None);
        await history.SavePatchAsync(Repository, Range(), @"C:\dev\range.patch", CancellationToken.None);

        Assert.NotEmpty(git.Invocations);
        Assert.All(git.Invocations, i => Assert.True(i.ReadOnly));
    }
}
