using FlickGit.Diff;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Which line the viewer puts opposite which.
///
/// In scope under "the working tree": a row is what Revert lines and Stage hunk act on, so a row
/// that pairs a deletion with an insertion it has nothing to do with is a revert that rewrites the
/// wrong line — and it is what the user sees as red and green landing on different rows.
///
/// The alignment DiffPlex's own <c>SideBySideDiffBuilder</c> produces is positional: within a
/// change block it pairs the first deletion with the first insertion and so on, which is the wrong
/// answer whenever the two counts differ. <see cref="DiffService.Rediff"/> pairs by similarity
/// instead, and these are the cases that distinguish the two.
/// </summary>
public class DiffAlignmentTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    /// <summary>The row a line is on, on the side it is on. −1 when it is not in the diff at all.</summary>
    private static int RowOfLeft(IReadOnlyList<DiffRow> rows, string text)
    {
        for (int row = 0; row < rows.Count; row++)
        {
            if (rows[row].Left.LineNumber is not null && rows[row].Left.Text == text)
                return row;
        }

        return -1;
    }

    private static int RowOfRight(IReadOnlyList<DiffRow> rows, string text)
    {
        for (int row = 0; row < rows.Count; row++)
        {
            if (rows[row].Right.LineNumber is not null && rows[row].Right.Text == text)
                return row;
        }

        return -1;
    }

    [Fact]
    public void AReplacedLineIsOppositeTheLineThatReplacedIt()
    {
        //The reported bug, at its smallest: one line replaced, and insertions above it inside the
        //same change block. Positional pairing puts OLD-A opposite `extra1` -- the first insertion
        //of the block -- and pushes NEW-A two rows down, so the red and the green are not on the
        //same row.
        IReadOnlyList<DiffRow> rows = DiffService.Rediff(
            Lines("keep", "OLD-A", "tail"),
            Lines("keep", "extra1", "extra2", "NEW-A", "tail"),
            wordLevel: false);

        int red = RowOfLeft(rows, "OLD-A");
        int green = RowOfRight(rows, "NEW-A");

        Assert.Equal(red, green);
        Assert.Equal(DiffLineKind.Modified, rows[red].Kind);
    }

    [Fact]
    public void TheInsertionsAroundAPairKeepTheirOrder()
    {
        //Everything the block contains still appears, once, in the order the new file has it. The
        //pairing chooses what sits opposite what, never what exists.
        IReadOnlyList<DiffRow> rows = DiffService.Rediff(
            Lines("keep", "OLD-A", "tail"),
            Lines("keep", "extra1", "extra2", "NEW-A", "tail"),
            wordLevel: false);

        Assert.Equal(
            ["keep", "extra1", "extra2", "NEW-A", "tail", string.Empty],
            rows.Where(row => row.Right.LineNumber is not null).Select(row => row.Right.Text));

        Assert.Equal(
            ["keep", "OLD-A", "tail", string.Empty],
            rows.Where(row => row.Left.LineNumber is not null).Select(row => row.Left.Text));
    }

    [Fact]
    public void WordSpansAreComputedAgainstTheLineActuallyPaired()
    {
        //The second half of the same bug. A pair built from unrelated lines highlights the
        //difference between two unrelated lines, so the whole row lights up as changed words
        //instead of the one token that moved.
        IReadOnlyList<DiffRow> rows = DiffService.Rediff(
            Lines("keep", "var pool = new Pool();", "tail"),
            Lines("keep", "using System;", "var pool = pooled(x);", "tail"),
            wordLevel: true);

        DiffRow pair = rows[RowOfLeft(rows, "var pool = new Pool();")];

        Assert.Equal("var pool = pooled(x);", pair.Right.Text);

        //`var pool = ` is common to both, so the highlight starts after it rather than at column 0.
        Assert.NotEmpty(pair.Right.ChangedSpans);
        Assert.True(pair.Right.ChangedSpans[0].Start > 0);
    }

    [Fact]
    public void ADeletedLineWithNoCounterpartPairsWithNothing()
    {
        //Deleting one of two lines and inserting nothing: the survivor has to stay opposite itself,
        //and the deletion has to be a row of its own rather than a pair with the next line down.
        IReadOnlyList<DiffRow> rows = DiffService.Rediff(
            Lines("keep", "gone", "tail"),
            Lines("keep", "tail"),
            wordLevel: false);

        Assert.Equal(DiffLineKind.Deleted, rows[RowOfLeft(rows, "gone")].Kind);
        Assert.True(rows[RowOfLeft(rows, "gone")].Right.IsFiller);
        Assert.Equal(RowOfLeft(rows, "tail"), RowOfRight(rows, "tail"));
    }

    [Fact]
    public void AOneForOneReplacementStillPairsWhenTheLinesShareNothing()
    {
        //The tie-break the pair bonus exists for. Two lines with no similarity at all still belong
        //on one row -- a red row above a green row is a worse reading of a replacement than a red
        //row beside a green one, and it is what an unbonused best-alignment would produce.
        IReadOnlyList<DiffRow> rows = DiffService.Rediff(
            Lines("keep", "aaaa"),
            Lines("keep", "zzzz"),
            wordLevel: false);

        Assert.Equal(RowOfLeft(rows, "aaaa"), RowOfRight(rows, "zzzz"));
    }

    [Fact]
    public void BothSidesAlwaysHaveOneEntryPerRow()
    {
        //The invariant every other part of the viewer rests on: the two documents have the same
        //number of lines, which is what makes synchronised scrolling an offset copy. A block whose
        //two counts differ is where a builder that emits rows per side rather than per pair would
        //break it.
        IReadOnlyList<DiffRow> rows = DiffService.Rediff(
            Lines("a", "b", "c", "d"),
            Lines("a", "B1", "B2", "B3", "d"),
            wordLevel: false);

        Assert.All(rows, row => Assert.NotNull(row.Left));
        Assert.All(rows, row => Assert.NotNull(row.Right));

        //Every line of each file appears exactly once, and nothing else does.
        Assert.Equal(5, rows.Count(row => row.Left.LineNumber is not null));
        Assert.Equal(6, rows.Count(row => row.Right.LineNumber is not null));
    }

    [Fact]
    public void LineNumbersCountTheFileRatherThanTheRow()
    {
        //The gutter shows each side's number in its own file. Filler rows are what make the two
        //disagree, and a row's numbers have to survive them.
        IReadOnlyList<DiffRow> rows = DiffService.Rediff(
            Lines("a", "b"),
            Lines("a", "x", "y", "b"),
            wordLevel: false);

        Assert.Equal(2, rows[RowOfLeft(rows, "b")].Left.LineNumber);
        Assert.Equal(4, rows[RowOfLeft(rows, "b")].Right.LineNumber);
    }

    [Fact]
    public void AnUneditedFileIsAllUnchanged()
    {
        //No blocks at all, which is the path a file with one edited line spends most of its rows on.
        IReadOnlyList<DiffRow> rows = DiffService.Rediff(Lines("a", "b", "c"), Lines("a", "b", "c"), wordLevel: true);

        Assert.All(rows, row => Assert.Equal(DiffLineKind.Unchanged, row.Kind));
        Assert.Equal(4, rows.Count);
    }
}
