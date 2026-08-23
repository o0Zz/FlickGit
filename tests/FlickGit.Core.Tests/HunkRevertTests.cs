using FlickGit.Diff;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The working tree — Hard Requirement 4's fourth bullet: "the one value that may ever be written to
/// a file".
///
/// <see cref="Hunks.RevertRows"/> is the only function in the product that <i>removes</i> the user's
/// uncommitted work, and what it returns becomes the whole contents of a file in their repository.
/// Every case below is a way that reconstruction can silently corrupt a file rather than fail: a
/// dropped line, a gained one, a trailing newline appearing or disappearing.
///
/// It works in the normalised <c>\n</c> text the viewer holds and says nothing about line endings —
/// <c>WorkingTreeWriter</c> restores those — so unlike <see cref="HunkPatchTests"/> there is no
/// terminator to assert here. That division is the thing being relied on, and it is why these are
/// two files rather than one.
/// </summary>
public class HunkRevertTests
{
    private static DiffRow Unchanged(int line, string text) =>
        new(DiffLineKind.Unchanged, new DiffSide(line, text, []), new DiffSide(line, text, []));

    private static DiffRow Inserted(int rightLine, string text) =>
        new(DiffLineKind.Inserted, new DiffSide(null, string.Empty, []), new DiffSide(rightLine, text, []));

    private static DiffRow Deleted(int leftLine, string text) =>
        new(DiffLineKind.Deleted, new DiffSide(leftLine, text, []), new DiffSide(null, string.Empty, []));

    private static DiffRow Modified(int leftLine, string before, int rightLine, string after) =>
        new(DiffLineKind.Modified, new DiffSide(leftLine, before, []), new DiffSide(rightLine, after, []));

    /// <summary>A changed line goes back to what the left side has, and its neighbours are untouched.</summary>
    [Fact]
    public void A_modified_line_reverts_to_the_left_side()
    {
        DiffRow[] rows = [Unchanged(1, "one"), Modified(2, "two", 2, "TWO"), Unchanged(3, "three")];

        Assert.Equal("one\ntwo\nthree", Hunks.RevertRows(rows, new HashSet<int> { 1 }));
    }

    /// <summary>
    /// Reverting an inserted line removes it, because the left side of that row is filler and filler
    /// contributes no line at all.
    /// </summary>
    [Fact]
    public void Reverting_an_insertion_drops_the_line()
    {
        DiffRow[] rows = [Unchanged(1, "a"), Inserted(2, "new"), Unchanged(2, "b")];

        Assert.Equal("a\nb", Hunks.RevertRows(rows, new HashSet<int> { 1 }));
    }

    /// <summary>
    /// And reverting a deletion brings the line back. The mirror of the case above, out of the same
    /// rule — which is the whole reason one function serves both directions.
    /// </summary>
    [Fact]
    public void Reverting_a_deletion_restores_the_line()
    {
        DiffRow[] rows = [Unchanged(1, "a"), Deleted(2, "gone"), Unchanged(3, "b")];

        Assert.Equal("a\ngone\nb", Hunks.RevertRows(rows, new HashSet<int> { 1 }));
    }

    /// <summary>
    /// An unselected change stays changed. This is the assertion that separates "revert these lines"
    /// from "discard the file": everything the user did not pick survives verbatim.
    /// </summary>
    [Fact]
    public void An_unselected_change_is_left_alone()
    {
        DiffRow[] rows =
        [
            Modified(1, "one", 1, "ONE"),
            Unchanged(2, "two"),
            Modified(3, "three", 3, "THREE"),
        ];

        Assert.Equal("one\ntwo\nTHREE", Hunks.RevertRows(rows, new HashSet<int> { 0 }));
    }

    /// <summary>
    /// A file that ends with a newline arrives as a final empty row, and reverting must neither
    /// consume that terminator nor add a second one.
    ///
    /// CLAUDE.md records this as the bug that made every patch reaching the bottom of a file fail:
    /// "A file ending with a newline produces one diff row past its last line — the empty string
    /// after the final terminator." Hand-built rows without that row let the unit tests pass while
    /// the feature was broken, so it is spelled out here.
    /// </summary>
    [Fact]
    public void A_trailing_newline_survives_a_revert()
    {
        DiffRow[] rows = [Modified(1, "one", 1, "ONE"), Unchanged(2, string.Empty)];

        Assert.Equal("one\n", Hunks.RevertRows(rows, new HashSet<int> { 0 }));
    }

    /// <summary>And a file without one does not gain one.</summary>
    [Fact]
    public void A_file_with_no_trailing_newline_does_not_gain_one()
    {
        DiffRow[] rows = [Unchanged(1, "one"), Modified(2, "two", 2, "TWO")];

        Assert.Equal("one\ntwo", Hunks.RevertRows(rows, new HashSet<int> { 1 }));
    }

    /// <summary>
    /// A selection covering only context reverts nothing and says so, rather than returning the file
    /// unchanged — which would mark the editor dirty for an edit that did not happen.
    /// </summary>
    [Fact]
    public void A_selection_with_no_change_in_it_returns_null()
    {
        DiffRow[] rows = [Unchanged(1, "one"), Modified(2, "two", 2, "TWO"), Unchanged(3, "three")];

        Assert.Null(Hunks.RevertRows(rows, new HashSet<int> { 0, 2 }));
    }

    /// <summary>Out-of-range indices cannot reach past the row list.</summary>
    [Fact]
    public void Row_indices_outside_the_list_are_ignored()
    {
        DiffRow[] rows = [Unchanged(1, "one"), Modified(2, "two", 2, "TWO")];

        Assert.Null(Hunks.RevertRows(rows, new HashSet<int> { -1, 99 }));
        Assert.Equal("one\ntwo", Hunks.RevertRows(rows, new HashSet<int> { -1, 1, 99 }));
    }

    /// <summary>
    /// Reverting every row reproduces the left side exactly, which is the strongest single statement
    /// of what this function means.
    /// </summary>
    [Fact]
    public void Reverting_everything_reproduces_the_left_side()
    {
        DiffRow[] rows =
        [
            Unchanged(1, "keep"),
            Modified(2, "before", 2, "after"),
            Inserted(3, "added"),
            Deleted(3, "removed"),
            Unchanged(4, "tail"),
        ];

        Assert.Equal(
            "keep\nbefore\nremoved\ntail",
            Hunks.RevertRows(rows, new HashSet<int> { 0, 1, 2, 3, 4 }));
    }
}
