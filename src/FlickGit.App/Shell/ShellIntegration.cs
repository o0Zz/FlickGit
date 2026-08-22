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
/// The layout is TortoiseGit's, and for TortoiseGit's reason: the operations performed all day are
/// <b>root</b> verbs in the folder's context menu, and everything else is one submenu below them. A
/// submenu costs a hover and a second aim, so putting Commit behind one taxes the most frequent
/// action in the product in order to tidy up the least frequent ones.
///
/// Three structural constraints shape what is written here:
///
/// <list type="bullet">
/// <item><description><b><c>ExtendedSubCommandsKey</c> resolves relative to
/// <c>HKCR</c></b>, so the submenu definition has to live under
/// <c>HKCU\Software\Classes\FlickGit.Menu</c> rather than beside the verb.</description></item>
/// <item><description><b>Entries are ordered alphabetically by key name</b> — the root verbs as
/// much as the submenu items — not by any position value. Hence the numeric stride (<c>10</c>,
/// <c>20</c>, <c>30</c>): inserting an entry between two others must not mean rewriting every key
/// after it.</description></item>
/// <item><description><b>Every key this tool creates is named <c>FlickGit.*</c></b>, which is what
/// lets an uninstall find its own root verbs — now several keys rather than one — without ever
/// reaching a neighbouring shell extension's.</description></item>
/// </list>
///
/// On Windows 11 these appear under "Show more options" (Shift+F10). That is a limitation
/// of registry verbs, not of this code — the Windows 11 primary menu needs
/// <c>IExplorerCommand</c> and a sparse MSIX package, which is Phase 6.
/// </summary>
public sealed class ShellIntegration(ActionCatalog catalog, ILog log)
{
    /// <summary>
    /// The prefix on every key this tool creates under a <c>shell</c> parent.
    ///
    /// Load-bearing for <see cref="Uninstall"/>: the root entries are several keys now, so removal
    /// finds them by this prefix. Nothing else on a Windows machine uses it, so "keys the tool did
    /// not create" stay outside the filter by construction.
    /// </summary>
    private const string KeyPrefix = "FlickGit.";

    /// <summary>The submenu definition, referenced by <c>ExtendedSubCommandsKey</c>.</summary>
    private const string MenuKeyName = "FlickGit.Menu";

    /// <summary>
    /// The submenu's own key name under each parent.
    ///
    /// <c>zz</c> rather than a number, so it sorts after every root verb whatever stride the catalog
    /// gave them — including a user action that fell back to the default order of 900.
    /// </summary>
    private const string MenuVerbKeyName = KeyPrefix + "zz.menu";

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
    /// Class-level keys this tool creates, deleted by name because enumerating
    /// <c>Software\Classes</c> to find them would mean walking every file association on the machine.
    ///
    /// <c>FlickGit.Menu.More</c> is no longer written — the submenu <i>is</i> the former More list,
    /// now that the everyday actions are root verbs — and is named here only so that an install
    /// which created it does not leave it behind.
    /// </summary>
    private static readonly string[] ClassKeyNames = [MenuKeyName, "FlickGit.Menu.More"];

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

            GitAction[] rootActions = [.. actions.Where(a => !a.InMoreSubmenu)];
            GitAction[] submenuActions = [.. actions.Where(a => a.InMoreSubmenu)];

            WriteSubmenu(classes, cliPath, submenuActions);

            foreach (string parent in VerbParents)
            {
                foreach (GitAction action in rootActions)
                    WriteRootVerb(classes, parent, cliPath, action);

                //Only when there is something behind it. CLAUDE.md: "Do not show a submenu with a
                //single item" -- an empty one is worse still.
                if (submenuActions.Length > 0)
                    WriteMenuVerb(classes, parent, appPath);
            }

            //Read back what was written. CLAUDE.md, "Registry synchronisation" step 4:
            //"Verify by reading back; report failures in the UI." A registry write that
            //silently did nothing (policy, a locked hive) must not be reported as success.
            string? verification = Verify(cliPath, rootActions, submenuActions);
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
    /// CLAUDE.md: "Never enumerate or modify registry keys the tool did not create." The root
    /// entries are several keys now, so they are found by enumeration rather than by name — but the
    /// filter is <see cref="KeyPrefix"/>, so nothing without FlickGit's own name in it is reachable
    /// from here.
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
                if (shell is null)
                    continue;

