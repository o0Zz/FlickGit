using System.IO;
using FlickGit.Actions;
using FlickGit.App.Localization;
using FlickGit.Logging;
using Microsoft.Win32;

namespace FlickGit.App.Shell;

/// <summary>
/// Registers and removes the Explorer context-menu entries.
///
/// Every entry is a thin trigger: it launches <c>flick.exe</c> with a verb and a path, and
/// contains no logic of any kind. CLAUDE.md, "Shell Integration".
///
/// Two structural constraints shape the layout written here:
///
/// <list type="bullet">
/// <item><description><b><c>ExtendedSubCommandsKey</c> resolves relative to
/// <c>HKCR</c></b>, so the submenu definition has to live under
/// <c>HKCU\Software\Classes\FlickGit.Menu</c> rather than beside the verb.</description></item>
/// <item><description><b>Submenu entries are ordered alphabetically by key name</b>, not
/// by any position value. Hence the numeric stride (<c>10</c>, <c>20</c>, <c>30</c>):
/// inserting an entry between two others must not mean rewriting every key after
/// it.</description></item>
/// </list>
///
/// On Windows 11 these appear under "Show more options" (Shift+F10). That is a limitation
/// of registry verbs, not of this code — the Windows 11 primary menu needs
/// <c>IExplorerCommand</c> and a sparse MSIX package, which is Phase 6.
/// </summary>
public sealed class ShellIntegration(ActionCatalog catalog, ILog log)
{
    /// <summary>The one root key name this tool owns. Nothing outside it is ever touched.</summary>
    private const string RootKeyName = "FlickGit";

    /// <summary>The submenu definition, referenced by <c>ExtendedSubCommandsKey</c>.</summary>
    private const string MenuKeyName = "FlickGit.Menu";

    private const string MoreMenuKeyName = "FlickGit.Menu.More";

    private const string ClassesPath = @"Software\Classes";

    /// <summary>
    /// The parents a directory verb has to be registered under, and the argument each one
    /// substitutes for the clicked path.
    ///
    /// All three are required to cover the cases CLAUDE.md, "Repository Detection" lists:
    /// the repository root, a subdirectory, and the Explorer background while browsing
    /// inside a repository. <c>%V</c> is used everywhere — for <c>Background</c> it is the
    /// only thing that yields the current folder, and for a selected directory it is
    /// equivalent to <c>%1</c>.
    /// </summary>
    private static readonly string[] VerbParents =
    [
        @"Directory\shell",
        @"Directory\Background\shell",

        //Drive roots. Right-clicking D:\ when D: is a repository is a real case on
        //machines that keep a work drive, and "Directory" does not cover it.
        @"Drive\shell",
    ];

    /// <summary>
    /// The menu, projected from the Action Catalog.
    ///
    /// This used to be a hard-coded array here, a second one in the palette and the verb table in the
    /// CLI — three lists, three places to add an entry, three chances to disagree about its wording.
    /// Now the catalog is the definition and this only decides how to write it into the registry.
    ///
    /// <b><see cref="GitAction.RequiresRepository"/> is ignored on this surface, and has to be.</b> A
    /// registry verb is written once and shown on every folder on the machine; it cannot ask whether
    /// the clicked directory is a repository. So every menu action is written, and the ones that need
    /// a repository report that when clicked — which beats hiding entries that would have worked.
    /// Repository-aware visibility needs <c>IExplorerCommand::GetState</c>, which is Phase 6.
    ///
    /// Hidden entries are the exception: those the user turned off really are absent.
    /// </summary>
    private IReadOnlyList<GitAction> MenuActions() => catalog.For(ActionSurfaces.Menu);

