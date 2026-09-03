using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using FlickGit.Diff;

namespace FlickGit.App.Mac.Rendering;

/// <summary>
/// The whole file's changes, mapped onto the height of the pane.
///
/// Draws the entire document rather than the visible window, which is the point: it answers "is
/// there anything below where I am looking" without scrolling.
/// </summary>
internal sealed class DiffOverviewStrip : Control
{
    /// <summary>
    /// The shortest mark that is still visible. A single changed line in a two-thousand-line file is
    /// a fifth of a pixel, which rounds away to nothing — and a change you cannot see is the one
    /// thing this control must not have.
    /// </summary>
    private const double MinimumMarkHeight = 2;

    private IReadOnlyList<DiffRow> _rows = [];

    public DiffOverviewStrip() =>
        //Decorative. It maps the file rather than offering a way to move through it, so swallowing a
        //click here would only take one away from the editor underneath.
        IsHitTestVisible = false;

    public void SetRows(IReadOnlyList<DiffRow> rows)
    {
        _rows = rows;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(DiffBrushes.Gutter, new Rect(Bounds.Size));

        //A hairline against the editor, so an empty strip reads as part of the frame rather than as a
        //gap in it.
        context.FillRectangle(DiffBrushes.OverviewBorder, new Rect(0, 0, 1, Bounds.Height));

        if (_rows.Count == 0 || Bounds.Height <= 0)
            return;

        //Modified first, then insertions, then deletions, so where the scale collapses several
        //changes onto one pixel the mark that survives is the strongest signal. A red mark that
        //turned out to be an insertion is a worse answer than a green one that was also a deletion.
        Draw(context, DiffLineKind.Modified, DiffBrushes.OverviewModified);
        Draw(context, DiffLineKind.Inserted, DiffBrushes.OverviewInserted);
        Draw(context, DiffLineKind.Deleted, DiffBrushes.OverviewDeleted);
    }

    private void Draw(DrawingContext context, DiffLineKind kind, IBrush brush)
    {
        double scale = Bounds.Height / _rows.Count;
        double width = Math.Max(0, Bounds.Width - 2);

        var geometry = new StreamGeometry();

        using (StreamGeometryContext figures = geometry.Open())
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

                AddMark(figures, runTop, runBottom, width);

                runTop = top;
                runBottom = bottom;
            }

            if (!double.IsNaN(runTop))
                AddMark(figures, runTop, runBottom, width);
        }

        context.DrawGeometry(brush, null, geometry);
    }

    private void AddMark(StreamGeometryContext figures, double top, double bottom, double width)
    {
        //Clamped rather than left to overflow: the last row's mark is grown to the minimum height
        //like every other, which would otherwise put it a pixel or two past the bottom edge.
        bottom = Math.Min(bottom, Bounds.Height);
        top = Math.Min(top, bottom - MinimumMarkHeight);

        figures.BeginFigure(new Point(1, top), isFilled: true);
        figures.LineTo(new Point(1 + width, top));
        figures.LineTo(new Point(1 + width, bottom));
        figures.LineTo(new Point(1, bottom));
        figures.EndFigure(true);
    }
}
