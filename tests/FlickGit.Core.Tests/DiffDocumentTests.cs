using FlickGit.Diff;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Building the pane documents, and — the part that matters — reconstructing the file from the
/// edited one.
///
/// The right-hand document contains blank filler lines that keep the two panes aligned and are not
/// part of the file. Every test below is about not writing those into the user's source, and about
/// not dropping a line that only looks like one.
/// </summary>
public class DiffDocumentTests
{
    private static DiffRow Unchanged(string text, int left, int right) =>
        new(DiffLineKind.Unchanged, new DiffSide(left, text, []), new DiffSide(right, text, []));

    private static DiffRow Inserted(string text, int right) =>
        new(DiffLineKind.Inserted, new DiffSide(null, string.Empty, []), new DiffSide(right, text, []));

    private static DiffRow Deleted(string text, int left) =>
        new(DiffLineKind.Deleted, new DiffSide(left, text, []), new DiffSide(null, string.Empty, []));

    [Fact]
    public void BothDocumentsGetOneLinePerRow()
    {
        //The invariant the whole viewer rests on: row N is line N in both panes, so synchronised
        //scrolling is an offset copy rather than a mapping that can drift.
        DiffRow[] rows =
        [
            Unchanged("one", 1, 1),
            Deleted("removed", 2),
            Inserted("added", 2),
            Unchanged("last", 3, 3),
        ];

        (string left, string right, IReadOnlyList<int> fillers) = DiffDocument.Build(rows);

        Assert.Equal(4, left.Split('\n').Length);
        Assert.Equal(4, right.Split('\n').Length);

        //The deleted row has no right-hand content, so line 2 of the right pane is padding.
        Assert.Equal([2], fillers);
    }

    [Fact]
    public void TheReconstructedFileHasNoFillerLinesInIt()
    {
        //THE test. A filler written to disk is a blank line inserted into the user's source.
        string[] document = ["one", "", "added", "last"];

        string file = DiffDocument.ToFileText(document, [2], endsWithNewline: true);

        Assert.Equal("one\nadded\nlast\n", file);
    }

    [Fact]
    public void ALineTheUserTypedIntoIsKeptEvenThoughItWasFiller()
    {
        //The filler was padding until the user put something in it. Now it is a line they added,
        //and dropping it would silently discard their work.
        string[] document = ["one", "the user typed this", "last"];

        string file = DiffDocument.ToFileText(document, [2], endsWithNewline: true);

        Assert.Equal("one\nthe user typed this\nlast\n", file);
    }

    [Fact]
    public void AGenuinelyEmptyLineTheUserAddedIsKept()
    {
        //Empty, but not filler. A user who adds a blank line between two paragraphs must get one.
        string[] document = ["one", "", "three"];

        string file = DiffDocument.ToFileText(document, [], endsWithNewline: true);

        Assert.Equal("one\n\nthree\n", file);
    }

    [Fact]
    public void OnlyLinesThatAreBothFillerAndEmptyAreDropped()
    {
        //Two empty lines, one of them padding. Exactly one survives.
        string[] document = ["a", "", "", "b"];

        string file = DiffDocument.ToFileText(document, [2], endsWithNewline: true);

        Assert.Equal("a\n\nb\n", file);
    }

    [Fact]
    public void TheTrailingNewlineFollowsTheFileRatherThanTheDocument()
    {
        //A document always ends with a line; a file may or may not be terminated. Adding one the
        //file did not have is a change to its last line.
        string[] document = ["one", "two"];

        Assert.Equal("one\ntwo\n", DiffDocument.ToFileText(document, [], endsWithNewline: true));
        Assert.Equal("one\ntwo", DiffDocument.ToFileText(document, [], endsWithNewline: false));
    }

    [Fact]
    public void AnAllFillerDocumentReconstructsToNothing()
    {
        //Every line is padding: the file is empty, and must not come back as a run of blank lines.
        string[] document = ["", "", ""];

        Assert.Equal(string.Empty, DiffDocument.ToFileText(document, [1, 2, 3], endsWithNewline: true));
    }

    [Fact]
    public void AnEmptyDocumentIsAnEmptyFile() =>
        Assert.Equal(string.Empty, DiffDocument.ToFileText([], [], endsWithNewline: true));

    [Fact]
    public void FillerNumbersOutsideTheDocumentAreIgnored()
    {
        //A stale entry must not throw. It is dropped, which is the same as not being there.
        string[] document = ["a", "b"];

        Assert.Equal("a\nb\n", DiffDocument.ToFileText(document, [7, 99], endsWithNewline: true));
    }

    [Fact]
    public void BuildAndReconstructRoundTripAnUneditedFile()
    {
        //Opening a file and saving it without touching it must produce the same text. If this fails,
        //the viewer corrupts every file it merely looks at.
        const string original = "one\ntwo\nthree\n";

        IReadOnlyList<DiffRow> rows = DiffService.Rediff("one\nTWO\nthree\n", original, wordLevel: false);

        (_, string right, IReadOnlyList<int> fillers) = DiffDocument.Build(rows);

        string reconstructed = DiffDocument.ToFileText(right.Split('\n'), fillers, endsWithNewline: true);

        Assert.Equal(original, reconstructed);
    }

    [Fact]
    public void RoundTripSurvivesADeletionOnlyDiff()
    {
        //The case that produces right-hand fillers: the base has lines the working copy does not.
        const string working = "one\n";

        IReadOnlyList<DiffRow> rows = DiffService.Rediff("one\ntwo\nthree\n", working, wordLevel: false);

        (_, string right, IReadOnlyList<int> fillers) = DiffDocument.Build(rows);

        Assert.NotEmpty(fillers);
        Assert.Equal(working, DiffDocument.ToFileText(right.Split('\n'), fillers, endsWithNewline: true));
    }

    [Fact]
    public void RoundTripSurvivesAFileOfOnlyInsertions()
    {
        //An untracked file: empty base, so every row is an insertion and none is filler.
        const string working = "a\nb\nc\n";

        IReadOnlyList<DiffRow> rows = DiffService.Rediff(string.Empty, working, wordLevel: false);

        (_, string right, IReadOnlyList<int> fillers) = DiffDocument.Build(rows);

        Assert.Equal(working, DiffDocument.ToFileText(right.Split('\n'), fillers, endsWithNewline: true));
    }

    [Fact]
    public void RoundTripPreservesBlankLinesThatAreRealContent()
    {
        //A file with its own blank lines, diffed against a base that also has them. None of those
        //blanks is padding, and all of them have to come back.
        const string working = "one\n\ntwo\n\n\nthree\n";

        IReadOnlyList<DiffRow> rows = DiffService.Rediff("one\n\ntwo\n\n\nCHANGED\n", working, wordLevel: false);

        (_, string right, IReadOnlyList<int> fillers) = DiffDocument.Build(rows);

        Assert.Equal(working, DiffDocument.ToFileText(right.Split('\n'), fillers, endsWithNewline: true));
    }
}
