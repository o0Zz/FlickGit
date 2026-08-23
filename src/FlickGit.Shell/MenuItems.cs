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
internal sealed record MenuItem(
    string Label,
    string Verb,
    string? Icon,
    bool ShowBranch,
    bool NeedsRepository,
    bool InSubmenu);

/// <summary>
/// The menu, as the App wrote it into the registry.
///
/// <b>The whole block is now one COM object, so the DLL needs the whole list.</b> The
/// <c>IExplorerCommand</c> handlers it replaces were one CLSID per verb, each reading its own key —
/// which worked because Explorer asked about each verb separately. A context-menu handler is asked
/// once and hands back everything, so the items are enumerated from one place.
///
/// Ordered subkeys under the handler's own CLSID, named with the catalog's menu order so the
/// registry enumerates them in the order they are drawn:
///
/// <code>
/// HKCU\Software\Classes\CLSID\{handler}\Items\0010    Label, Verb, Icon, ShowBranch, ...
///                                     \Items\0020
/// </code>
///
/// Read once and cached for the life of the process. These values change only when the menu is
/// re-registered, and Explorer keeps this DLL loaded until it exits — so a stale entry would outlive
/// the process that could act on it anyway.
/// </summary>
internal static class MenuItems
{
    private static MenuItem[]? _items;
    private static readonly object Gate = new();

    /// <summary>
    /// Every item, in draw order. Empty when the key is missing, which draws no block at all rather
    /// than a separator with nothing between it.
    /// </summary>
    public static MenuItem[] All()
    {
        lock (Gate)
        {
            return _items ??= Load();
        }
    }

    private static MenuItem[] Load()
    {
        try
        {
            using RegistryKey? items = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\CLSID\{ShellCommandIds.MenuHandlerClsid}\Items");

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
                    IsSet(item, ShellCommandIds.ValueInSubmenu)));
            }

            return [.. loaded];
        }
        catch
        {
            //A registry this process cannot read. No menu, rather than a broken one.
            return [];
        }
    }

    private static bool IsSet(RegistryKey key, string name) => key.GetValue(name) as string == "1";
}
