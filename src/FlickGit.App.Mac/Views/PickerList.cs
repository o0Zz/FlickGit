using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// The keyboard and pointer plumbing every "filter box above a list" surface needs, and the two
/// rules a row menu must not get subtly differently in each window.
///
/// <b>The macOS counterpart of <c>FlickGit.App.Infrastructure.FilterList</c>, and it exists for the
/// same reason.</b> Four windows here are the same shape — type to filter, arrow to choose, Enter to
/// act, right-click a row for what applies to it — and four copies of these thirty lines are four
/// copies that can drift. Hard Requirement 2 wants a second caller before an abstraction; there are
/// four.
///
/// It is not a port of the WPF file. The visual-tree walk is the whole difference: WPF needs
/// <c>ContainerFromElement</c> because a row template built from <c>Run</c>s produces
/// <c>ContentElement</c>s that <c>VisualTreeHelper</c> throws on. Avalonia has no such split — every
/// row template here is <c>Control</c>s all the way down — so the ancestor walk is the honest
/// mechanism rather than a workaround.
/// </summary>
internal static class PickerList
{
    /// <summary>
    /// Moves the selection with Down and Up while the caret stays in the filter box, so the whole
    /// interaction is type-then-Enter and the hands never leave the keyboard.
    ///
    /// Clamped rather than wrapping: at the bottom of a list, jumping to the top is never what the
    /// user meant. Marks the event handled, so the <c>TextBox</c> does not also act on the key.
    /// </summary>
    public static void RouteArrows(ListBox list, KeyEventArgs e)
    {
        if (list.ItemCount == 0)
            return;

        int delta = e.Key switch
        {
            Key.Down => 1,
            Key.Up => -1,
            _ => 0,
        };

        if (delta == 0)
            return;

        list.SelectedIndex = Math.Clamp(list.SelectedIndex + delta, 0, list.ItemCount - 1);

        if (list.SelectedItem is { } selected)
            list.ScrollIntoView(selected);

        e.Handled = true;
    }

    /// <summary>
    /// Settles which rows a context menu is about, before it is built from them.
    ///
    /// <b>Avalonia's <c>ListBox</c> does select on right-click, and that is not enough.</b> It
    /// selects the <i>one</i> row under the pointer, which silently collapses a multi-selection the
    /// user built up to act on — so a right-click inside a five-row selection would offer a menu
    /// about one file. The rule this restores is the Windows one: a click inside the selection means
    /// the selection, anywhere else means the row under the pointer.
    ///
    /// A click that missed every row leaves the previous selection alone, and the caller decides
    /// whether a menu with nothing selected is worth showing.
    /// </summary>
    /// <returns>False when the pointer was not over a row.</returns>
    public static bool SelectRowUnderPointer(ListBox list, object? source)
    {
        if (source is not Visual visual)
            return false;

        ListBoxItem? row = visual
            .GetSelfAndVisualAncestors()
            .OfType<ListBoxItem>()
            .FirstOrDefault();

        if (row is null)
            return false;

        //Already part of what the user built up: the menu is about all of it. Checked before
        //narrowing, because narrowing is exactly what must not happen here.
        if (row.IsSelected)
            return true;

        list.SelectedItem = row.DataContext;

        return true;
    }

    /// <summary>
    /// A menu item that runs one thing.
    ///
    /// Fire-and-forget by construction: a menu click has nothing to await it, and the alternative —
    /// <c>async void</c> — puts an unhandled exception on the dispatcher, where in the resident
    /// process it takes every other window with it. Each caller's own body reports its failures.
    /// </summary>
    public static MenuItem Item(string header, Func<Task> onClick)
    {
        var item = new MenuItem { Header = header };

        item.Click += (_, _) => _ = onClick();

        return item;
    }

    /// <summary>The synchronous spelling, for an item that opens something rather than running Git.</summary>
    public static MenuItem Item(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };

        item.Click += (_, _) => onClick();

        return item;
    }
}
