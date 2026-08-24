using System.Windows;
using System.Windows.Media;
using FlickGit.Blame;
using ICSharpCode.AvalonEdit.Rendering;

namespace FlickGit.App.Rendering;

/// <summary>
/// Tints every line the selected commit is responsible for.
///
/// This is what a click buys beyond the detail band: selecting one line shows the <i>rest</i> of that
/// commit's work in the same file, which is usually the question behind "who wrote this" — not the
/// line, the change it was part of.
///
/// An <see cref="IBackgroundRenderer"/> rather than per-line elements, per CLAUDE.md: it draws only
/// over the lines currently on screen and adds nothing to the visual tree.
/// </summary>
public sealed class BlameBackgroundRenderer : IBackgroundRenderer
{
    private IReadOnlyList<BlameLine> _lines = [];
    private string? _sha;

    public KnownLayer Layer => KnownLayer.Background;

    public void SetLines(IReadOnlyList<BlameLine> lines)
    {
        _lines = lines;
        _sha = null;
    }

    /// <summary>The selected commit, or null to highlight nothing.</summary>
    public void Select(string? sha) => _sha = sha;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (_sha is null || _lines.Count == 0 || !textView.VisualLinesValid)
            return;

        foreach (VisualLine line in textView.VisualLines)
        {
            int index = line.FirstDocumentLine.LineNumber - 1;

            if (index < 0 || index >= _lines.Count)
                continue;

            if (!string.Equals(_lines[index].Commit.Sha, _sha, StringComparison.Ordinal))
                continue;

            drawingContext.DrawRectangle(
                DiffBrushes.BlameSelected,
                pen: null,
                new Rect(0, line.VisualTop - textView.VerticalOffset, textView.ActualWidth, line.Height));
        }
    }
}