    /// <summary>
    /// Writes the whole menu. Idempotent: the existing keys are removed first, so a
    /// re-apply after a settings change cannot leave a stale entry behind.
    /// </summary>
    public InstallResult Install()
    {
        string installDirectory = InstallDirectory;
        string cliPath = Path.Combine(installDirectory, "flick.exe");
        string appPath = Path.Combine(installDirectory, "FlickGit.exe");

        if (!File.Exists(cliPath))
        {
            //Registering a command line pointing at an exe that is not there would produce
            //a context menu entry that silently does nothing -- the worst possible failure
            //mode for a shell extension.
            return new InstallResult(false,
                $"flick.exe was not found in:\n\n{installDirectory}\n\n" +
                "The context menu was not modified.");
        }

        try
        {
            Uninstall();

            using RegistryKey classes = Registry.CurrentUser.CreateSubKey(ClassesPath, writable: true)
                                        ?? throw new InvalidOperationException($@"Could not open HKCU\{ClassesPath}.");

            IReadOnlyList<GitAction> actions = MenuActions();

            WriteSubmenu(classes, MenuKeyName, cliPath, appPath, actions.Where(a => !a.InMoreSubmenu), withMoreEntry: true);
            WriteSubmenu(classes, MoreMenuKeyName, cliPath, appPath, actions.Where(a => a.InMoreSubmenu), withMoreEntry: false);

            foreach (string parent in VerbParents)
                WriteRootVerb(classes, parent, appPath);

            //Read back what was written. CLAUDE.md, "Registry synchronisation" step 4:
            //"Verify by reading back; report failures in the UI." A registry write that
            //silently did nothing (policy, a locked hive) must not be reported as success.
            string? verification = Verify(cliPath);
            if (verification is not null)
                return new InstallResult(false, verification);

            log.Info($"Shell integration installed from {installDirectory}.");
            return new InstallResult(true, Strings.Get("shell.installed"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            log.Error($"Shell integration install failed: {ex.Message}");
            return new InstallResult(false, $"The context menu could not be registered:\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// Removes exactly the keys this tool created, and nothing else.
    ///
    /// CLAUDE.md: "Never enumerate or modify registry keys the tool did not create." Every
    /// path deleted here ends in a FlickGit-owned key name, so there is no code path that
    /// can walk into a neighbouring shell extension's keys.
    /// </summary>
    public InstallResult Uninstall()
    {
        try
        {
            using RegistryKey? classes = Registry.CurrentUser.OpenSubKey(ClassesPath, writable: true);
            if (classes is null)
                return new InstallResult(true, "Nothing to remove.");

            foreach (string parent in VerbParents)
            {
                using RegistryKey? shell = classes.OpenSubKey(parent, writable: true);
                shell?.DeleteSubKeyTree(RootKeyName, throwOnMissingSubKey: false);
            }

            classes.DeleteSubKeyTree(MenuKeyName, throwOnMissingSubKey: false);
            classes.DeleteSubKeyTree(MoreMenuKeyName, throwOnMissingSubKey: false);

            log.Info("Shell integration removed.");
            return new InstallResult(true, Strings.Get("shell.removed"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Error($"Shell integration removal failed: {ex.Message}");
            return new InstallResult(false, $"The context menu could not be removed:\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// Where flick.exe, FlickGit.exe and <c>icons\</c> live: beside the running module.
    ///
    /// Resolved from the module rather than the working directory, because Explorer sets the
    /// working directory to the clicked folder -- so anything relative would look for the
    /// executables inside the user's repository.
    /// </summary>
    private static string InstallDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>True when the root verb is present. Asked by `flick diag doctor` and by the tray.</summary>
    public bool IsInstalled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            $@"{ClassesPath}\{VerbParents[0]}\{RootKeyName}", writable: false);

        return key is not null;
    }

    private static void WriteRootVerb(RegistryKey classes, string parent, string appPath)
    {
        using RegistryKey verb = classes.CreateSubKey($@"{parent}\{RootKeyName}", writable: true)
                                 ?? throw new InvalidOperationException($@"Could not create {parent}\{RootKeyName}.");

        verb.SetValue("MUIVerb", Strings.Get("shell.menu.root"), RegistryValueKind.String);

        //The exe's own first icon, so the root entry is branded even before the icons\
        //directory is consulted.
        verb.SetValue("Icon", $"{appPath},0", RegistryValueKind.String);

        //Resolved relative to HKCR, i.e. HKCU\Software\Classes\FlickGit.Menu.
        verb.SetValue("ExtendedSubCommandsKey", MenuKeyName, RegistryValueKind.String);
        verb.SetValue("Position", "Top", RegistryValueKind.String);
    }

    private void WriteSubmenu(
        RegistryKey classes,
        string menuKeyName,
        string cliPath,
        string appPath,
        IEnumerable<GitAction> entries,
        bool withMoreEntry)
    {
        using RegistryKey menu = classes.CreateSubKey($@"{menuKeyName}\shell", writable: true)
                                 ?? throw new InvalidOperationException($"Could not create {menuKeyName}.");

        string iconDirectory = Path.Combine(Path.GetDirectoryName(cliPath) ?? string.Empty, "icons");

        foreach (GitAction entry in entries)
        {
            //A built-in is its own verb; a user action is reached by id through `flick run`. Both are
            //command lines flick.exe accepts, which is all a registry verb can be.
            string command = entry.Cli is { Length: > 0 } cli
                ? $"{cli} \"%V\""
                : $"run {entry.Id} \"%V\"";

            //The stride is in the key name because that is what Explorer sorts on. "120push" sorts
            //after "110switch" and before "130clone", and a new entry at 115 needs no other key
            //touched. Explorer sorts these as strings, which is why every entry within one submenu
            //has the same number of digits.
            using RegistryKey item = menu.CreateSubKey($"{entry.MenuOrder}{entry.Id}", writable: true)
                                     ?? throw new InvalidOperationException($"Could not create menu entry {entry.Id}.");

            item.SetValue("MUIVerb", entry.Label, RegistryValueKind.String);

            if (entry.IconFileName is { Length: > 0 } iconName)
            {
                string icon = Path.Combine(iconDirectory, iconName);
                if (File.Exists(icon))
                    item.SetValue("Icon", icon, RegistryValueKind.String);
            }

            using RegistryKey commandKey = item.CreateSubKey("command", writable: true)
                                           ?? throw new InvalidOperationException($"Could not create command for {entry.Id}.");

            //%V, quoted. The quotes are what make a path containing a space work, and
            //Explorer substitutes inside them.
            commandKey.SetValue(string.Empty, $"\"{cliPath}\" {command}", RegistryValueKind.String);
        }

        if (!withMoreEntry)
            return;

        //"More" is itself a submenu entry pointing at a second ExtendedSubCommandsKey.
        //Sorted last by giving it the highest stride, and it carries no command of its own.
        using RegistryKey more = menu.CreateSubKey("90more", writable: true)
                                 ?? throw new InvalidOperationException("Could not create the More submenu.");

        more.SetValue("MUIVerb", Strings.Get("shell.menu.more"), RegistryValueKind.String);
        more.SetValue("ExtendedSubCommandsKey", MoreMenuKeyName, RegistryValueKind.String);

        //A separator above More, so the two everyday entries read as a group. This is the
        //"──────" row in the CLAUDE.md layout.
        more.SetValue("CommandFlags", 0x20, RegistryValueKind.DWord);

        //Windows 11 accepts only one level of submenu under IExplorerCommand (Phase 6), so
        //More must never grow a submenu of its own. Registry verbs would allow it; the
        //catalog must stay projectable onto the stricter surface.
        _ = appPath;
    }

    private static string? Verify(string expectedCliPath)
    {
        //The commit entry, by name. It is the first built-in and the one the catalog cannot be
        //without, so it is the honest thing to read back.
        using RegistryKey? command = Registry.CurrentUser.OpenSubKey(
            $@"{ClassesPath}\{MenuKeyName}\shell\10commit\command", writable: false);

        string? value = command?.GetValue(string.Empty) as string;

        if (value is null)
            return "The context menu keys were written but could not be read back. " +
                   "Group policy or another tool may be blocking HKCU\\Software\\Classes.";

        return value.Contains(expectedCliPath, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"The context menu was registered, but points somewhere unexpected:\n\n{value}";
    }

}

/// <param name="Succeeded">False means the registry was not left in the intended state.</param>
/// <param name="Message">Shown verbatim. Never a generic string.</param>
public sealed record InstallResult(bool Succeeded, string Message);
