using FlickGit.History;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The `git log --format=` stream.
///
/// In scope under Hard Requirement 4's parser bullet: this is the third place where a wrong byte
/// becomes a wrong list, and the field grammar is positional, so a field off by one puts the author
/// in the date with no error anywhere.
/// </summary>
public class CommitLogParserTests
{
    private const char Unit = '\x1f';

    /// <summary>
    /// One record as Git emits it: the fields, our NUL, then the newline `tformat` appends.
    /// </summary>
    private static string Record(
        string sha,
        string shortSha,
        string parents,
        string author,
        string date,
        string refs,
        string message) =>
        string.Join(Unit, sha, shortSha, parents, author, date, refs, message) + "\0\n";

    [Fact]
    public void ParsesEveryFieldOfOneRecord()
    {
        IReadOnlyList<LogCommit> commits = CommitLogParser.Parse(Record(
            "5f3a9c21b7e04d6f8a1c2b3d4e5f60718293a4b5",
            "5f3a9c2",
            "9d8c7b6a5f4e3d2c1b0a99887766554433221100",
            "Thomas Quemerais",
            "2026-08-21T14:03:07+02:00",
            "HEAD -> feature/storage-gw, origin/feature/storage-gw",
            "feat: add PgBouncer pooling\n"));

        LogCommit commit = Assert.Single(commits);

        Assert.Equal("5f3a9c21b7e04d6f8a1c2b3d4e5f60718293a4b5", commit.Sha);
        Assert.Equal("5f3a9c2", commit.ShortSha);
        Assert.Equal("9d8c7b6a5f4e3d2c1b0a99887766554433221100", Assert.Single(commit.Parents));
        Assert.Equal("Thomas Quemerais", commit.Author);
        Assert.Equal(new DateTimeOffset(2026, 8, 21, 14, 3, 7, TimeSpan.FromHours(2)), commit.When);
        Assert.Equal("HEAD -> feature/storage-gw, origin/feature/storage-gw", commit.Refs);
        Assert.Equal("feat: add PgBouncer pooling", commit.Subject);
        Assert.Equal(string.Empty, commit.Body);
        Assert.False(commit.IsMerge);
        Assert.False(commit.IsRoot);
    }

    [Fact]
    public void AMessageContainingTheSeparatorAndNewlinesDoesNotShiftTheNextRecord()
    {
        //This one test is the justification for all three format decisions at once -- %B last, the
        //bounded split, and the NUL record terminator. It is the only thing standing between an
        //arbitrary commit message and a corrupted list.
        string awkward = $"fix: handle {Unit} in the payload\n\nSee also: a => b\nCo-authored-by: nobody\n";

        IReadOnlyList<LogCommit> commits = CommitLogParser.Parse(
            Record("aaa", "aaa1111", "bbb", "Ana", "2026-08-21T14:03:07+02:00", string.Empty, awkward) +
            Record("ccc", "ccc2222", "ddd", "Bo", "2026-08-20T09:15:00+02:00", "main", "chore: bump\n"));

        Assert.Equal(2, commits.Count);

        Assert.Equal($"fix: handle {Unit} in the payload", commits[0].Subject);
        Assert.Equal("See also: a => b\nCo-authored-by: nobody", commits[0].Body);

        //The second record's sha would begin with a newline without the TrimStart, and would be
        //shifted by a field if the split were unbounded.
        Assert.Equal("ccc", commits[1].Sha);
        Assert.Equal("Bo", commits[1].Author);
        Assert.Equal("chore: bump", commits[1].Subject);
    }

    [Fact]
    public void RootCommitReportsNoParents()
    {
        //%P is empty for the root. Splitting it without RemoveEmptyEntries reports one parent whose
        //sha is the empty string, which becomes a base spec of "" -- and turns the repository's
        //first commit into a Git error instead of a diff against the empty tree.
        IReadOnlyList<LogCommit> commits = CommitLogParser.Parse(Record(
            "111", "1111111", string.Empty, "Ana", "2026-01-01T00:00:00+00:00", "tag: v0.1", "init\n"));

        LogCommit commit = Assert.Single(commits);

        Assert.Empty(commit.Parents);
        Assert.True(commit.IsRoot);
    }

    [Fact]
    public void MergeCommitCarriesEveryParent()
    {
        IReadOnlyList<LogCommit> commits = CommitLogParser.Parse(Record(
            "abc", "abc1234", "first second", "Ana", "2026-01-01T00:00:00+00:00", string.Empty,
            "Merge branch 'topic'\n"));

        LogCommit commit = Assert.Single(commits);

        Assert.Equal(["first", "second"], commit.Parents);
        Assert.True(commit.IsMerge);
    }
}
