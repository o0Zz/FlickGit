using Avalonia.Media;

namespace FlickGit.App.Mac.Rendering;

/// <summary>
/// The diff palette, as immutable brushes built once.
///
/// The same values the WPF renderers use, so a diff looks the same on both platforms — the colours
/// are part of how the product reads, not a per-platform choice. <c>ToImmutable</c> is Avalonia's
/// equivalent of WPF's <c>Freeze</c>: it makes the brush safe to share across every line of every
/// paint without a per-draw allocation.
/// </summary>
internal static class DiffBrushes
{
    private static IBrush Fixed(string hex) => new SolidColorBrush(Color.Parse(hex)).ToImmutable();

    public static readonly IBrush Inserted = Fixed("#FFE4F5E7");
    public static readonly IBrush InsertedWord = Fixed("#FFB6E5BF");
    public static readonly IBrush Deleted = Fixed("#FFFBE7E9");
    public static readonly IBrush DeletedWord = Fixed("#FFF4C0C5");
    public static readonly IBrush Neutral = Fixed("#FFF6F6F8");
    public static readonly IBrush Gutter = Fixed("#FFF2F3F7");
    public static readonly IBrush LineNumber = Fixed("#FF8A93A6");

    public static readonly IBrush OverviewInserted = Fixed("#FF4CA45C");
    public static readonly IBrush OverviewDeleted = Fixed("#FFCC5A61");
    public static readonly IBrush OverviewModified = Fixed("#FF7A93BE");
    public static readonly IBrush OverviewBorder = Fixed("#FFDDE0E8");

    /// <summary>The abbreviated hash in the blame gutter. The accent, so the eye lands on it first.</summary>
    public static readonly IBrush BlameSha = Fixed("#FF3A6EA5");

    /// <summary>
    /// Every line the selected commit is responsible for.
    ///
    /// Deliberately fainter than any diff colour: it answers "where else did this commit touch the
    /// file" while the code stays the thing being read.
    /// </summary>
    public static readonly IBrush BlameSelected = Fixed("#FFEAF1FA");

    public static readonly IBrush SearchMatch = Fixed("#FFFFE082");
    public static readonly IBrush SearchMatchBorder = Fixed("#FFD9A106");
}
