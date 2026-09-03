using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace FlickGit.App.Mac.Rendering;

/// <summary>
/// Lights the search matches in one pane.
///
/// <c>KnownLayer.Selection</c> rather than Background: the diff colours already own the background,
/// and a match inside a changed line has to be visible against them.
/// </summary>
internal sealed class SearchHighlightRenderer : IBackgroundRenderer
{
    private static readonly IPen MatchPen =
        new Pen(DiffBrushes.SearchMatchBorder, 1).ToImmutable();

    private IReadOnlyList<ISegment> _matches = [];

    public KnownLayer Layer => KnownLayer.Selection;

    public void SetMatches(IReadOnlyList<ISegment> matches) => _matches = matches;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_matches.Count == 0 || !textView.VisualLinesValid || textView.VisualLines.Count == 0)
            return;

        //The visible window, so a term with a thousand hits in a long file costs one comparison each
        //rather than a geometry per match. BackgroundGeometryBuilder would clip them correctly and
        //charge for the attempt.
        int from = textView.VisualLines[0].FirstDocumentLine.Offset;
        int to = textView.VisualLines[^1].LastDocumentLine.EndOffset;

        foreach (ISegment match in _matches)
        {
            if (match.EndOffset < from || match.Offset > to)
                continue;

            var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 1 };
            builder.AddSegment(textView, match);

            if (builder.CreateGeometry() is { } geometry)
                drawingContext.DrawGeometry(DiffBrushes.SearchMatch, MatchPen, geometry);
        }
    }
}
