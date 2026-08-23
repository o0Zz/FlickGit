using System.Text;

namespace FlickGit.Diff;

/// <summary>
/// One run of changed rows, with its context, as a unified-diff hunk.
/// </summary>
/// <param name="FirstRow">Index into the diff's row list, inclusive. Context included.</param>
/// <param name="LastRow">Inclusive.</param>
/// <param name="LeftStart">1-based first line on the left side, or 0 when the hunk adds to an empty file.</param>
/// <param name="LeftCount">Lines the left side contributes.</param>
/// <param name="RightStart">1-based first line on the right side.</param>
/// <param name="RightCount">Lines the right side contributes.</param>
public sealed record DiffHunk(
    int FirstRow,
    int LastRow,
    int LeftStart,
    int LeftCount,
    int RightStart,
    int RightCount)
{
    /// <summary>Whether <paramref name="row"/> is inside this hunk, context included.</summary>
    public bool Covers(int row) => row >= FirstRow && row <= LastRow;
}

/// <summary>
/// Turns the in-memory diff into hunks, and hunks into a patch <c>git apply</c> will accept.
///
/// <b>Why this exists at all.</b> The viewer diffs two buffers rather than parsing <c>git diff</c>
/// output — that is what lets the right pane be edited — so there is no patch lying around to stage
/// from. CLAUDE.md, Phase 6: "staging a hunk means generating a unified patch from the in-memory diff
/// and applying it with <c>git apply --cached -</c>. The patch must be generated with the file's
/// original line endings or it will not apply."
///
/// That last sentence is the whole difficulty. <c>git apply</c> matches context lines against the
/// index byte for byte, and in a CRLF file the content of a line <i>includes</i> its carriage return.
/// A patch built from the normalised text this viewer holds would be rejected on every line of every
/// CRLF repository — so each emitted line is re-terminated from the <see cref="FileText"/> it came
/// from, per line for a file whose endings are mixed.
///
/// Pure functions of their arguments, hence static: Hard Requirement 3's stated exception.
/// </summary>
public static class Hunks
{
    /// <summary>
    /// Context lines kept either side of a change.
    ///
    /// Three, which is what every Git tool emits and therefore what <c>git apply</c> is most
    /// forgiving about. Fewer makes a patch that fails to locate itself when the index has moved on;
    /// more makes neighbouring changes merge into one hunk the user cannot stage separately.
    /// </summary>
    private const int ContextLines = 3;

    /// <summary>
    /// Every hunk in <paramref name="rows"/>, in order.
    ///
    /// Two changes closer together than twice the context become one hunk, because their context
    /// would otherwise overlap and a patch cannot describe the same line twice.
    /// </summary>
    public static IReadOnlyList<DiffHunk> Find(IReadOnlyList<DiffRow> rows)
    {
        var hunks = new List<DiffHunk>();
        int row = 0;

        while (row < rows.Count)
        {
            if (!IsChange(rows[row]))
            {
                row++;
                continue;
            }

            int first = row;
            int last = row;

            //Walk forward, absorbing any further change whose context would touch this one.
            while (last + 1 < rows.Count)
            {
                int next = NextChange(rows, last + 1);

                if (next < 0 || next - last > ContextLines * 2)
                    break;

                last = next;
            }

            hunks.Add(Build(rows, Math.Max(0, first - ContextLines), Math.Min(rows.Count - 1, last + ContextLines)));
            row = last + 1;
        }

        return hunks;
    }

