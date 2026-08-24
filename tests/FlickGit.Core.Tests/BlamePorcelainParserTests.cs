using FlickGit.Blame;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// `git blame --porcelain` parsing.
///
/// In scope under Hard Requirement 4's parser bullet. It is the only line-oriented format in the
/// product, and its metadata-once-per-commit rule is the place where a wrong assumption silently
/// blanks the author on most of the file rather than failing.
/// </summary>
public class BlamePorcelainParserTests
{
    private const string Sha = "a91030d413df21c94931a3eebc2c748f7c4bcd2b";
    private const string Other = "6b04582b0d39d6d95109a1a6905780c4c217673a";

    [Fact]
    public void MetadataFromACommitsFirstAppearanceIsReusedByItsLaterLines()
    {
        //Git emits the block once and then the bare header, so a parser that expects it every time
        //keeps the author on line 1 and loses it on lines 2 and 4.
        string stream = string.Join('\n',
            $"{Sha} 1 1 2",
            "author o0Zz",
            "author-time 1787430202",
            "author-tz +0200",
            "summary Remove pause/resume shell (Useless)",
            "previous " + Other + " README.md",
            "filename README.md",
            "\tfirst line",
            $"{Sha} 2 2",
            "\tsecond line",
            $"{Other} 9 3 1",
            "author Someone Else",
            "author-time 1787382126",
            "author-tz +0200",
            "summary Initial commit",
            "boundary",
            "filename README.md",
            "\tthird line",
            $"{Sha} 4 4",
            "\tfourth line");

        IReadOnlyList<BlameLine> lines = BlamePorcelainParser.Parse(stream);

        Assert.Equal(4, lines.Count);
        Assert.Equal([1, 2, 3, 4], lines.Select(l => l.Number));
        Assert.Equal(["first line", "second line", "third line", "fourth line"], lines.Select(l => l.Text));

        //The bare-header lines carry the same commit, including the one after an intervening commit
        //re-set the cursor.
        Assert.All([lines[0], lines[1], lines[3]], l =>
        {
            Assert.Equal("o0Zz", l.Commit.Author);
            Assert.Equal("Remove pause/resume shell (Useless)", l.Commit.Summary);
        });

        Assert.Equal("Someone Else", lines[2].Commit.Author);
    }

    [Fact]
    public void PreviousAndBoundaryAreCaptured()
    {
        //The walk-back mechanism. A line that loses its `previous` is a button that cannot be
        //pressed, and a commit wrongly read as a boundary ends the walk early.
        string stream = string.Join('\n',
            $"{Sha} 1 1 1",
            "author o0Zz",
            "author-time 1787430202",
            "author-tz +0200",
            "summary a commit",
            "previous " + Other + " docs/old name.md",
            "filename docs/new name.md",
            "\tline",
            $"{Other} 1 2 1",
            "author o0Zz",
            "author-time 1787382126",
            "author-tz +0200",
            "summary the first one",
            "boundary",
            "filename docs/old name.md",
            "\tanother");

        IReadOnlyList<BlameLine> lines = BlamePorcelainParser.Parse(stream);

        //The path is taken whole: only the sha is split off, because a path may contain spaces.
        Assert.Equal(Other, lines[0].Commit.PreviousSha);
        Assert.Equal("docs/old name.md", lines[0].Commit.PreviousPath);
        Assert.Equal("docs/new name.md", lines[0].Commit.Filename);
        Assert.True(lines[0].Commit.HasPrevious);
        Assert.False(lines[0].Commit.IsBoundary);

        Assert.True(lines[1].Commit.IsBoundary);
        Assert.False(lines[1].Commit.HasPrevious);
    }

    [Fact]
    public void FortyZerosReadsAsUncommittedRatherThanAsACommit()
    {
        //Blaming the working tree is the ordinary case -- it is what a right-click on a file does --
        //so a line the user has not committed is a normal state, not a failure.
        string stream = string.Join('\n',
            "0000000000000000000000000000000000000000 87 87 1",
            "author Not Committed Yet",
            "author-time 1787517569",
            "author-tz +0200",
            "summary Version of README.md from README.md",
            "previous " + Other + " README.md",
            "filename README.md",
            "\tan unsaved line");

        BlameLine line = Assert.Single(BlamePorcelainParser.Parse(stream));

        Assert.True(line.Commit.IsUncommitted);

        //Git still names where the file came from, so the walk back works from an uncommitted line
        //and lands on the committed version -- which is exactly "what was here before my edit".
        Assert.True(line.Commit.HasPrevious);
        Assert.Equal(Other, line.Commit.PreviousSha);
    }

    [Fact]
    public void ContentIsTakenFromTheTabAndTheOptionalGroupSizeIsNotRequired()
    {
        //The content line is found by its leading tab, never by exhausting known metadata keys: a
        //summary is arbitrary user text, and one shaped like a header field would otherwise shift
        //the parse. Here the message contains both a fake key and a tab.
        string stream = string.Join('\n',
            $"{Sha} 1 1",                       // no fourth field
            "author o0Zz",
            "author-time 1787430202",
            "author-tz +0200",
            "summary filename fake.md and a\ttab",
            "filename real.md",
            "\t\tindented with a tab",
            $"{Sha} 2 2",
            "\tplain\r");

        IReadOnlyList<BlameLine> lines = BlamePorcelainParser.Parse(stream);

        Assert.Equal(2, lines.Count);
        Assert.Equal("real.md", lines[0].Commit.Filename);
        Assert.Equal("filename fake.md and a\ttab", lines[0].Commit.Summary);

        //Only the one delimiting tab is consumed; the line's own indentation survives.
        Assert.Equal("\tindented with a tab", lines[0].Text);

        //A CRLF file's content keeps its carriage return in the stream, and it is not part of the line.
        Assert.Equal("plain", lines[1].Text);
    }

    [Fact]
    public void TheAuthorTimezoneIsKeptRatherThanConvertedToLocalTime()
    {
        //A commit made elsewhere reads as the hour its author saw, which is the hour they would name
        //if you asked them about it.
        string stream = string.Join('\n',
            $"{Sha} 1 1 1",
            "author o0Zz",
            "author-time 1787430202",
            "author-tz -0500",
            "summary a commit",
            "filename a.md",
            "\tline");

        BlameLine line = Assert.Single(BlamePorcelainParser.Parse(stream));

        Assert.Equal(TimeSpan.FromHours(-5), line.Commit.When.Offset);
        Assert.Equal(1787430202, line.Commit.When.ToUnixTimeSeconds());
    }
}
