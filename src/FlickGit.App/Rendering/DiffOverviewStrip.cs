using System.Windows;
using System.Windows.Media;
using FlickGit.Diff;

namespace FlickGit.App.Rendering;

/// <summary>
/// The whole file's changes compressed into one narrow column beside the panes: a green mark
/// where something was inserted, a red one where something was deleted, blue where a line was
/// modified. What a diff tool's overview ruler is for — knowing there are four more changes
/// further down without scrolling to find out.
///
/// <b>One strip for both panes, not one each.</b> The two documents are built from the same
/// <see cref="DiffRow"/> list with fillers, so row N is document line N on both sides — a second
/// strip would be a pixel-for-pixel copy of the first. That is the same property synchronised
/// scrolling relies on.
///
/// <b>It carries no viewport marker</b>, which every standalone overview ruler needs and this one
/// does not: it sits immediately beside the right editor's own scrollbar, so the thumb is already
/// showing where the view is, a few pixels away. Drawing a second indicator of the same fact would
/// be two things to keep in step.
///
/// One <see cref="FrameworkElement"/> drawing at most three geometries, per CLAUDE.md's "never
/// insert a visual element per line" — and it does not repaint on scroll at all, because it maps
/// the entire document rather than the visible window.
/// </summary>
public sealed class DiffOverviewStrip : FrameworkElement
{
    /// <summary>
    /// The shortest mark that is still visible. A single changed line in a two-thousand-line file
    /// is a fifth of a pixel, which rounds away to nothing — and a change you cannot see is the
    /// one thing this control must not have.
    /// </summary>
    private const double MinimumMarkHeight = 2;

    private IReadOnlyList<DiffRow> _rows = [];

    public DiffOverviewStrip()
    {
        //Decorative. It maps the file rather than offering a way to move through it, so
        //swallowing a click here would only take one away from the editor underneath.
        IsHitTestVisible = false;
    }

    public void SetRows(IReadOnlyList<DiffRow> rows)
    {
        _rows = rows;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        drawingContext.DrawRectangle(DiffBrushes.Gutter, pen: null, new Rect(RenderSize));

        //A hairline against the editor, so an empty strip reads as part of the frame rather than as
        //a gap in it.
        drawingContext.DrawRectangle(DiffBrushes.OverviewBorder, pen: null, new Rect(0, 0, 1, ActualHeight));

        if (_rows.Count == 0 || ActualHeight <= 0)
            return;

        //Modified first, then insertions, then deletions, so where the scale collapses several
        //changes onto one pixel the mark that survives is the strongest signal. A red mark that
        //turned out to be an insertion is a worse answer than a green one that was also a deletion.
        Draw(drawingContext, DiffLineKind.Modified, DiffBrushes.OverviewModified);
        Draw(drawingContext, DiffLineKind.Inserted, DiffBrushes.OverviewInserted);
        Draw(drawingContext, DiffLineKind.Deleted, DiffBrushes.OverviewDeleted);
    }

    private void Draw(DrawingContext drawingContext, DiffLineKind kind, Brush brush)
    {
        double scale = ActualHeight / _rows.Count;
        double width = Math.Max(0, ActualWidth - 2);

        var geometry = new StreamGeometry();

        using (StreamGeometryContext context = geometry.Open())
        {
            //Marks are merged in *pixel* space, not row space. Two runs of the same kind three rows
            //apart are one mark on a long file, and a file with a thousand small edits would
            //otherwise put a thousand figures into a geometry a few hundred pixels tall.
            double runTop = double.NaN;
            double runBottom = 0;

            for (int index = 0; index < _rows.Count; index++)
            {
                if (_rows[index].Kind != kind)
                    continue;

                double top = index * scale;
                double bottom = Math.Max((index + 1) * scale, top + MinimumMarkHeight);

                if (double.IsNaN(runTop))
                {
                    runTop = top;
                    runBottom = bottom;
                    continue;
                }

                if (top <= runBottom)
                {
                    runBottom = Math.Max(runBottom, bottom);
                    continue;
                }

                AddMark(context, runTop, runBottom, width);

                runTop = top;
                runBottom = bottom;
            }

            if (!double.IsNaN(runTop))
                AddMark(context, runTop, runBottom, width);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(brush, pen: null, geometry);
    }

    private void AddMark(StreamGeometryContext context, double top, double bottom, double width)
    {
        //Clamped rather than left to overflow: the last row's mark is grown to the minimum height
        //like every other, which would otherwise put it a pixel or two past the bottom edge.
        bottom = Math.Min(bottom, ActualHeight);
        top = Math.Min(top, bottom - MinimumMarkHeight);

        context.BeginFigure(new Point(1, top), isFilled: true, isClosed: true);
        context.LineTo(new Point(1 + width, top), isStroked: false, isSmoothJoin: false);
        context.LineTo(new Point(1 + width, bottom), isStroked: false, isSmoothJoin: false);
        context.LineTo(new Point(1, bottom), isStroked: false, isSmoothJoin: false);
    }
}
