using System.Globalization;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using FlickGit.Diff;

namespace FlickGit.App.Mac.Rendering;

/// <summary>
/// The line-number gutter, showing each line's number <b>in its own file</b> rather than in the
/// document being displayed.
///
/// The built-in margin cannot be used here. Both panes render one document line per diff row, filler
/// rows included, so document line 43 in the right pane may be line 41 of the actual file. Showing
/// the document's numbering would put two columns of numbers on screen that disagree with
/// <c>git diff</c>, with the editor, and with each other.
///
/// <b>Ported from the WPF margin.</b> <c>AbstractMargin</c> survives; what differs is that Avalonia
/// measures and renders through <c>MeasureOverride</c>/<c>Render</c> rather than
/// <c>MeasureOverride</c>/<c>OnRender</c>, and that <see cref="FormattedText"/> is constructed with
/// its typeface and size rather than configured afterwards.
/// </summary>
internal sealed class DiffLineNumberMargin(bool isLeftPane) : AbstractMargin
{
    private const double HorizontalPadding = 6;

    private IReadOnlyList<DiffRow> _rows = [];
    private Typeface _typeface = new("monospace");
    private double _emSize = 12;

    public void SetRows(IReadOnlyList<DiffRow> rows)
    {
        _rows = rows;

        //The widest number may have changed, so the gutter width has to be re-measured before the
        //next paint — not just repainted.
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Kept in step with the editors, so the numbers sit on the same baseline as the code.</summary>
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
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
    {
        //Measured from the widest number the file actually contains, so the gutter does not jump
        //when scrolling from line 99 to line 100.
        int widest = 0;

        foreach (DiffRow row in _rows)
        {
            int? number = (isLeftPane ? row.Left : row.Right).LineNumber;

            if (number > widest)
                widest = number.Value;
        }

        FormattedText sample = Text(widest == 0 ? "0" : widest.ToString(CultureInfo.InvariantCulture));

        return new Size(sample.Width + (HorizontalPadding * 2), 0);
    }

    public override void Render(DrawingContext context)
    {
        TextView? view = TextView;

        if (view is null || !view.VisualLinesValid)
            return;

        context.FillRectangle(DiffBrushes.Gutter, new Rect(Bounds.Size));

        foreach (VisualLine line in view.VisualLines)
        {
            int rowIndex = line.FirstDocumentLine.LineNumber - 1;

            if (rowIndex < 0 || rowIndex >= _rows.Count)
                continue;

            int? number = (isLeftPane ? _rows[rowIndex].Left : _rows[rowIndex].Right).LineNumber;

            //Null on a filler row: this side has no line there, and a number would claim it does.
            if (number is null)
                continue;

            FormattedText text = Text(number.Value.ToString(CultureInfo.InvariantCulture));

            context.DrawText(
                text,
                new Point(
                    Bounds.Width - HorizontalPadding - text.Width,
                    line.VisualTop - view.VerticalOffset));
        }
    }

    private FormattedText Text(string value) =>
        new(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, _emSize,
            DiffBrushes.LineNumber);
}
