using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using FlickGit.App.Localization;
using FlickGit.App.Views;
using H.NotifyIcon;

namespace FlickGit.App.Tray;

/// <summary>
/// Builds the notification-area icon and its menu.
///
/// H.NotifyIcon rather than <c>System.Windows.Forms.NotifyIcon</c>: CLAUDE.md picks it
/// specifically to keep WinForms out of the process. That is not tidiness — WinForms
/// brings its own message loop and its own DPI model into a WPF app whose entire purpose
/// is to have finished starting up before the user asks for anything.
///
/// Built in code rather than declared in XAML so that the resident path stays one linear
/// sequence with no resource lookup: the tray icon is the first thing the user can see
/// after logon, and it appears before the pre-warm work that follows it.
/// </summary>
public static class TrayIconFactory
{
    /// <param name="recent">The MRU list, re-read each time the menu opens.</param>
    /// <param name="onOpenRecent">Opens the commit window on one of them.</param>
    public static TaskbarIcon Create(
        Func<IReadOnlyList<string>> recent,
        Action<string> onOpenRecent,
        Action onSettings,
        Action onAbout,
        Action onExit)
    {
        var menu = new ContextMenu();

        //No "Quick commit" entry. It launched the popup, which is gone -- and there is nothing to
        //replace it with: a tray click has no Explorer window behind it, so there is no folder to
        //resolve and the recent list below is the honest way to name a repository.
        var recentMenu = new MenuItem { Header = Strings.Get("tray.recent") };
        menu.Items.Add(recentMenu);

        menu.Items.Add(new Separator());

        menu.Items.Add(MenuItem(Strings.Get("tray.settings"), onSettings));
        menu.Items.Add(MenuItem(Strings.Get("tray.about"), onAbout));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(Strings.Get("tray.exit"), onExit));

        var icon = new TaskbarIcon
        {
            ToolTipText = Strings.Get("tray.tooltip"),
            ContextMenu = menu,

            //Left-click opens the menu, rather than committing something. There is no folder behind
            //a tray click and therefore no repository to guess, so the menu -- with the recent list
            //in it -- is the only honest thing one click can do.
            MenuActivation = H.NotifyIcon.Core.PopupActivationMode.LeftOrRightClick,
            NoLeftClickDelay = true,
        };

        //Where the menu goes, decided here rather than by H.NotifyIcon.
        //
        //The library reports the click in physical pixels, divides by a DPI factor it read from an
        //unpositioned HwndSource -- so always the *primary* monitor's -- and hands the result to
        //ContextMenu.HorizontalOffset, which WPF scales back up by the popup HWND's own DPI, i.e.
        //whichever monitor it was last shown on. Divide by one display's scale and multiply by
        //another's and the menu lands a whole scale factor away from the pointer, routinely on a
        //different screen. Nothing in the library's API corrects it, so the menu is opened by it and
        //immediately moved by us, in physical pixels, the way every other FlickGit surface is placed.
        //
        //A flag rather than a sentinel coordinate: a cursor read that failed must leave the menu
        //where the library put it, not drag it to the top-left corner of the primary display.
        bool anchored = false;
        int anchorX = 0;
        int anchorY = 0;

        //Before IsOpen, which is what makes this the right place for both halves: the anchor is
        //captured while the click is still current (a left click reaches ShowContextMenu through the
        //library's double-click timer, by which time the pointer may have moved), and the items are
        //built while the menu can still be measured without being on screen.
        icon.PreviewTrayContextMenuOpen += (_, _) =>
        {
            anchored = PopupPlacement.TryGetCursor(out anchorX, out anchorY);

            //Rebuilt on open rather than kept in sync. The list changes with every commit and the
            //menu is looked at rarely, so reading it when asked is both simpler and always right.
            recentMenu.Items.Clear();

            IReadOnlyList<string> paths = recent();

            if (paths.Count == 0)
            {
                recentMenu.Items.Add(new MenuItem
                {
                    Header = Strings.Get("tray.recent.none"),
                    IsEnabled = false,
                });
            }
            else
            {
                foreach (string path in paths)
                {
                    string captured = path;

                    recentMenu.Items.Add(MenuItem(
                        //The folder name leads, because that is what the user thinks of it as; the
                        //full path follows for the case where two are called "api".
                        $"{System.IO.Path.GetFileName(captured)}    {captured}",
                        () => onOpenRecent(captured)));
                }
            }
        };

        menu.Opened += (_, _) =>
        {
            Place();

            //Again once the layout has settled. Moving the popup onto a monitor at a different scale
            //earns it a WM_DPICHANGED, WPF re-renders the menu at the new scale, and the size the
            //flip above was decided from is then stale. AtPoint re-reads the window rect every call,
            //so this corrects it and is a no-op when both monitors are at the same scale.
            _ = menu.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Place);
        };

        void Place()
        {
            //The popup's own HWND, found the way the library finds it.
            if (anchored && menu.IsOpen && PresentationSource.FromVisual(menu) is HwndSource source)
                PopupPlacement.AtPoint(source.Handle, anchorX, anchorY);
        }

        //Loaded from the exe's own directory rather than embedded: the same .ico file is
        //what the registry hands to Explorer for the context menu, so there is exactly one
        //file to keep in step. A missing icon leaves the system default rather than
        //throwing -- no icon is a cosmetic problem, a crash on startup is not.
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "flickgit.ico");

        if (File.Exists(iconPath))
            icon.IconSource = new System.Windows.Media.Imaging.BitmapImage(new Uri(iconPath, UriKind.Absolute));

        icon.ForceCreate();
        return icon;
    }

    private static MenuItem MenuItem(string header, Action action, bool isDefault = false)
    {
        var item = new MenuItem { Header = header };

        if (isDefault)
            item.FontWeight = FontWeights.SemiBold;

        item.Click += (_, _) => action();
        return item;
    }
}
