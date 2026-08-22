using System.Text;

namespace FlickGit.Diff;

/// <summary>
/// Converts between a diff's aligned rows and the two documents the viewer displays.
///
/// This exists because of a hazard rather than for tidiness. The two panes are kept in alignment by
/// giving each one a document with the same number of lines — which means the right-hand document
/// contains blank <i>filler</i> lines that are not in the file. Saving that document verbatim would
/// write blank lines into the user's source, which is the worst thing this product could do to a
/// working tree.
///
/// So the reconstruction is a named, tested function rather than a loop inside a WPF code-behind.
/// <see cref="ToFileText"/> is the only thing that may ever be written to disk.
/// </summary>
public static class DiffDocument
{
    /// <summary>
    /// Builds both pane documents from a row list, and reports which right-hand lines are filler.
    /// </summary>
    /// <returns>
    /// The two documents and the 1-based line numbers that are filler on the right. Both documents
    /// always have exactly <c>rows.Count</c> lines, which is what makes scroll synchronisation an
    /// offset copy instead of a mapping.
    /// </returns>
    public static (string Left, string Right, IReadOnlyList<int> RightFillerLines) Build(IReadOnlyList<DiffRow> rows)
    {
        var left = new StringBuilder();
        var right = new StringBuilder();
        var fillers = new List<int>();

        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
            {
                left.Append('\n');
                right.Append('\n');
            }

            left.Append(rows[i].Left.Text);
            right.Append(rows[i].Right.Text);

            if (rows[i].Right.IsFiller)
                fillers.Add(i + 1);
        }

        return (left.ToString(), right.ToString(), fillers);
    }

    /// <summary>
    /// Reconstructs the file's text from the editor's lines.
    ///
    /// A line is dropped only when it is <b>both</b> still marked as filler <b>and</b> still empty.
    /// Those two conditions together are what makes this safe in the two cases that matter:
    ///
    /// <list type="bullet">
    /// <item><description>The user <b>typed into a filler line</b>. It is no longer empty, so it is
    /// kept — it is a line they added.</description></item>
    /// <item><description>The user <b>added a genuinely empty line</b>. It was never filler, so it
    /// is kept.</description></item>
    /// </list>
    ///
    /// <paramref name="fillerLines"/> must be current. In the viewer that is guaranteed by tracking
    /// each filler with an AvalonEdit <c>TextAnchor</c>, which the document moves as text is
    /// inserted and deleted; a plain list of line numbers captured at render time would go stale on
    /// the first inserted line and start dropping the wrong ones.
    /// </summary>
    /// <param name="documentLines">Every line of the editor's document, in order, without terminators.</param>
    /// <param name="fillerLines">1-based line numbers currently known to be filler.</param>
    /// <param name="endsWithNewline">Whether the file being reconstructed ends with a newline.</param>
    public static string ToFileText(
        IReadOnlyList<string> documentLines,
        IReadOnlyCollection<int> fillerLines,
        bool endsWithNewline)
    {
        var fillers = fillerLines as IReadOnlySet<int> ?? fillerLines.ToHashSet();
        var builder = new StringBuilder();
        bool first = true;

        for (int i = 0; i < documentLines.Count; i++)
        {
            string line = documentLines[i];

            if (line.Length == 0 && fillers.Contains(i + 1))
                continue;

            if (!first)
                builder.Append('\n');

            builder.Append(line);
            first = false;
        }

        string text = builder.ToString();

        //The trailing newline needs care, because the document encodes it in two ways at once. A
        //file ending in a newline is held as a final *empty* line, so joining the lines has already
        //reproduced the terminator -- appending another would add a blank line to the file on every
        //save. But that final empty line can also be filler and have been dropped just above, in
        //which case the terminator has to be put back.
        //
        //So the file's own property decides, and the text is corrected either way rather than
        //assumed.
        if (text.Length == 0)
            return text;

        bool hasTrailing = text.EndsWith('\n');

        if (endsWithNewline && !hasTrailing)
            return text + '\n';

        if (!endsWithNewline && hasTrailing)
            return text[..^1];

        return text;
    }
}
