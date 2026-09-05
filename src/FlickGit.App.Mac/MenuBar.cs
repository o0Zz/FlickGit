using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using FlickGit.App.Localization;

namespace FlickGit.App.Mac;

/// <summary>
/// The menu bar item and its menu — the macOS counterpart of <c>TrayIconFactory</c>.
///
/// <b>Avalonia's own <see cref="TrayIcon"/></b>, which maps onto <c>NSStatusItem</c> here and onto
/// <c>Shell_NotifyIcon</c> on Windows. The port was expected to need an <c>H.NotifyIcon.Avalonia</c>
/// package to match the WPF one; no such package exists, and none is wanted — this keeps the
/// dependency list one shorter than planned.
///
/// <b>What is not here, and deliberately.</b> No placement code. The WPF factory carries thirty
/// lines correcting where <c>H.NotifyIcon</c> puts its popup across two monitors at different
/// scales; <c>NSStatusItem</c> owns its own menu placement and there is nothing to correct.
///
/// <b>No "quick commit" entry either.</b> A menu bar click has no Finder window behind it, so there
/// is no folder to resolve — the recent list is the honest way to name a repository.
/// </summary>
internal static class MenuBar
{
    /// <param name="recent">The MRU list, re-read each time the menu opens.</param>
    /// <param name="onOpenRecent">Opens the commit window on one of them.</param>
    public static TrayIcon Create(
        Func<IReadOnlyList<string>> recent,
        Action<string> onOpenRecent,
        Action onSettings,
        Action onAbout,
        Action onExit)
    {
        var recentItem = new NativeMenuItem(Strings.Get("tray.recent")) { Menu = new NativeMenu() };

        var menu = new NativeMenu
        {
            Items =
            {
                recentItem,
                new NativeMenuItemSeparator(),
                Item(Strings.Get("tray.settings"), onSettings),
                Item(Strings.Get("tray.about"), onAbout),
                new NativeMenuItemSeparator(),
                Item(Strings.Get("tray.exit"), onExit),
            },
        };

        //Rebuilt on open rather than kept in sync. The list changes with every commit and the menu is
        //looked at rarely, so reading it when asked is both simpler and always right.
        //
        //On the root rather than on the submenu: macOS builds the whole tree when the status item is
        //clicked, so this is the moment the items are needed, and hanging it off the submenu would
        //rely on a second Opening that the platform may never raise for an empty one.
        menu.Opening += (_, _) => FillRecent(recentItem.Menu!, recent(), onOpenRecent);

        return new TrayIcon
        {
            ToolTipText = Strings.Get("tray.tooltip"),
            Icon = LoadIcon(),
            Menu = menu,
            IsVisible = true,
        };
    }

    private static void FillRecent(NativeMenu menu, IReadOnlyList<string> paths, Action<string> onOpen)
    {
        menu.Items.Clear();

        if (paths.Count == 0)
        {
            menu.Items.Add(new NativeMenuItem(Strings.Get("tray.recent.none")) { IsEnabled = false });

            return;
        }

        foreach (string path in paths)
        {
            string captured = path;

            //The folder name leads, because that is what the user thinks of it as; the full path
            //follows for the case where two are called "api".
            menu.Items.Add(Item(
                $"{Path.GetFileName(captured)}    {captured}",
                () => onOpen(captured)));
        }
    }

    private static NativeMenuItem Item(string header, Action onClick)
    {
        var item = new NativeMenuItem(header);

        item.Click += (_, _) => onClick();

        return item;
    }

    /// <summary>
    /// The status item's image.
    ///
    /// <b>Failure is not fatal.</b> A menu bar item with no icon is still clickable and still holds
    /// the menu; an exception thrown here during startup would take the resident service with it,
    /// which is a far worse trade than a blank slot. The decode is Skia's, and this is the one place
    /// in the process that asks it to read an <c>.ico</c>.
    /// </summary>
    private static WindowIcon? LoadIcon()
    {
        try
        {
            return new WindowIcon(AssetLoader.Open(new Uri("avares://FlickGit/Resources/flickgit.ico")));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
