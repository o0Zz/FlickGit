using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace FlickGit.App.Rendering;

/// <summary>
/// Tints every occurrence of the search term.
///
/// <b>Every occurrence, not the current one.</b> The match the user is standing on is shown by
/// selecting it in the editor, so AvalonEdit's own selection marks it and there is no second colour
/// to keep in step with this one. What this adds is the answer to "where are the others" — which is
/// the question that makes a search bar worth having over pressing Enter until the scrollbar stops.
///
/// An <see cref="IBackgroundRenderer"/> rather than per-match visual elements, per CLAUDE.md, and at
/// <see cref="KnownLayer.Selection"/> rather than <see cref="KnownLayer.Background"/>: a match sits
/// inside a row the diff renderer has already painted, so drawing at the same layer would leave the
/// highlight underneath the tint that hides it.
/// </summary>
public sealed class SearchHighlightRenderer : IBackgroundRenderer
{
    private readonly Pen _pen = CreatePen();

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
                drawingContext.DrawGeometry(DiffBrushes.SearchMatch, _pen, geometry);
        }
    }

    private static Pen CreatePen()
    {
        var pen = new Pen(DiffBrushes.SearchMatchBorder, 1);
        pen.Freeze();

        return pen;
    }
}
