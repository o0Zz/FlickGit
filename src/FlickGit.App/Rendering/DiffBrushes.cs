using System.Windows.Media;

namespace FlickGit.App.Rendering;

/// <summary>
/// The colours the three diff renderers paint with, created once and frozen.
///
/// <b>Frozen is the point.</b> A frozen <see cref="Brush"/> can be shared across threads and skips
/// WPF's change tracking, and these are drawn on every paint of a scrolling editor — the one place in
/// the product where that cost is real. Allocating a brush per paint is the classic way a diff viewer
/// becomes sluggish on a long file.
///
/// Gathered here because the four-line helper that makes them existed three times, once per renderer,
/// and <see cref="Neutral"/> was declared twice with the same literal under two different names.
/// Naming each colour once is also what stops the green behind an inserted line from drifting away
/// from the green in the gutter beside it.
/// </summary>
internal static class DiffBrushes
{
    /// <summary>The row behind an inserted line.</summary>
    public static readonly Brush Inserted = Frozen("#FFE4F5E7");

    /// <summary>The changed run inside an inserted line — the word-level highlight.</summary>
    public static readonly Brush InsertedWord = Frozen("#FFB6E5BF");

    /// <summary>The row behind a deleted line.</summary>
    public static readonly Brush Deleted = Frozen("#FFFBE7E9");

    /// <summary>The changed run inside a deleted line.</summary>
    public static readonly Brush DeletedWord = Frozen("#FFF4C0C5");

    /// <summary>
    /// Where there is nothing: a filler row in the editors, and the connector strip's own background.
    /// One colour because it means one thing — this side has no line here.
    /// </summary>
    public static readonly Brush Neutral = Frozen("#FFF6F6F8");

    /// <summary>The line-number margin's background.</summary>
    public static readonly Brush Gutter = Frozen("#FFF2F3F7");

    /// <summary>The line numbers themselves, and any hairline drawn beside them.</summary>
    public static readonly Brush LineNumber = Frozen("#FF8A93A6");

    /// <summary>The connector's tie for an insertion. Lighter than the row, so it reads as a link.</summary>
    public static readonly Brush ConnectorInserted = Frozen("#FFCCEAD3");

    /// <summary>The connector's tie for a deletion.</summary>
    public static readonly Brush ConnectorDeleted = Frozen("#FFF2CDD1");

    /// <summary>The connector's tie for a modified pair, which exists on both sides.</summary>
    public static readonly Brush ConnectorModified = Frozen("#FFD9E2F0");

    /// <summary>
    /// The overview strip's marks. Saturated where the row colours are pale, and that is not an
    /// inconsistency: a row colour sits behind text and must not fight it, while a two-pixel mark
    /// on a grey column has nothing to lose contrast to and everything to gain from having it.
    /// </summary>
    public static readonly Brush OverviewInserted = Frozen("#FF4CA45C");

    /// <summary>The overview strip's mark for a deletion.</summary>
    public static readonly Brush OverviewDeleted = Frozen("#FFCC5A61");

    /// <summary>The overview strip's mark for a modified pair.</summary>
    public static readonly Brush OverviewModified = Frozen("#FF7A93BE");

    /// <summary>The hairline between the overview strip and the editor beside it.</summary>
    public static readonly Brush Border = Frozen("#FFDDE0E8");

    /// <summary>The abbreviated hash in the blame gutter. The accent, so the eye lands on it first.</summary>
    public static readonly Brush BlameSha = Frozen("#FF3A6EA5");

    /// <summary>
    /// Every line the selected commit is responsible for.
    ///
    /// Deliberately fainter than any diff colour: it answers "where else did this commit touch the
    /// file" while the code stays the thing being read.
    /// </summary>
    public static readonly Brush BlameSelected = Frozen("#FFEAF1FA");

    private static Brush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();

        return brush;
    }
}
