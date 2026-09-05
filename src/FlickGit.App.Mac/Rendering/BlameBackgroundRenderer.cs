using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using FlickGit.Blame;

namespace FlickGit.App.Mac.Rendering;

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
internal sealed class BlameBackgroundRenderer : IBackgroundRenderer
{
    private IReadOnlyList<BlameLine> _lines = [];
    private string? _sha;

    public KnownLayer Layer => KnownLayer.Background;

    public void SetLines(IReadOnlyList<BlameLine> lines)
    {
        _lines = lines;

        //A selection belongs to the blame it was made in. Carrying one across a walk would tint lines
        //of a different revision that happen to share a hash prefix with nothing.
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

            drawingContext.FillRectangle(
                DiffBrushes.BlameSelected,
                new Rect(0, line.VisualTop - textView.VerticalOffset, textView.Bounds.Width, line.Height));
        }
    }
}
