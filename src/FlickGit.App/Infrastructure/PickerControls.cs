using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlickGit.App.Infrastructure;

/// <summary>
/// Walks up the visual tree. Its one use is finding the row a pointer is over.
///
/// <b>Here rather than at the bottom of a window file.</b> It lived under
/// <c>SwitchBranchWindow.xaml.cs</c>, below the class, with a comment saying it was "for the one
/// place that has to find the row under the pointer" -- and it had three callers across two windows
/// by then.
/// </summary>
internal static class VisualTree
{
    public static T? FindAncestor<T>(this DependencyObject? from) where T : DependencyObject
    {
        for (DependencyObject? node = from; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is T match)
                return match;
        }

        return null;
    }
}

/// <summary>
/// The keyboard and pointer plumbing that every "filter box above a list" surface needs.
///
/// The Branches picker and the Tags window are the same shape -- type to filter, arrow to choose,
/// Enter to act, right-click a row for what applies to it -- and both carried their own copy of
/// these two methods, identical but for the name of the list. A third copy lived in the commit
/// window, and it was the better one: see <see cref="SelectRowUnderPointer"/>.
/// </summary>
internal static class FilterList
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
        if (list.Items.Count == 0)
            return;

        int delta = e.Key switch
        {
            Key.Down => 1,
            Key.Up => -1,
            _ => 0,
        };

        if (delta == 0)
            return;

        list.SelectedIndex = Math.Clamp(list.SelectedIndex + delta, 0, list.Items.Count - 1);
        list.ScrollIntoView(list.SelectedItem);
        e.Handled = true;
    }

    /// <summary>
    /// Selects the row the pointer is over, before a context menu is built from it.
    ///
    /// A <c>ListBox</c> does not select on right-click, so without this a menu is built for whatever
    /// was highlighted before -- which for a delete is the difference between removing the row that
    /// was clicked and removing another one, with the wrong name shown in a confirmation the user is
    /// not reading closely.
    ///
    /// <b><see cref="ItemsControl.ContainerFromElement(DependencyObject)"/> rather than a visual-tree
    /// walk</b>, which is the commit window's implementation and the one that survives a row template
    /// built from <c>Run</c>s: those are <c>ContentElement</c>s rather than <c>Visual</c>s, and
    /// <c>VisualTreeHelper.GetParent</c> throws on one.
    ///
    /// <b>A click inside a multi-selection means the selection; anywhere else means the one row under
    /// the pointer.</b> The diff pane's context menu already works that way, and the commit window's
    /// file list is <c>Extended</c> -- where a bare <c>IsSelected = true</c> would <i>add</i> the
    /// clicked row to whatever was highlighted, so a right-click on an unrelated file would revert it
    /// along with three others. Hence the clear before the select.
    ///
    /// <b>And hence the mode test, because <c>SelectedItems</c> throws in a single-selection list.</b>
    /// Not "is redundant there" -- <c>Clear</c> raises "Can only change SelectedItems collection in
    /// multiple selection modes", so on five of this method's six callers every right-click was an
    /// unhandled exception and an error dialog instead of a context menu. Only the commit window's
    /// list declares <c>Extended</c>; the four pickers and the log window's file list take the
    /// default. In <c>Single</c> mode the assignment below is the whole operation: selecting a row
    /// deselects the previous one, which is exactly what the clear was reaching for.
    /// </summary>
    /// <returns>False when the click was not on a row, so the caller can suppress the menu.</returns>
    public static bool SelectRowUnderPointer(ListBox list, object? originalSource)
    {
        if (originalSource is not DependencyObject source)
            return false;

        if (list.ContainerFromElement(source) is not ListBoxItem row)
            return false;

        if (!row.IsSelected)
        {
            if (list.SelectionMode != SelectionMode.Single)
                list.SelectedItems.Clear();

            row.IsSelected = true;
        }

        return true;
    }
}

/// <summary>
/// One context-menu item that runs an async action. Both pickers build their menus when the menu
/// opens rather than in XAML, because the labels have to name the row they would act on.
/// </summary>
internal static class Menus
{
    public static MenuItem Item(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };

        item.Click += async (_, _) => await action().ConfigureAwait(true);

        return item;
    }
}
