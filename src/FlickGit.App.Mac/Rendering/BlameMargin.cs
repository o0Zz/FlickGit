using System.Globalization;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using FlickGit.Blame;

namespace FlickGit.App.Mac.Rendering;

/// <summary>
/// The blame gutter: who last touched each line, and when.
///
/// A margin rather than a column in a list, and that is what makes the window work: the annotation
/// stays put while a long line scrolls sideways, the code keeps its syntax highlighting, and — per
/// CLAUDE.md, "never insert a visual element per line" — a whole screen costs one
/// <see cref="DrawingContext"/> rather than a <c>Grid</c> and four <c>TextBlock</c>s per row.
///
/// <b>The annotation is drawn once per run, not once per line.</b> Twenty consecutive lines from one
/// commit repeat the same hash twenty times otherwise, which is how a blame becomes unreadable — the
/// eye is looking for where authorship <i>changes</i>, and only the first line of a run carries that.
///
/// <b>Ported from the WPF margin.</b> <c>AbstractMargin</c> survives; what differs is the drawing
/// vocabulary, and that a click arrives through <c>PointerPressed</c> rather than
/// <c>OnMouseLeftButtonDown</c>.
/// </summary>
internal sealed class BlameMargin : AbstractMargin
{
    private const double HorizontalPadding = 8;

    /// <summary>
    /// Space between the hash, the author and the date columns.
    ///
    /// Fixed columns rather than one composed string, so the three read as columns down the gutter
    /// instead of as ragged sentences.
    /// </summary>
    private const double ColumnGap = 10;

    /// <summary>
    /// Characters of author name shown.
    ///
    /// A cap rather than the widest name, because <see cref="MeasureOverride"/> sizes the gutter from
    /// the widest value it will ever draw — so one contributor with a very long display name would
    /// otherwise push the code halfway across the window for the whole file.
    /// </summary>
    private const int AuthorWidth = 12;

    private IReadOnlyList<BlameLine> _lines = [];
    private Typeface _typeface = new("monospace");
    private double _emSize = 12;
    private double _shaWidth;
    private double _authorWidth;

    /// <summary>Raised with a 1-based line number when the gutter itself is clicked.</summary>
    public event Action<int>? LineClicked;

    public BlameMargin() =>
        //The gutter is a second way to pick a line, so it says so.
        Cursor = new Cursor(StandardCursorType.Hand);

    public void SetLines(IReadOnlyList<BlameLine> lines)
    {
        _lines = lines;

        //The widest author and hash may have changed, so the width is re-measured before the next
        //paint rather than only repainted.
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Kept in step with the editor, so the gutter sits on the same baseline as the code.</summary>
    public void SetTypography(FontFamily family, double fontSize)
    {
        _typeface = new Typeface(family);
        _emSize = fontSize;

        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView is not null)
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;

        if (newTextView is not null)
            newTextView.VisualLinesChanged += OnVisualLinesChanged;

        base.OnTextViewChanged(oldTextView, newTextView);
        InvalidateVisual();
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (TextView is not { VisualLinesValid: true } view)
            return;

        //The y the pointer reports is relative to the margin, which is scrolled with the view --
        //hence adding the offset back before asking which visual line is there.
        double y = e.GetPosition(this).Y + view.VerticalOffset;

        if (view.GetVisualLineFromVisualTop(y) is not { } line)
            return;

        LineClicked?.Invoke(line.FirstDocumentLine.LineNumber);
        e.Handled = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        //Measured from the widest value this blame will ever draw, once per load -- not from a pixel
        //constant, which is right at one font size and one DPI and wrong at every other.
        _shaWidth = Format("0000000", DiffBrushes.LineNumber).Width;
        _authorWidth = 0;

        foreach (BlameLine line in _lines)
        {
            double width = Format(Author(line.Commit), DiffBrushes.LineNumber).Width;

            if (width > _authorWidth)
                _authorWidth = width;
        }

        double date = Format("0000-00-00", DiffBrushes.LineNumber).Width;

        return new Size(
            HorizontalPadding + _shaWidth + ColumnGap + _authorWidth + ColumnGap + date + HorizontalPadding,
            0);
    }

    public override void Render(DrawingContext context)
    {
        //Also what makes the margin hit-testable: a control with no rendered background is not
        //clickable, and clicking the gutter is the second way to pick a line.
        context.FillRectangle(DiffBrushes.Gutter, new Rect(Bounds.Size));

        if (TextView is not { VisualLinesValid: true } view || _lines.Count == 0)
            return;

        foreach (VisualLine visual in view.VisualLines)
        {
            int index = visual.FirstDocumentLine.LineNumber - 1;

            if (index < 0 || index >= _lines.Count)
                continue;

            //Only the first line of a run from one commit is annotated. The rest are deliberately
            //blank -- see the class comment.
            if (index > 0
                && string.Equals(_lines[index - 1].Commit.Sha, _lines[index].Commit.Sha, StringComparison.Ordinal))
            {
                continue;
            }

            BlameCommit commit = _lines[index].Commit;
            double y = visual.VisualTop - view.VerticalOffset;
            double x = HorizontalPadding;

            //An uncommitted line has no hash worth showing -- forty zeros abbreviate to "0000000",
            //which reads as a real commit. The bullet is the honest form.
            IBrush shaBrush = commit.IsUncommitted ? DiffBrushes.LineNumber : DiffBrushes.BlameSha;
            string sha = commit.IsUncommitted ? "•" : commit.ShortSha;

            context.DrawText(Format(sha, shaBrush), new Point(x, y));
            x += _shaWidth + ColumnGap;

            context.DrawText(Format(Author(commit), DiffBrushes.LineNumber), new Point(x, y));
            x += _authorWidth + ColumnGap;

            context.DrawText(
                Format(
                    commit.When.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    DiffBrushes.LineNumber),
                new Point(x, y));
        }
    }

    private static string Author(BlameCommit commit)
    {
        if (commit.IsUncommitted)
            return "uncommitted";

        return commit.Author.Length > AuthorWidth ? commit.Author[..AuthorWidth] : commit.Author;
    }

    private FormattedText Format(string text, IBrush brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, _emSize, brush);
}
