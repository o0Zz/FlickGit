using System.Windows;
using System.Windows.Media;
using FlickGit.Diff;

namespace FlickGit.App.Rendering;

/// <summary>
/// The band between the two panes, joining each changed block on the left to its
/// counterpart on the right.
///
/// One <see cref="FrameworkElement"/> drawing one geometry, per CLAUDE.md: "The connector
/// strip between panes drawn as a single visual." The alternative — a shape per changed
/// block — puts an unbounded number of elements into the visual tree on a file with a
/// thousand small edits, and every one of them would take part in layout and hit-testing
/// for no benefit, since the strip is decorative and never clicked.
/// </summary>
public sealed class DiffConnectorStrip : FrameworkElement
{
    private IReadOnlyList<DiffRow> _rows = [];
    private double _lineHeight = 15;
    private double _verticalOffset;

    public DiffConnectorStrip()
    {
        //Decorative. Excluding it from hit-testing means a click that lands between the
        //panes still reaches whichever editor is underneath rather than being swallowed.
        IsHitTestVisible = false;
    }

    public void SetRows(IReadOnlyList<DiffRow> rows)
    {
        _rows = rows;
        InvalidateVisual();
    }

    /// <summary>
    /// Kept in step with the editors' scroll position and line height.
    ///
    /// Both come from the left editor's text view. They have to: the strip's whole job is to
    /// line up with rows that are on screen, and computing its own geometry from the row
    /// list alone would drift the moment either editor scrolled.
    /// </summary>
    public void SetViewport(double lineHeight, double verticalOffset)
    {
        if (Math.Abs(_lineHeight - lineHeight) < 0.01 && Math.Abs(_verticalOffset - verticalOffset) < 0.01)
            return;

        _lineHeight = lineHeight > 0 ? lineHeight : _lineHeight;
        _verticalOffset = verticalOffset;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawRectangle(DiffBrushes.Neutral, pen: null, new Rect(RenderSize));

        if (_rows.Count == 0 || _lineHeight <= 0)
            return;

        //Only the rows that can be on screen. A 50,000-row diff would otherwise build
        //geometry for all of them on every scroll tick.
        int first = Math.Max(0, (int)(_verticalOffset / _lineHeight) - 1);
        int last = Math.Min(_rows.Count - 1, first + (int)(ActualHeight / _lineHeight) + 2);

        //Three passes over the visible window, one per colour, because a geometry carries a
        //single brush. Still one visual and at most three geometries per paint.
        DrawRuns(drawingContext, first, last, DiffLineKind.Inserted, DiffBrushes.ConnectorInserted);
        DrawRuns(drawingContext, first, last, DiffLineKind.Deleted, DiffBrushes.ConnectorDeleted);
        DrawRuns(drawingContext, first, last, DiffLineKind.Modified, DiffBrushes.ConnectorModified);
    }

    private void DrawRuns(
        DrawingContext drawingContext,
        int first,
        int last,
        DiffLineKind kind,
        Brush brush)
    {
        var runGeometry = new StreamGeometry();

        using (StreamGeometryContext context = runGeometry.Open())
        {
            int index = first;

            while (index <= last)
            {
                if (_rows[index].Kind != kind)
                {
                    index++;
                    continue;
                }

                //Consecutive rows of the same kind become one band. A ten-line insertion is
                //one shape, not ten -- which is both fewer figures and a truer picture of
                //the change.
                int runStart = index;
                while (index <= last && _rows[index].Kind == kind)
                    index++;

                double top = (runStart * _lineHeight) - _verticalOffset;
                double bottom = (index * _lineHeight) - _verticalOffset;

                context.BeginFigure(new Point(0, top), isFilled: true, isClosed: true);
                context.LineTo(new Point(ActualWidth, top), isStroked: false, isSmoothJoin: false);
                context.LineTo(new Point(ActualWidth, bottom), isStroked: false, isSmoothJoin: false);
                context.LineTo(new Point(0, bottom), isStroked: false, isSmoothJoin: false);
            }
        }

        runGeometry.Freeze();
        drawingContext.DrawGeometry(brush, pen: null, runGeometry);
    }

}
