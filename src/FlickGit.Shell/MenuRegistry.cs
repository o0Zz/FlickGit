using Microsoft.Win32;
using FlickGit.Shared;

namespace FlickGit.Shell;

/// <summary>
/// One entry in the FlickGit block.
/// </summary>
/// <param name="Label">Already localised. The DLL holds no interface text of its own.</param>
/// <param name="Verb">What to pass <c>flick.exe</c>. A built-in's verb, or <c>run &lt;id&gt;</c>.</param>
/// <param name="Icon">Full path to an <c>.ico</c>, or null.</param>
/// <param name="ShowBranch">Append the checked-out branch to the label.</param>
/// <param name="NeedsRepository">Omit the item entirely outside a repository.</param>
/// <param name="InSubmenu">Goes under the <c>FlickGit</c> popup rather than at the top level.</param>
/// <param name="OnFiles">Drawn when a file was clicked.</param>
/// <param name="OnFolders">Drawn when a folder, a drive or a folder background was clicked.</param>
/// <param name="OnClickedFolders">
/// Drawn when a folder was pointed at -- not its own background, not a drive, and not the repository
/// root. Narrower than <paramref name="OnFolders"/> and not implied by it: an item carrying only this
/// one acts on everything below the folder, which is why the click has to have named one.
/// </param>
internal sealed record MenuItem(
    string Label,
    string Verb,
    string? Icon,
    bool ShowBranch,
    bool NeedsRepository,
    bool InSubmenu,
    bool OnFiles,
    bool OnFolders,
    bool OnClickedFolders);

/// <summary>
/// The menu, as the App wrote it into the handler's own CLSID key: what to run, what to call the
/// submenu and draw beside it, and every item in draw order.
///
/// <code>
/// HKCU\Software\Classes\CLSID\{handler}          Exe, SubmenuLabel, SubmenuIcon
///                                     \Items\0010    Label, Verb, Icon, ShowBranch, ...
///                                     \Items\0020
/// </code>
///
/// Every string here is written by the App, because the DLL holds no interface text and no paths of
/// its own. The submenu's name comes from the <c>.lang</c> file in force when the menu was
/// registered, so <c>flick language de</c> plus a re-register changes it without this assembly
/// knowing a word of German.
///
/// <b>The whole block is one COM object, so this is one class and one read.</b> It was two —
/// <c>MenuConfig</c> for the three scalars and <c>MenuItems</c> for the list — which was the shape
/// of the <c>IExplorerCommand</c> handlers this replaced: one CLSID per verb, each reading its own
/// key, because Explorer asked about each verb separately. A context-menu handler is asked once and
/// hands back everything, so two caches, two locks and two swallow-all handlers over one key were
/// duplicated work that opened the same key twice on the first right-click — inside
/// <c>explorer.exe</c>, which is the one process where doing as little as possible is the point.
///
/// Read once and cached for the life of the process. These values change only when the menu is
/// re-registered, and Explorer keeps this DLL loaded until it exits — so a value that changed
/// underneath would outlive the process that could act on it anyway.
/// </summary>
internal static class MenuRegistry
{
    private static readonly object Gate = new();
    private static bool _loaded;

    private static string _exe = string.Empty;
    private static string? _submenuLabel;
    private static string? _submenuIcon;
    private static MenuItem[] _items = [];

    /// <summary>The full path of <c>flick.exe</c>, or empty when the key is missing.</summary>
    public static string ExePath()
    {
        lock (Gate)
        {
            Load();
            return _exe;
        }
    }

    /// <summary>
    /// The <c>FlickGit</c> submenu's label.
    ///
    /// Falls back to the product name, which is the one string in this assembly that is the same in
    /// every language and so cannot be got wrong by not having been translated.
    /// </summary>
    public static string SubmenuLabel()
    {
        lock (Gate)
        {
            Load();
            return _submenuLabel is { Length: > 0 } label ? label : "FlickGit";
        }
    }

    /// <summary>
    /// The <c>.ico</c> drawn beside the submenu, or null when the App wrote none.
    ///
    /// Null is a normal answer: the popup is then drawn without an icon, which is what every item
    /// whose file is missing already does.
    /// </summary>
    public static string? SubmenuIcon()
    {
        lock (Gate)
        {
            Load();
            return _submenuIcon is { Length: > 0 } icon ? icon : null;
        }
    }

    /// <summary>
    /// Every item, in draw order. Empty when the key is missing, which draws no block at all rather
    /// than a separator with nothing between it.
    /// </summary>
    public static MenuItem[] All()
    {
        lock (Gate)
        {
            Load();
            return _items;
        }
    }

    /// <summary>
    /// The single read. Called with <see cref="Gate"/> held.
    ///
    /// The CLSID key is opened once and the <c>Items</c> subkey comes off the handle it already has,
    /// so a right-click costs one open of that path rather than two.
    /// </summary>
    private static void Load()
    {
        if (_loaded)
            return;

        _loaded = true;

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\CLSID\{ShellCommandIds.MenuHandlerClsid}");

            if (key is null)
                return;

            _exe = key.GetValue(ShellCommandIds.ValueExe) as string ?? string.Empty;
            _submenuLabel = key.GetValue(ShellCommandIds.ValueSubmenuLabel) as string;
            _submenuIcon = key.GetValue(ShellCommandIds.ValueSubmenuIcon) as string;

            _items = LoadItems(key);
        }
        catch
        {
            //A registry this process cannot read. No menu, rather than a broken one: an item with no
            //executable behind it would be an entry that silently does nothing.
        }
    }

    private static MenuItem[] LoadItems(RegistryKey key)
    {
        using RegistryKey? items = key.OpenSubKey(ShellCommandIds.ItemsKeyName);

        if (items is null)
            return [];

        var loaded = new List<MenuItem>();

        //Ordinal, so 0010 sorts before 0020 and before 0100. GetSubKeyNames does not promise an
        //order, so it is imposed here rather than assumed.
        foreach (string name in items.GetSubKeyNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            using RegistryKey? item = items.OpenSubKey(name);

            if (item is null)
                continue;

            string? label = item.GetValue(ShellCommandIds.ValueLabel) as string;
            string? verb = item.GetValue(ShellCommandIds.ValueVerb) as string;

            //An item with no label would draw as an empty row; one with no verb would do nothing
            //when clicked. Either way there is nothing worth showing.
            if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(verb))
                continue;

            loaded.Add(new MenuItem(
                label,
                verb,
                item.GetValue(ShellCommandIds.ValueIcon) as string is { Length: > 0 } icon ? icon : null,
                IsSet(item, ShellCommandIds.ValueShowBranch),
                IsSet(item, ShellCommandIds.ValueNeedsRepository),
                IsSet(item, ShellCommandIds.ValueInSubmenu),
                IsSet(item, ShellCommandIds.ValueOnFiles),
                IsSet(item, ShellCommandIds.ValueOnFolders),
                IsSet(item, ShellCommandIds.ValueOnClickedFolders)));
        }

        return [.. loaded];
    }

    private static bool IsSet(RegistryKey key, string name) => key.GetValue(name) as string == "1";
}