    /// <summary>
    /// A patch that stages exactly the changed rows in <paramref name="staged"/>.
    ///
    /// The same function serves whole-hunk and selected-line staging, because they differ only in
    /// which rows are in the set. Rows inside the window that are <i>not</i> staged are demoted rather
    /// than dropped, and the two directions are not symmetric:
    ///
    /// <list type="bullet">
    /// <item><description>An unstaged <b>deletion</b> becomes a context line. The line is not being
    /// removed, so it is present on both sides of this patch.</description></item>
    /// <item><description>An unstaged <b>insertion</b> is omitted entirely. It is on neither side: not
    /// in the index, and not being added to it.</description></item>
    /// </list>
    ///
    /// Getting that pair the wrong way round produces a patch that applies cleanly and stages the
    /// opposite of what the user picked, which is why it is spelled out rather than inlined.
    /// </summary>
    /// <returns>The patch text, or null when nothing in <paramref name="staged"/> changes anything.</returns>
    public static string? ToPatch(
        string repositoryRelativePath,
        IReadOnlyList<DiffRow> rows,
        IReadOnlySet<int> staged,
        FileText left,
        FileText right)
    {
        if (staged.Count == 0)
            return null;

        int first = int.MaxValue;
        int last = int.MinValue;

        foreach (int row in staged)
        {
            if (row < 0 || row >= rows.Count || !IsChange(rows[row]))
                continue;

            first = Math.Min(first, row);
            last = Math.Max(last, row);
        }

        if (first > last)
            return null;

        int windowStart = Math.Max(0, first - ContextLines);
        int windowEnd = Math.Min(rows.Count - 1, last + ContextLines);

        var body = new StringBuilder();
        int leftStart = 0;
        int leftCount = 0;
        int rightStart = 0;
        int rightCount = 0;

        //The right-hand line number has to be tracked rather than read off the row: an unstaged
        //insertion is omitted, so after the first one the working tree's numbering and this patch's
        //numbering diverge. The left side never diverges, because nothing is dropped from it.
        for (int row = windowStart; row <= windowEnd; row++)
        {
            DiffRow current = rows[row];
            bool isStaged = staged.Contains(row);

            if (!IsChange(current))
            {
                //A file that ends with a newline produces one row past its last line: the empty
                //string after the final terminator. It is not a line, and emitting it as context
                //makes the hunk header claim one line more than the file has -- which git rejects as
                //"patch does not apply", at the end of every file.
                if (!IsRealLine(left, current.Left.LineNumber))
                    continue;

                Emit(body, ' ', current.Left, left);
                Count(current.Left, ref leftStart, ref leftCount);
                Count(current.Right, ref rightStart, ref rightCount);
                continue;
            }

            if (IsRealLine(left, current.Left.LineNumber))
            {
                //Staged: removed from the index. Not staged: still there, so context.
                Emit(body, isStaged ? '-' : ' ', current.Left, left);
                Count(current.Left, ref leftStart, ref leftCount);

                if (!isStaged)
                    Count(current.Left, ref rightStart, ref rightCount);
            }

            if (isStaged && IsRealLine(right, current.Right.LineNumber))
            {
                Emit(body, '+', current.Right, right);
                Count(current.Right, ref rightStart, ref rightCount);
            }
        }

        if (leftCount == 0 && rightCount == 0)
            return null;

        //Unified-diff convention: a side contributing nothing is written as start 0.
        string path = repositoryRelativePath.Replace('\\', '/');

        var patch = new StringBuilder();
        patch.Append("diff --git a/").Append(path).Append(" b/").Append(path).Append('\n');
        patch.Append("--- a/").Append(path).Append('\n');
        patch.Append("+++ b/").Append(path).Append('\n');
        patch.Append("@@ -").Append(leftCount == 0 ? 0 : leftStart).Append(',').Append(leftCount)
             .Append(" +").Append(rightCount == 0 ? 0 : rightStart).Append(',').Append(rightCount)
             .Append(" @@\n");
        patch.Append(body);

        return patch.ToString();
    }

    /// <summary>The set of changed rows in <paramref name="hunk"/> — what staging the whole hunk means.</summary>
    public static IReadOnlySet<int> RowsOf(IReadOnlyList<DiffRow> rows, DiffHunk hunk)
    {
        var set = new HashSet<int>();

        for (int row = hunk.FirstRow; row <= hunk.LastRow; row++)
        {
            if (IsChange(rows[row]))
                set.Add(row);
        }

        return set;
    }

    /// <summary>
    /// Whether <paramref name="row"/> represents a difference.
    ///
    /// A filler row is padding the renderers use to keep the panes aligned; it has no line on either
    /// side and so contributes nothing to a patch.
    ///
    /// Public because the viewer has to answer "would staging this do anything?" before it offers the
    /// button, and answering it with its own copy of this rule is how the two would come to disagree.
    /// </summary>
    public static bool IsChange(DiffRow row) =>
        row.Kind is not DiffLineKind.Unchanged
        && (row.Left.LineNumber is not null || row.Right.LineNumber is not null);

