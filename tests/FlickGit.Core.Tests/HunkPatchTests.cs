using System.Text;
using FlickGit.Diff;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The working tree — Hard Requirement 4's fourth bullet, and the one place where thoroughness is the
/// point rather than the cost.
///
/// A generated patch is the only thing in the product that describes a change to the index in bytes
/// rather than in paths, and CLAUDE.md is specific about the way it goes wrong: "The patch must be
/// generated with the file's original line endings or it will not apply." A patch built from the
/// normalised text the viewer holds is rejected on every line of every CRLF repository, so the
/// terminators are asserted rather than assumed.
/// </summary>
public class HunkPatchTests
{
    private static FileText Text(string content, LineEndingStyle endings, bool endsWithNewline = true) =>
        new()
        {
            Text = content,
            Encoding = new UTF8Encoding(false),
            HasByteOrderMark = false,
            LineEndings = endings,
            EndsWithNewline = endsWithNewline,
        };

    private static DiffRow Unchanged(int line, string text) =>
        new(DiffLineKind.Unchanged, new DiffSide(line, text, []), new DiffSide(line, text, []));

    private static DiffRow Inserted(int rightLine, string text) =>
        new(DiffLineKind.Inserted, new DiffSide(null, string.Empty, []), new DiffSide(rightLine, text, []));

    private static DiffRow Deleted(int leftLine, string text) =>
        new(DiffLineKind.Deleted, new DiffSide(leftLine, text, []), new DiffSide(null, string.Empty, []));

    private static DiffRow Modified(int leftLine, string before, int rightLine, string after) =>
        new(DiffLineKind.Modified, new DiffSide(leftLine, before, []), new DiffSide(rightLine, after, []));

    /// <summary>
    /// A CRLF file's patch carries the carriage return as part of each line's content.
    ///
    /// This is the assertion the whole feature rests on. <c>git apply</c> compares a context line to
    /// the index byte for byte; in a CRLF file the line's content is <c>text\r</c>, so a patch line
    /// reading <c> text</c> matches nothing and the patch is refused in full.
    /// </summary>
    [Fact]
    public void A_crlf_file_keeps_its_carriage_returns()
    {
        DiffRow[] rows = [Unchanged(1, "one"), Modified(2, "two", 2, "TWO"), Unchanged(3, "three")];
        FileText file = Text("one\ntwo\nthree\n", LineEndingStyle.CrLf);

        string patch = Hunks.ToPatch("a.txt", rows, new HashSet<int> { 1 }, file, file)!;

        Assert.Contains(" one\r\n", patch);
        Assert.Contains("-two\r\n", patch);
        Assert.Contains("+TWO\r\n", patch);
        Assert.Contains(" three\r\n", patch);
    }

    /// <summary>An LF file gets no carriage returns invented for it.</summary>
    [Fact]
    public void An_lf_file_stays_lf()
    {
        DiffRow[] rows = [Unchanged(1, "one"), Modified(2, "two", 2, "TWO"), Unchanged(3, "three")];
        FileText file = Text("one\ntwo\nthree\n", LineEndingStyle.Lf);

        string patch = Hunks.ToPatch("a.txt", rows, new HashSet<int> { 1 }, file, file)!;

        Assert.DoesNotContain('\r', patch);
        Assert.Contains("-two\n", patch);
    }

    /// <summary>
    /// A mixed-ending file keeps each line's own terminator.
    ///
    /// Rewriting one kind as the other is the whole-file diff <see cref="FileText"/> exists to
    /// prevent, and in a patch it is worse than cosmetic: the rewritten lines stop matching the index.
    /// </summary>
    [Fact]
    public void A_mixed_file_keeps_each_line_as_it_was()
    {
        DiffRow[] rows = [Unchanged(1, "one"), Modified(2, "two", 2, "TWO"), Unchanged(3, "three")];

        FileText file = Text("one\ntwo\nthree\n", LineEndingStyle.Mixed) with
        {
            PerLineEndings = ["\r\n", "\n", "\r\n"],
            DominantNewLine = "\r\n",
        };

        string patch = Hunks.ToPatch("a.txt", rows, new HashSet<int> { 1 }, file, file)!;

        Assert.Contains(" one\r\n", patch);
        Assert.Contains("-two\n", patch);
        Assert.Contains("+TWO\n", patch);
        Assert.Contains(" three\r\n", patch);
    }

    /// <summary>
    /// A file with no trailing newline says so, or applying the patch would add one.
    /// </summary>
    [Fact]
    public void An_unterminated_last_line_is_marked()
    {
        DiffRow[] rows = [Unchanged(1, "one"), Modified(2, "two", 2, "TWO")];
        FileText file = Text("one\ntwo", LineEndingStyle.Lf, endsWithNewline: false);

        string patch = Hunks.ToPatch("a.txt", rows, new HashSet<int> { 1 }, file, file)!;

        Assert.Contains("\\ No newline at end of file", patch);
    }

    /// <summary>
    /// An unstaged deletion becomes context; an unstaged insertion disappears.
    ///
    /// The asymmetry is the heart of selected-line staging and the easiest thing to get backwards. A
    /// line not being removed is still in the index, so it is context on both sides. A line not being
    /// added is on neither side, so it is written nowhere. Reversed, the patch applies cleanly and
    /// stages the opposite of what was picked.
    /// </summary>
    [Fact]
    public void Unstaged_rows_are_demoted_rather_than_dropped()
    {
        DiffRow[] rows =
        [
            Unchanged(1, "keep"),
            Deleted(2, "goes"),
            Inserted(2, "comes"),
            Unchanged(3, "keep2"),
        ];

        FileText file = Text("keep\ngoes\nkeep2\n", LineEndingStyle.Lf);

        //Only the deletion is staged.
        string deletionOnly = Hunks.ToPatch("a.txt", rows, new HashSet<int> { 1 }, file, file)!;

        Assert.Contains("-goes\n", deletionOnly);
        Assert.DoesNotContain("comes", deletionOnly);

        //Only the insertion is staged: the deletion is not happening, so that line is context.
        string insertionOnly = Hunks.ToPatch("a.txt", rows, new HashSet<int> { 2 }, file, file)!;

        Assert.Contains(" goes\n", insertionOnly);
        Assert.Contains("+comes\n", insertionOnly);
        Assert.DoesNotContain("-goes", insertionOnly);
    }

