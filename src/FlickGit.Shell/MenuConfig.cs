using Microsoft.Win32;
using FlickGit.Shared;

namespace FlickGit.Shell;

/// <summary>
/// The three things the block needs that are not per-item: what to run, and what to call the submenu
/// and draw beside it.
///
/// All three are written by the App under the handler's own CLSID key, for the reason every string here
/// is — the DLL holds no interface text and no paths of its own. The submenu's name comes from the
/// <c>.lang</c> file in force when the menu was registered, so <c>flick language de</c> and a
/// re-register changes it without this assembly knowing a word of German.
///
/// Read once and cached. Explorer keeps this DLL loaded until it exits, so a value that changed
/// underneath would outlive the process that could act on it.
/// </summary>
internal static class MenuConfig
{
    private static string? _exe;
    private static string? _submenuLabel;
    private static string? _submenuIcon;
    private static bool _loaded;
    private static readonly object Gate = new();

    /// <summary>The full path of <c>flick.exe</c>, or empty when the key is missing.</summary>
    public static string ExePath()
    {
        Load();
        return _exe ?? string.Empty;
    }

    /// <summary>
    /// The <c>FlickGit</c> submenu's label.
    ///
    /// Falls back to the product name, which is the one string in this assembly that is the same in
    /// every language and so cannot be got wrong by not having been translated.
    /// </summary>
    public static string SubmenuLabel()
    {
        Load();
        return _submenuLabel is { Length: > 0 } label ? label : "FlickGit";
    }

    /// <summary>
    /// The <c>.ico</c> drawn beside the submenu, or null when the App wrote none.
    ///
    /// Null is a normal answer: the popup is then drawn without an icon, which is what every item
    /// whose file is missing already does.
    /// </summary>
    public static string? SubmenuIcon()
    {
        Load();
        return _submenuIcon is { Length: > 0 } icon ? icon : null;
    }

    private static void Load()
    {
        lock (Gate)
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

                _exe = key.GetValue(ShellCommandIds.ValueExe) as string;
                _submenuLabel = key.GetValue(ShellCommandIds.ValueSubmenuLabel) as string;
                _submenuIcon = key.GetValue(ShellCommandIds.ValueSubmenuIcon) as string;
            }
            catch
            {
                //A registry this process cannot read. The menu then draws nothing, because an item
                //with no executable behind it would be an entry that silently does nothing.
            }
        }
    }
}