    /// <summary>
    /// The right-hand text with the selected changes put back the way the left side has them.
    ///
    /// <b>This is "revert these lines", and it is the only thing in the viewer that removes the
    /// user's work rather than adding to it.</b> Two properties make that safe, and both come from
    /// where the result goes rather than from anything here:
    ///
    /// <list type="bullet">
    /// <item><description>It returns <i>text</i>, not a file operation. The caller puts it in the
    /// editor, which means <c>Ctrl+Z</c> undoes it and nothing has touched the disk. CLAUDE.md's
    /// "never discard uncommitted work" is satisfied by the change being an ordinary edit the user
    /// can walk back, and by <c>Ctrl+S</c> still being the only thing that
    /// writes.</description></item>
    /// <item><description>It works in the normalised <c>\n</c> text the viewer holds, and says
    /// nothing about line endings. <see cref="ToPatch"/> above has to re-terminate every line from
    /// the original bytes because <c>git apply</c> compares them byte for byte; this does not,
    /// because <c>WorkingTreeWriter</c> restores the file's own encoding, BOM and endings when it
    /// saves. Two places deciding line endings is how a one-line revert becomes a whole-file
    /// diff.</description></item>
    /// </list>
    ///
    /// Every row is emitted in order: a selected change contributes its <b>left</b> side, everything
    /// else contributes its <b>right</b> side. A side that is filler contributes no line at all,
    /// which is what makes the two directions work out of one rule — reverting an added line drops
    /// it, and reverting a deleted line brings it back.
    /// </summary>
    /// <param name="rows">
    /// The <i>live</i> alignment, not the one the diff was first computed with. After an edit the
    /// viewer re-diffs, and reverting against a stale row list would rewrite lines the user has since
    /// changed.
    /// </param>
    /// <param name="selected">Row indices to take from the left. Anything else is ignored.</param>
    /// <returns>
    /// The complete new text for the right-hand side, or null when the selection contains no change
    /// — where returning the text unaltered would mark the editor dirty for nothing.
    /// </returns>
    public static string? RevertRows(IReadOnlyList<DiffRow> rows, IReadOnlySet<int> selected)
    {
        bool anything = false;

        foreach (int row in selected)
        {
            if (row >= 0 && row < rows.Count && IsChange(rows[row]))
            {
                anything = true;
                break;
            }
        }

        if (!anything)
            return null;

        var lines = new List<string>(rows.Count);

        for (int row = 0; row < rows.Count; row++)
        {
            //A selected change takes the left side; everything else keeps what the file has now.
            DiffSide side = selected.Contains(row) && IsChange(rows[row]) ? rows[row].Left : rows[row].Right;

            //Filler contributes nothing. That is the whole of the asymmetry between reverting an
            //insertion and reverting a deletion.
            if (!side.IsFiller)
                lines.Add(side.Text);
        }

        //Joined with \n and nothing else. A file that ends with a newline arrives here as a final
        //empty row, so the join reproduces that terminator without this having to know whether the
        //file had one -- and a file that does not, does not gain one.
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Whether <paramref name="lineNumber"/> names a line the file actually has.
    ///
    /// The differ works on text split by newline, so a file ending with one yields a final empty
    /// element that is not a line. It arrives here as a row numbered one past the end, and every
    /// patch whose context reaches the bottom of a file depends on it being dropped.
    /// </summary>
    private static bool IsRealLine(FileText file, int? lineNumber) =>
        lineNumber is { } line && line >= 1 && line <= file.LineCount;

    private static int NextChange(IReadOnlyList<DiffRow> rows, int from)
    {
        for (int row = from; row < rows.Count; row++)
        {
            if (IsChange(rows[row]))
                return row;
        }

        return -1;
    }

    private static DiffHunk Build(IReadOnlyList<DiffRow> rows, int firstRow, int lastRow)
    {
        int leftStart = 0;
        int leftCount = 0;
        int rightStart = 0;
        int rightCount = 0;

        for (int row = firstRow; row <= lastRow; row++)
        {
            Count(rows[row].Left, ref leftStart, ref leftCount);
            Count(rows[row].Right, ref rightStart, ref rightCount);
        }

        return new DiffHunk(firstRow, lastRow, leftStart, leftCount, rightStart, rightCount);
    }

    private static void Count(DiffSide side, ref int start, ref int count)
    {
        if (side.LineNumber is not { } line)
            return;

        if (count == 0)
            start = line;

        count++;
    }

    /// <summary>
    /// Writes one patch line: the prefix, the text, and the terminator that line has in its file.
    ///
    /// The terminator is the load-bearing part. <c>git apply</c> compares a context line to the index
    /// byte for byte, and in a CRLF file the line's content ends with a carriage return — so the patch
    /// has to carry it, or nothing matches.
    /// </summary>
    private static void Emit(StringBuilder body, char prefix, DiffSide side, FileText file)
    {
        body.Append(prefix).Append(side.Text);

        string terminator = TerminatorFor(file, side.LineNumber);

        if (terminator.Length == 0)
        {
            //The file's last line is unterminated. Saying so is not decoration: without this marker
            //git apply would add a newline the user never asked for.
            body.Append('\n').Append("\\ No newline at end of file\n");
            return;
        }

        //Everything before the final \n is part of the line's content as git sees it; the \n is the
        //patch's own line break.
        body.Append(terminator[..^1]).Append('\n');
    }

    /// <summary>
    /// What terminated line <paramref name="lineNumber"/> in <paramref name="file"/>.
    ///
    /// Empty means the line was the last one and had no terminator. A mixed-ending file is answered
    /// per line, because rewriting one kind as the other is exactly the whole-file diff
    /// <see cref="FileText"/> exists to prevent.
    /// </summary>
    private static string TerminatorFor(FileText file, int? lineNumber)
    {
        if (lineNumber is not { } line)
            return file.NewLine;

        if (file.PerLineEndings is { } endings)
            return line >= 1 && line <= endings.Count ? endings[line - 1] : file.NewLine;

        bool isLastLine = line >= file.LineCount;

        return isLastLine && !file.EndsWithNewline ? string.Empty : file.NewLine;
    }
}