    /// <summary>
    /// The hunk header counts what the patch actually contains, not what the diff contained.
    ///
    /// With the insertion dropped, the new side is one line shorter than the working tree — and a
    /// header that disagreed with the body is a patch <c>git apply</c> rejects as corrupt.
    /// </summary>
    [Fact]
    public void The_header_counts_the_lines_the_patch_emits()
    {
        DiffRow[] rows = [Unchanged(1, "a"), Inserted(2, "new"), Unchanged(2, "b")];
        FileText file = Text("a\nb\n", LineEndingStyle.Lf);

        string patch = Hunks.ToPatch("a.txt", rows, new HashSet<int> { 1 }, file, file)!;

        //Two old lines, three new ones.
        Assert.Contains("@@ -1,2 +1,3 @@", patch);
    }

    /// <summary>Staging nothing produces no patch, rather than one that applies emptily.</summary>
    [Fact]
    public void Staging_nothing_produces_no_patch()
    {
        DiffRow[] rows = [Unchanged(1, "a"), Inserted(2, "new")];
        FileText file = Text("a\n", LineEndingStyle.Lf);

        Assert.Null(Hunks.ToPatch("a.txt", rows, new HashSet<int>(), file, file));

        //A row that is not a change is not stageable either.
        Assert.Null(Hunks.ToPatch("a.txt", rows, new HashSet<int> { 0 }, file, file));
    }

    /// <summary>
    /// The path is written the way Git writes it, whatever Windows handed over.
    /// </summary>
    [Fact]
    public void The_path_uses_forward_slashes_and_git_prefixes()
    {
        DiffRow[] rows = [Modified(1, "a", 1, "b")];
        FileText file = Text("a\n", LineEndingStyle.Lf);

        string patch = Hunks.ToPatch(@"src\deep\a.txt", rows, new HashSet<int> { 0 }, file, file)!;

        Assert.StartsWith("diff --git a/src/deep/a.txt b/src/deep/a.txt\n", patch);
        Assert.Contains("--- a/src/deep/a.txt\n", patch);
        Assert.Contains("+++ b/src/deep/a.txt\n", patch);
    }

    /// <summary>
    /// Two changes far apart are two hunks; two close together are one.
    ///
    /// They have to merge, because their three lines of context would otherwise overlap and a patch
    /// cannot describe the same line twice.
    /// </summary>
    [Fact]
    public void Nearby_changes_merge_into_one_hunk()
    {
        var far = new List<DiffRow>();
        far.Add(Modified(1, "x", 1, "X"));

        for (int i = 2; i <= 20; i++)
            far.Add(Unchanged(i, $"line{i}"));

        far.Add(Modified(21, "y", 21, "Y"));

        Assert.Equal(2, Hunks.Find(far).Count);

        DiffRow[] near = [Modified(1, "x", 1, "X"), Unchanged(2, "a"), Modified(3, "y", 3, "Y")];

        Assert.Single(Hunks.Find(near));
    }

    /// <summary>
    /// A hunk carries the context around the change, so the patch can locate itself.
    /// </summary>
    [Fact]
    public void A_hunk_includes_its_context()
    {
        var rows = new List<DiffRow>();

        for (int i = 1; i <= 10; i++)
            rows.Add(Unchanged(i, $"line{i}"));

        rows[5] = Modified(6, "line6", 6, "SIX");

        DiffHunk hunk = Assert.Single(Hunks.Find(rows));

        //Three either side of row 5.
        Assert.Equal(2, hunk.FirstRow);
        Assert.Equal(8, hunk.LastRow);
        Assert.True(hunk.Covers(5));
        Assert.False(hunk.Covers(9));
    }

    /// <summary>
    /// A row past the file's last line is not a line, and must not become a context line.
    ///
    /// The differ splits on newlines, so a file that ends with one yields a final empty element and
    /// therefore a row numbered one past the end. Emitting it made the hunk header claim one line more
    /// than the file has, and <c>git apply</c> refused every patch whose context reached the bottom of
    /// a file. The hand-built rows in the tests above never had that row, which is exactly why they all
    /// passed while the feature was broken against real Git.
    /// </summary>
    [Fact]
    public void The_phantom_row_after_a_trailing_newline_is_not_a_line()
    {
        //Three real lines, then the empty element the final terminator leaves behind.
        DiffRow[] rows =
        [
            Unchanged(1, "one"),
            Modified(2, "two", 2, "TWO"),
            Unchanged(3, "three"),
            Unchanged(4, string.Empty),
        ];

        FileText file = Text("one\ntwo\nthree\n", LineEndingStyle.Lf);
        Assert.Equal(3, file.LineCount);

        string patch = Hunks.ToPatch("a.txt", rows, new HashSet<int> { 1 }, file, file)!;

        //Three lines each side, not four.
        Assert.Contains("@@ -1,3 +1,3 @@", patch);

        //And nothing after the last real line.
        Assert.EndsWith(" three\n", patch);
    }
}
