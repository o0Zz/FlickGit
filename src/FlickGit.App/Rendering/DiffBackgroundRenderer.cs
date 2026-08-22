using System.Windows;
using System.Windows.Media;
using FlickGit.Diff;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace FlickGit.App.Rendering;

/// <summary>
/// Paints the diff colours behind one pane's text.
///
/// An <see cref="IBackgroundRenderer"/>, and CLAUDE.md is specific about why: "Change bars
/// and line backgrounds via <c>IBackgroundRenderer</c> — never insert a visual element per
/// line." A 2,000-line file with a per-line Border would put 2,000 elements into the visual
/// tree, each with its own layout and hit-testing; this draws into one drawing context per
/// paint and only for the lines currently on screen.
///
/// Both panes share this class. The pane decides which colours apply to it — a deleted line
/// is red on the left and blank on the right, and inverting that is the whole difference
/// between the two instances.
/// </summary>
public sealed class DiffBackgroundRenderer(bool isLeftPane) : IBackgroundRenderer
{
    private IReadOnlyList<DiffRow> _rows = [];

    /// <summary>
    /// Behind the text and behind the selection. Any layer above would paint over the
    /// glyphs and over the caret.
    /// </summary>
    public KnownLayer Layer => KnownLayer.Background;

    /// <summary>
    /// Swaps in a new diff. The rows are indexed by document line, which holds because the
    /// pane builds its document from these same rows — one document line per row, filler
    /// rows included.
    /// </summary>
    public void SetRows(IReadOnlyList<DiffRow> rows) => _rows = rows;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_rows.Count == 0 || !textView.VisualLinesValid)
            return;

        foreach (VisualLine line in textView.VisualLines)
        {
            int rowIndex = line.FirstDocumentLine.LineNumber - 1;
            if (rowIndex < 0 || rowIndex >= _rows.Count)
                continue;

            DiffRow row = _rows[rowIndex];
            DiffSide side = isLeftPane ? row.Left : row.Right;

            Brush? lineBrush = LineBrush(row.Kind, side);
            if (lineBrush is not null)
            {
                //Full width, not the width of the text: a changed line reads as a changed
                //*row*, and a background that stops at the last character makes a
                //two-character line look like a rendering artefact.
                double top = line.VisualTop - textView.VerticalOffset;

                drawingContext.DrawRectangle(
                    lineBrush,
                    pen: null,
                    new Rect(0, top, textView.ActualWidth, line.Height));
            }

            if (side.ChangedSpans.Count == 0)
                continue;

            Brush wordBrush = isLeftPane ? DiffBrushes.DeletedWord : DiffBrushes.InsertedWord;
            int lineOffset = line.FirstDocumentLine.Offset;
            int lineLength = line.FirstDocumentLine.Length;

            foreach (DiffSpan span in side.ChangedSpans)
            {
                //Clamped to the line. The spans were computed against the diff's copy of
                //the text; if the two ever disagree, drawing outside the line would throw
                //rather than degrade.
                int start = Math.Clamp(span.Start, 0, lineLength);
                int end = Math.Clamp(span.Start + span.Length, start, lineLength);

                if (end == start)
                    continue;

                var builder = new BackgroundGeometryBuilder
                {
                    AlignToWholePixels = true,
                    CornerRadius = 1,
                };

                builder.AddSegment(textView, new TextSegment
                {
                    StartOffset = lineOffset + start,
                    EndOffset = lineOffset + end,
                });

                if (builder.CreateGeometry() is { } geometry)
                    drawingContext.DrawGeometry(wordBrush, pen: null, geometry);
            }
        }
    }

    private Brush? LineBrush(DiffLineKind kind, DiffSide side) => kind switch
    {
        //A filler row exists only so the two panes stay aligned. Shading it is what makes
        //the alignment legible -- without it the reader has to count blank lines to see
        //that the other pane has content there.
        DiffLineKind.Filler => DiffBrushes.Neutral,

        DiffLineKind.Inserted => isLeftPane ? DiffBrushes.Neutral : DiffBrushes.Inserted,
        DiffLineKind.Deleted => isLeftPane ? DiffBrushes.Deleted : DiffBrushes.Neutral,
        DiffLineKind.Modified => isLeftPane ? DiffBrushes.Deleted : DiffBrushes.Inserted,

        //An unchanged row on a side with no line is padding introduced by the aligner.
        _ => side.IsFiller ? DiffBrushes.Neutral : null,
    };

}