                foreach (string name in OwnedKeyNames(shell))
                    shell.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
            }

            foreach (string name in ClassKeyNames)
                classes.DeleteSubKeyTree(name, throwOnMissingSubKey: false);

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
    /// FlickGit's own keys under one <c>shell</c> parent.
    ///
    /// The bare name "FlickGit" matches as well as the prefix, so the single submenu verb written by
    /// the layout this replaced is removed rather than left sitting beside the new root entries.
    /// </summary>
    private static string[] OwnedKeyNames(RegistryKey shell) =>
        [.. shell.GetSubKeyNames()
            .Where(n => n.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase)
                        || n.Equals("FlickGit", StringComparison.OrdinalIgnoreCase))];

    /// <summary>
    /// Where flick.exe, FlickGit.exe and <c>icons\</c> live: beside the running module.
    ///
    /// Resolved from the module rather than the working directory, because Explorer sets the
    /// working directory to the clicked folder -- so anything relative would look for the
    /// executables inside the user's repository.
    /// </summary>
    private static string InstallDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>True when any FlickGit entry is present. Asked by `flick diag doctor` and by the tray.</summary>
    public bool IsInstalled()
    {
        using RegistryKey? shell = Registry.CurrentUser.OpenSubKey(
            $@"{ClassesPath}\{VerbParents[0]}", writable: false);

        return shell is not null && OwnedKeyNames(shell).Length > 0;
    }

    /// <summary>
    /// One root-level entry: a verb of its own in the folder's context menu rather than a submenu
    /// item. This is the whole point of the layout — Commit and Pull are one click from the
    /// right-click, the way they are in TortoiseGit.
    /// </summary>
    private static void WriteRootVerb(RegistryKey classes, string parent, string cliPath, GitAction action)
    {
        using RegistryKey verb = classes.CreateSubKey($@"{parent}\{RootKeyName(action)}", writable: true)
                                 ?? throw new InvalidOperationException($"Could not create the {action.Id} verb.");

        verb.SetValue("MUIVerb", action.Label, RegistryValueKind.String);
        SetIcon(verb, cliPath, action.IconFileName);

        //Bottom, so the entries land with the other tools' verbs at the end of the menu instead of
        //above Explorer's own "Open". That is where the hand already goes looking for them.
        verb.SetValue("Position", "Bottom", RegistryValueKind.String);

        using RegistryKey commandKey = verb.CreateSubKey("command", writable: true)
                                       ?? throw new InvalidOperationException($"Could not create command for {action.Id}.");

        commandKey.SetValue(string.Empty, CommandLine(cliPath, action), RegistryValueKind.String);
    }

    /// <summary>The "FlickGit" submenu, carrying everything not worth a root entry.</summary>
    private static void WriteMenuVerb(RegistryKey classes, string parent, string appPath)
    {
        using RegistryKey verb = classes.CreateSubKey($@"{parent}\{MenuVerbKeyName}", writable: true)
                                 ?? throw new InvalidOperationException($@"Could not create {parent}\{MenuVerbKeyName}.");

        verb.SetValue("MUIVerb", Strings.Get("shell.menu.root"), RegistryValueKind.String);

        //The exe's own first icon, so the submenu is branded even before the icons\
        //directory is consulted.
        verb.SetValue("Icon", $"{appPath},0", RegistryValueKind.String);

        //Resolved relative to HKCR, i.e. HKCU\Software\Classes\FlickGit.Menu.
        verb.SetValue("ExtendedSubCommandsKey", MenuKeyName, RegistryValueKind.String);
        verb.SetValue("Position", "Bottom", RegistryValueKind.String);
    }

    private static void WriteSubmenu(RegistryKey classes, string cliPath, IReadOnlyList<GitAction> entries)
    {
        if (entries.Count == 0)
            return;

        using RegistryKey menu = classes.CreateSubKey($@"{MenuKeyName}\shell", writable: true)
                                 ?? throw new InvalidOperationException($"Could not create {MenuKeyName}.");

        foreach (GitAction entry in entries)
        {
            //The stride is in the key name because that is what Explorer sorts on. "120push" sorts
            //after "110switch" and before "130clone", and a new entry at 115 needs no other key
            //touched. Explorer sorts these as strings, which is why every entry within one submenu
            //has the same number of digits.
            using RegistryKey item = menu.CreateSubKey($"{entry.MenuOrder}{entry.Id}", writable: true)
                                     ?? throw new InvalidOperationException($"Could not create menu entry {entry.Id}.");

            item.SetValue("MUIVerb", entry.Label, RegistryValueKind.String);
            SetIcon(item, cliPath, entry.IconFileName);

            using RegistryKey commandKey = item.CreateSubKey("command", writable: true)
                                           ?? throw new InvalidOperationException($"Could not create command for {entry.Id}.");

            commandKey.SetValue(string.Empty, CommandLine(cliPath, entry), RegistryValueKind.String);
        }
    }

    /// <summary>
    /// The key name a root entry gets, strided like the submenu's because top-level verbs are
    /// enumerated alphabetically too.
    /// </summary>
    private static string RootKeyName(GitAction action) => $"{KeyPrefix}{action.MenuOrder}.{action.Id}";

    /// <summary>
    /// What Explorer runs. A built-in is its own verb; a user action is reached by id through
    /// <c>flick run</c>. Both are command lines flick.exe accepts, which is all a registry verb can
    /// be.
    ///
    /// <c>%V</c>, quoted. The quotes are what make a path containing a space work, and Explorer
    /// substitutes inside them.
    /// </summary>
    private static string CommandLine(string cliPath, GitAction action)
    {
        string verb = action.Cli is { Length: > 0 } cli ? cli : $"run {action.Id}";
        return $"\"{cliPath}\" {verb} \"%V\"";
    }

    private static void SetIcon(RegistryKey key, string cliPath, string? iconFileName)
    {
        if (iconFileName is not { Length: > 0 } name)
            return;

        string icon = Path.Combine(Path.GetDirectoryName(cliPath) ?? string.Empty, "icons", name);

        if (File.Exists(icon))
            key.SetValue("Icon", icon, RegistryValueKind.String);
    }

    /// <summary>
    /// Reads one command back out of the registry.
    ///
    /// The entry checked is whichever the catalog put first, rather than "commit" by name: the
    /// catalog can hide or move any built-in, and verifying a key this run was never going to write
    /// would report a working install as broken.
    /// </summary>
    private static string? Verify(
        string expectedCliPath,
        IReadOnlyList<GitAction> rootActions,
        IReadOnlyList<GitAction> submenuActions)
    {
        string path;

        if (rootActions.Count > 0)
        {
            path = $@"{ClassesPath}\{VerbParents[0]}\{RootKeyName(rootActions[0])}\command";
        }
        else if (submenuActions.Count > 0)
        {
            GitAction first = submenuActions[0];
            path = $@"{ClassesPath}\{MenuKeyName}\shell\{first.MenuOrder}{first.Id}\command";
        }
        else
        {
            return "Every menu action is hidden, so there was nothing to register.";
        }

        using RegistryKey? command = Registry.CurrentUser.OpenSubKey(path, writable: false);

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
