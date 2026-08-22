using System.IO;
using System.Windows;
using System.Windows.Controls;
using FlickGit.App.Localization;
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
        Action onQuickCommit,
        Func<IReadOnlyList<string>> recent,
        Action<string> onOpenRecent,
        Action onSettings,
        Action onAbout,
        Action onExit)
    {
        var menu = new ContextMenu();

        menu.Items.Add(MenuItem(Strings.Get("tray.commit"), onQuickCommit, isDefault: true));

        var recentMenu = new MenuItem { Header = Strings.Get("tray.recent") };
        menu.Items.Add(recentMenu);

        menu.Items.Add(new Separator());

        menu.Items.Add(MenuItem(Strings.Get("tray.settings"), onSettings));
        menu.Items.Add(MenuItem(Strings.Get("tray.about"), onAbout));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(Strings.Get("tray.exit"), onExit));

        //Rebuilt on open rather than kept in sync. The list changes with every commit and the menu
        //is looked at rarely, so reading it when asked is both simpler and always right.
        menu.Opened += (_, _) =>
        {
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

        var icon = new TaskbarIcon
        {
            ToolTipText = Strings.Get("tray.tooltip"),
            ContextMenu = menu,

            //Left-click opens the same action the menu's default entry does, so the
            //fast path is one click from the tray as well as from Explorer.
            LeftClickCommand = new Infrastructure.RelayCommand(onQuickCommit),
            NoLeftClickDelay = true,
        };

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
