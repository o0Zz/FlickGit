using FlickGit.Models;
using FlickGit.Status;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// `diff --name-status -z` parsing.
///
/// In scope under Hard Requirement 4's parser bullet, and for the same reason the numstat tests
/// are: the rename form consumes a different number of fields from every other record, so getting
/// it wrong does not lose one row — it turns the old path into the next row's status letter and
/// corrupts everything after it.
/// </summary>
public class NameStatusParserTests
{
    private static string Stream(params string[] fields) =>
        string.Concat(fields.Select(f => f + '\0'));

    [Fact]
    public void ReadsTheLetterAndThePathForAddedModifiedAndDeleted()
    {
        //CLAUDE.md's parser bullet names paths with spaces and non-ASCII characters explicitly, so
        //they are here rather than in a test of their own.
        var entries = NameStatusParser.Parse(Stream(
            "M", "src/GatewayClient.cs",
            "A", "src/Pg Bouncer/Pool.cs",
            "D", "src/Légacy/Poøl.cs"));

        Assert.Equal(GitChangeType.Modified, entries["src/GatewayClient.cs"].Status);
        Assert.Equal(GitChangeType.Added, entries["src/Pg Bouncer/Pool.cs"].Status);
        Assert.Equal(GitChangeType.Deleted, entries["src/Légacy/Poøl.cs"].Status);

        Assert.All(entries.Values, e => Assert.Null(e.OldPath));
    }

    [Fact]
    public void RenameConsumesTwoExtraFieldsAndTheScoreIsGluedToTheLetter()
    {
        //R100 is one field, not two, and the record that follows must still parse -- which is what
        //fails when the reader takes one path per record.
        var entries = NameStatusParser.Parse(Stream(
            "R100", "src/LegacyPool.cs", "src/PgBouncerPool.cs",
            "M", "src/Options.cs"));

        NameStatusEntry renamed = entries["src/PgBouncerPool.cs"];

        Assert.Equal(GitChangeType.Renamed, renamed.Status);
        Assert.Equal("src/LegacyPool.cs", renamed.OldPath);

        Assert.Equal(GitChangeType.Modified, entries["src/Options.cs"].Status);
        Assert.Equal(2, entries.Count);
    }
}
