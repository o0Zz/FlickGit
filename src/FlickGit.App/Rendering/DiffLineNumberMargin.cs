using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FlickGit.Diff;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;

namespace FlickGit.App.Rendering;

/// <summary>
/// The line-number gutter, showing each line's number <b>in its own file</b> rather than in
/// the document being displayed.
///
/// AvalonEdit's built-in margin cannot be used here. Both panes render one document line per
/// diff row, filler rows included, so document line 43 in the right pane may be line 41 of
/// the actual file. Showing the document's numbering would put two columns of numbers on
/// screen that disagree with `git diff`, with the editor, and with each other.
/// </summary>
public sealed class DiffLineNumberMargin(bool isLeftPane) : AbstractMargin
{
    private const double HorizontalPadding = 6;

    private IReadOnlyList<DiffRow> _rows = [];
    private Typeface _typeface = new("Consolas");
    private double _emSize = 12;

    public void SetRows(IReadOnlyList<DiffRow> rows)
    {
        _rows = rows;

        //The widest number may have changed, so the gutter width has to be re-measured
        //before the next paint -- not just repainted.
        InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>Kept in step with the editors, so the numbers sit on the same baseline as the code.</summary>
    public void SetTypography(FontFamily family, double fontSize)
    {
        _typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _emSize = fontSize;

        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (oldTextView is not null)
            oldTextView.VisualLinesChanged -= OnVisualLinesChanged;

        if (newTextView is not null)
            newTextView.VisualLinesChanged += OnVisualLinesChanged;

        base.OnTextViewChanged(oldTextView, newTextView);
        InvalidateVisual();
    }

    private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
    {
        //Width from the widest number this file will ever show, measured once per diff, not
        //from a pixel constant: a constant is right at one font size and one DPI and wrong
        //at every other.
        int highest = 0;

        foreach (DiffRow row in _rows)
        {
            DiffSide side = isLeftPane ? row.Left : row.Right;
            if (side.LineNumber is { } number && number > highest)
                highest = number;
        }

        FormattedText sample = Format(Math.Max(highest, 99).ToString(CultureInfo.InvariantCulture));
        return new Size(sample.Width + (HorizontalPadding * 2), 0);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawRectangle(DiffBrushes.Gutter, pen: null, new Rect(0, 0, RenderSize.Width, RenderSize.Height));

        TextView? view = TextView;
        if (view is null || !view.VisualLinesValid || _rows.Count == 0)
            return;

        foreach (VisualLine line in view.VisualLines)
        {
            int rowIndex = line.FirstDocumentLine.LineNumber - 1;
            if (rowIndex < 0 || rowIndex >= _rows.Count)
                continue;

            DiffSide side = isLeftPane ? _rows[rowIndex].Left : _rows[rowIndex].Right;

            //A filler row has no number on this side. Blank, not zero, and not the previous
            //line's number repeated.
            if (side.LineNumber is not { } number)
                continue;

            FormattedText text = Format(number.ToString(CultureInfo.InvariantCulture));

            //Right-aligned, so the digits line up as the numbers grow past a power of ten.
            drawingContext.DrawText(
                text,
                new Point(
                    RenderSize.Width - HorizontalPadding - text.Width,
                    line.VisualTop - view.VerticalOffset));
        }
    }

    private FormattedText Format(string text) =>
        new(text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            _typeface,
            _emSize,
            DiffBrushes.LineNumber,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

}
