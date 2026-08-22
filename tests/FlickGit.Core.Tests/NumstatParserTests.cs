using FlickGit.Status;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// `diff --numstat -z` parsing.
///
/// Two of these tests are the whole reason the parser is a NUL state machine rather than a
/// line loop: the rename form, and a path containing a literal <c>=&gt;</c>.
/// </summary>
public class NumstatParserTests
{
    private static string Stream(params string[] fields) =>
        string.Concat(fields.Select(f => f + '\0'));

    [Fact]
    public void ParsesCountsAndPath()
    {
        var entries = NumstatParser.Parse(Stream("42\t17\tsrc/GatewayClient.cs"));

        NumstatEntry entry = entries["src/GatewayClient.cs"];

        Assert.Equal(42, entry.Added);
        Assert.Equal(17, entry.Removed);
        Assert.False(entry.IsBinary);
        Assert.Null(entry.OldPath);
    }

    [Fact]
    public void BinaryFileReportsNullCountsRatherThanZero()
    {
        //Git prints "-" for both counts on a binary file. Reading that as 0 would show
        //"+0 -0" on a file that was replaced wholesale, which says the opposite of the
        //truth. CLAUDE.md: display "bin", never "+0 -0".
        var entries = NumstatParser.Parse(Stream("-\t-\tassets/logo.png"));

        NumstatEntry entry = entries["assets/logo.png"];

        Assert.Null(entry.Added);
        Assert.Null(entry.Removed);
        Assert.True(entry.IsBinary);
    }

    [Fact]
    public void RenameReadsTwoExtraFieldsInPreImageThenPostImageOrder()
    {
        //THE -z numstat trap. The third tab-field is *empty* and the two paths follow as
        //separate NUL fields. There is no "old => new" arrow anywhere in this format.
        string stdout = Stream(
            "10\t2\t",
            "src/LegacyPool.cs",
            "src/PgBouncerPool.cs");

        NumstatEntry entry = Assert.Single(NumstatParser.Parse(stdout)).Value;

        Assert.Equal("src/PgBouncerPool.cs", entry.Path);
        Assert.Equal("src/LegacyPool.cs", entry.OldPath);
        Assert.Equal(10, entry.Added);
        Assert.Equal(2, entry.Removed);
    }

    [Fact]
    public void RecordAfterARenameIsStillParsed()
    {
        //The regression that matters: if the rename's two extra fields are not consumed, the
        //cursor is left mid-entry and every record after it is garbage.
        string stdout = Stream(
            "10\t2\t",
            "src/LegacyPool.cs",
            "src/PgBouncerPool.cs",
            "8\t1\tsrc/Options.cs");

        var entries = NumstatParser.Parse(stdout);

        Assert.Equal(2, entries.Count);
        Assert.Equal(8, entries["src/Options.cs"].Added);
    }

    [Fact]
    public void PathContainingALiteralArrowSequenceIsNotSplit()
    {
        //CLAUDE.md, "Testing": "--numstat -z parsing, including a rename and a path
        //containing a literal '=>' sequence." Under the non-z format this path is
        //ambiguous with a rename; under -z it is not, and this test is what pins that.
        const string path = "src/a => b.cs";

        var entries = NumstatParser.Parse(Stream($"3\t1\t{path}"));

        NumstatEntry entry = Assert.Single(entries).Value;

        Assert.Equal(path, entry.Path);
        Assert.Null(entry.OldPath);
        Assert.Equal(3, entry.Added);
    }

    [Fact]
    public void PathContainingATabIsKeptWholeBecauseTheSplitIsBounded()
    {
        //A tab is legal in a path. The split is bounded at three parts, so the remainder --
        //tabs included -- is the path.
        const string path = "src/one\ttwo.cs";

        var entries = NumstatParser.Parse(Stream($"5\t0\t{path}"));

        Assert.Equal(path, Assert.Single(entries).Value.Path);
    }

    [Fact]
    public void PathsAreMatchedCaseSensitivelyBecauseGitPathsAre()
    {
        //A repository can hold README.md and readme.md at once. Merging them case-
        //insensitively would sum two files' counts into one row.
        var entries = NumstatParser.Parse(Stream("1\t0\tREADME.md", "2\t0\treadme.md"));

        Assert.Equal(2, entries.Count);
        Assert.Equal(1, entries["README.md"].Added);
        Assert.Equal(2, entries["readme.md"].Added);
    }

    [Fact]
    public void TruncatedRenameIsDroppedRatherThanCorruptingTheCursor()
    {
        //A rename whose second path never arrived. Dropping the entry is the only safe
        //outcome -- guessing would attribute the counts to the wrong file.
        var entries = NumstatParser.Parse(Stream("10\t2\t", "src/OnlyOne.cs"));

        Assert.Empty(entries);
    }

    [Fact]
    public void EmptyOutputYieldsNoEntries() =>
        Assert.Empty(NumstatParser.Parse(string.Empty));
}
