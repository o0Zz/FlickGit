using System.IO;
using FlickGit.Actions;
using FlickGit.Shared;
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
/// <item><description><b>The block is bracketed by separators.</b> The first root entry carries
/// <c>ECF_SEPARATORBEFORE</c> and the last carries <c>ECF_SEPARATORAFTER</c>, which is what gives
/// FlickGit a group of its own rather than leaving it interleaved with every other tool's verbs.
/// This is the declarative form of what TortoiseGit does by hand: its <c>QueryContextMenu</c> calls
/// <c>InsertMenu(hMenu, indexMenu++, MF_SEPARATOR | MF_BYPOSITION, 0, nullptr)</c> either side of its
/// own items and nothing else, because an <c>IContextMenu</c> handler is given a raw <c>HMENU</c> and
/// has to draw the bars itself. A verb can ask for them instead — as a <c>CommandFlags</c>
/// <c>REG_DWORD</c>, and through <c>IExplorerCommand::GetFlags</c> when the handler is
/// registered.</description></item>
/// <item><description><b>No <c>Position</c> value is written, and that is the placement.</b> These
/// entries used to set <c>Position = "Bottom"</c>, on the stated grounds that it put them "with the
/// other tools' verbs at the end of the menu" and that it was "where TortoiseGit is". Both halves
/// were wrong. No other tool sets it — <c>git_gui</c>, <c>git_shell</c>, <c>cmd</c>,
/// <c>Powershell</c>, <c>WSL</c> and <c>vscode</c> all register a plain verb with no
/// <c>Position</c>, and they land in the block just above <c>New</c>; <c>Bottom</c> is what moved
/// FlickGit <i>past</i> <c>New</c> down beside <c>Properties</c>. And TortoiseGit is not a static
/// verb at all: it registers an <c>IContextMenu</c> handler under
/// <c>Directory\Background\shellex\ContextMenuHandlers</c>, which is handed the menu and inserts
/// into it at a chosen index. The default placement is therefore the correct one, and the risk the
/// value was guarding against — appearing above Explorer's own <c>Open</c> — does not exist, because
/// <c>Open</c> is the default verb and is drawn first whatever else is registered.</description></item>
/// </list>
///
/// On Windows 11 these appear under "Show more options" (Shift+F10). That is a limitation
/// of registry verbs, not of this code — the Windows 11 <i>primary</i> menu needs a sparse MSIX
/// package, which is still Phase 6.
///
/// <b>The two root verbs also get an <c>IExplorerCommand</c> handler</b>, which is what puts the
/// branch name in the Commit entry and hides both entries outside a repository. That does <i>not</i>
/// need MSIX or a signature: <c>ExplorerCommandHandler</c> on a verb key is honoured in the classic
/// menu with an ordinary per-user COM registration. What it does need is
/// <c>FlickGit.Shell.dll</c> — a Native AOT DLL that Explorer loads into itself — so
/// <see cref="ShellHandlerAvailable"/> checks the file is really there and the verbs stay plain
/// static entries when it is not.
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
    /// The classes the context-menu handler is registered under.
    ///
    /// Derived from <see cref="VerbParents"/> until files arrived, and now its own list because the
    /// two no longer line up: <c>*</c> takes the handler but no static verb. A static verb cannot
    /// hide itself, so registering one there would put a FlickGit submenu on every file on the
    /// machine, repository or not — whereas the handler is asked each time and answers with nothing.
    ///
    /// The cost of <c>*</c> is that Explorer loads this DLL on every file right-click. That is the
    /// price of a file entry at all: there is no per-repository class to register under, and
    /// enumerating extensions is not a smaller version of the same thing.
    /// </summary>
    private static readonly string[] HandlerOwners =
    [
        "Directory",
        @"Directory\Background",
        "Drive",

        //All files. The handler draws only what the click asks for -- see ValueOnFiles.
        "*",
    ];

    /// <summary>Where a COM class registers itself, under <c>Software\Classes</c>.</summary>
    private const string ClsidPath = "CLSID";

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

            //Sorted the way Explorer will show them -- alphabetically by key name -- because which
            //entry is first and which is last is what decides where the separators go.
            GitAction[] rootActions =
            [
                .. actions.Where(a => !a.InMoreSubmenu)
                    .OrderBy(RootKeyName, StringComparer.OrdinalIgnoreCase),
            ];

            GitAction[] submenuActions = [.. actions.Where(a => a.InMoreSubmenu)];

            string? handlerDll = ShellHandlerAvailable(installDirectory);

            //One or the other, never both: the same entries from a handler and from static verbs is
            //the menu twice over.
            if (handlerDll is not null)
            {
                WriteContextMenuHandler(classes, cliPath, handlerDll, rootActions, submenuActions);
            }
            else
            {
                log.Info($"{ShellCommandIds.DllFileName} is not present, so the menu falls back to static verbs.");

                WriteSubmenu(classes, cliPath, submenuActions);

                foreach (string parent in VerbParents)
                {
                    foreach (GitAction action in rootActions)
                        WriteRootVerb(classes, parent, cliPath, action, SeparatorFor(action, rootActions, submenuActions.Length > 0));

                    //Only when there is something behind it. CLAUDE.md: "Do not show a submenu with a
                    //single item" -- an empty one is worse still.
                    //
                    //The submenu verb sorts after every root entry, so when it exists it is the bottom
                    //of the block and carries the closing separator.
                    if (submenuActions.Length > 0)
                        WriteMenuVerb(classes, parent, appPath, ShellCommandIds.SeparatorAfter);
                }
            }

            //Read back what was written. CLAUDE.md, "Registry synchronisation" step 4:
            //"Verify by reading back; report failures in the UI." A registry write that
            //silently did nothing (policy, a locked hive) must not be reported as success.
            //
            //Only for the static-verb layout: the handler writes no verbs to read back, and what
            //would need verifying there is whether Explorer can create the class -- which cannot be
            //answered from here without loading the DLL into this process.
            if (handlerDll is null)
            {
                string? verification = Verify(cliPath, rootActions, submenuActions);

                if (verification is not null)
                    return new InstallResult(false, verification);
            }

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

            //The handler registration, under each class it was written to. The same list the install
            //uses, or the one that is only in the other list is the one that leaks.
            foreach (string owner in HandlerOwners)
            {
                using RegistryKey? handlers = classes.OpenSubKey(
                    $@"{owner}\{ShellCommandIds.ContextMenuHandlersPath}", writable: true);

                handlers?.DeleteSubKeyTree(ShellCommandIds.HandlerKeyName, throwOnMissingSubKey: false);
            }

            //By name, from the compiled-in list -- never by enumerating CLSID, which is every COM
            //class on the machine. This is why ShellCommandIds calls its GUIDs permanent: a renumbered
            //one is a key nothing can find to delete, and RetiredClsids exists so the per-verb
            //handlers an earlier version wrote are still removable.
            using (RegistryKey? clsids = classes.OpenSubKey(ClsidPath, writable: true))
            {
                clsids?.DeleteSubKeyTree(ShellCommandIds.MenuHandlerClsid, throwOnMissingSubKey: false);

                foreach (string retired in ShellCommandIds.RetiredClsids)
                    clsids?.DeleteSubKeyTree(retired, throwOnMissingSubKey: false);
            }

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
    /// <param name="withHandler">
    /// True to add <c>ExplorerCommandHandler</c>, so this entry's label and visibility come from the
    /// shell DLL rather than from the static values written here.
    ///
    /// <b>The static values are written either way.</b> They are not redundant: <c>MUIVerb</c> is
    /// what the entry falls back to if the handler cannot be created, and the <c>command</c> subkey
    /// is what runs if <c>Invoke</c> is never reached. A verb that consists only of a handler is a
    /// verb that vanishes when the DLL does.
    /// </param>
    /// <param name="separators">
    /// <c>ECF_SEPARATORBEFORE</c>, <c>ECF_SEPARATORAFTER</c>, or zero. Written as <c>CommandFlags</c>,
    /// which is the registry half of the pair — the handler reports the same value from
    /// <c>GetFlags</c>, because it is not documented which of the two the classic menu consults when
    /// both are present, and they cannot disagree if they come from one decision.
    /// </param>
    private static void WriteRootVerb(RegistryKey classes, string parent, string cliPath, GitAction action, uint separators)
    {
        using RegistryKey verb = classes.CreateSubKey($@"{parent}\{RootKeyName(action)}", writable: true)
                                 ?? throw new InvalidOperationException($"Could not create the {action.Id} verb.");

        verb.SetValue("MUIVerb", action.Label, RegistryValueKind.String);
        SetIcon(verb, cliPath, action.IconFileName);

        //No Position value: the default placement is what puts this just above New. See the class
        //remarks -- "Bottom" is what used to push it past New, down beside Properties.
        if (separators != 0)
            verb.SetValue("CommandFlags", unchecked((int)separators), RegistryValueKind.DWord);

        using RegistryKey commandKey = verb.CreateSubKey("command", writable: true)
                                       ?? throw new InvalidOperationException($"Could not create command for {action.Id}.");

        commandKey.SetValue(string.Empty, CommandLine(cliPath, action), RegistryValueKind.String);
    }

    /// <summary>
    /// The full path of the shell DLL if it is there to be registered, or null.
    ///
    /// Null is the ordinary case for a `dotnet build` working tree: Native AOT only runs on publish,
    /// so the DLL beside the executables exists only in a real install. Registering a handler whose
    /// CLSID has no DLL behind it would leave Explorer unable to create the object, and it drops the
    /// entry when that happens — turning two working menu items into none.
    /// </summary>
    private static string? ShellHandlerAvailable(string installDirectory)
    {
        string path = Path.Combine(installDirectory, ShellCommandIds.DllFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Registers the context-menu handler, and writes the whole menu into its CLSID key.
    ///
    /// <b>This is the placement, and nothing else was able to provide it.</b> A static verb reaches
    /// <c>Top</c>, the default, or <c>Bottom</c>; Explorer draws the static-verb block above the
    /// shell-extension block, which it draws above <c>New</c>. So the default left the entries up
    /// among <c>Open with Code</c> and <c>Git GUI Here</c>, and <c>Bottom</c> pushed them past
    /// <c>New</c> down beside <c>Properties</c>. The slot every Git client occupies belongs to
    /// handlers, and <c>shellex\ContextMenuHandlers</c> is how one is registered.
    ///
    /// The DLL holds no interface text: every label written here is already localised from the
    /// <c>.lang</c> file in force, so <c>flick language de</c> plus a re-register changes the menu
    /// without that assembly knowing a word of German.
    /// </summary>
    private void WriteContextMenuHandler(
        RegistryKey classes,
        string cliPath,
        string dllPath,
        IReadOnlyList<GitAction> rootActions,
        IReadOnlyList<GitAction> submenuActions)
    {
        using RegistryKey clsid = classes.CreateSubKey($@"{ClsidPath}\{ShellCommandIds.MenuHandlerClsid}", writable: true)
                                  ?? throw new InvalidOperationException("Could not register the context-menu handler.");

        clsid.SetValue(string.Empty, "FlickGit context menu", RegistryValueKind.String);
        clsid.SetValue(ShellCommandIds.ValueExe, cliPath, RegistryValueKind.String);
        clsid.SetValue(ShellCommandIds.ValueSubmenuLabel, Strings.Get("shell.menu.root"), RegistryValueKind.String);

        using (RegistryKey server = clsid.CreateSubKey("InprocServer32", writable: true)
                                   ?? throw new InvalidOperationException("Could not register the handler's server."))
        {
            server.SetValue(string.Empty, dllPath, RegistryValueKind.String);

            //Apartment: what a shell extension is called on. The shell then marshals for us rather
            //than expecting this code to be free-threaded.
            server.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
        }

        //Rewritten from scratch, so an action the user has since hidden does not survive as an item
        //nothing removed.
        clsid.DeleteSubKeyTree(ShellCommandIds.ItemsKeyName, throwOnMissingSubKey: false);

        using RegistryKey items = clsid.CreateSubKey(ShellCommandIds.ItemsKeyName, writable: true)
                                  ?? throw new InvalidOperationException("Could not write the menu items.");

        int order = 0;

        foreach (GitAction action in rootActions)
            WriteItem(items, ref order, cliPath, action, inSubmenu: false);

        foreach (GitAction action in submenuActions)
            WriteItem(items, ref order, cliPath, action, inSubmenu: true);

        //The file entries, which are always in the submenu: a file's FlickGit block is one hover
        //deep, and there is no everyday file action worth a root entry the way Commit is.
        foreach (GitAction action in catalog.For(ActionSurfaces.File))
            WriteItem(items, ref order, cliPath, action, inSubmenu: true);

        foreach (string owner in HandlerOwners)
        {
            using RegistryKey handler = classes.CreateSubKey(
                                            $@"{owner}\{ShellCommandIds.ContextMenuHandlersPath}\{ShellCommandIds.HandlerKeyName}",
                                            writable: true)
                                        ?? throw new InvalidOperationException($"Could not register the handler under {owner}.");

            handler.SetValue(string.Empty, ShellCommandIds.MenuHandlerClsid, RegistryValueKind.String);
        }

        log.Info($"Registered the context-menu handler from {dllPath} with {order} item(s).");
    }

    /// <summary>One menu entry, as a numbered subkey so the registry enumerates them in draw order.</summary>
    private static void WriteItem(RegistryKey items, ref int order, string cliPath, GitAction action, bool inSubmenu)
    {
        order += 10;

        using RegistryKey item = items.CreateSubKey(order.ToString("D4"), writable: true)
                                 ?? throw new InvalidOperationException($"Could not write the {action.Id} item.");

        item.SetValue(ShellCommandIds.ValueLabel, action.Label, RegistryValueKind.String);
        item.SetValue(
            ShellCommandIds.ValueVerb,
            action.Cli is { Length: > 0 } cli ? cli : $"run {action.Id}",
            RegistryValueKind.String);

        item.SetValue(ShellCommandIds.ValueNeedsRepository, action.RequiresRepository ? "1" : "0", RegistryValueKind.String);
        item.SetValue(ShellCommandIds.ValueInSubmenu, inSubmenu ? "1" : "0", RegistryValueKind.String);

        //Which click this item answers. The handler is registered on files and on folders alike, so
        //without this every folder action would appear on a file -- and Blame on a directory.
        item.SetValue(
            ShellCommandIds.ValueOnFiles,
            action.Surfaces.HasFlag(ActionSurfaces.File) ? "1" : "0",
            RegistryValueKind.String);

        item.SetValue(
            ShellCommandIds.ValueOnFolders,
            action.Surfaces.HasFlag(ActionSurfaces.Menu) ? "1" : "0",
            RegistryValueKind.String);

        //Only the Commit entry. On Pull it would read as "pull *into* this branch" -- true, and
        //saying nothing the entry above it has not already said.
        item.SetValue(
            ShellCommandIds.ValueShowBranch,
            action.Cli == "commit" ? "1" : "0",
            RegistryValueKind.String);

        if (action.IconFileName is { Length: > 0 } iconName)
        {
            string icon = Path.Combine(Path.GetDirectoryName(cliPath) ?? string.Empty, "icons", iconName);

            if (File.Exists(icon))
                item.SetValue(ShellCommandIds.ValueIcon, icon, RegistryValueKind.String);
        }
    }

    /// <summary>The "FlickGit" submenu, carrying everything not worth a root entry.</summary>
    private static void WriteMenuVerb(RegistryKey classes, string parent, string appPath, uint separators)
    {
        using RegistryKey verb = classes.CreateSubKey($@"{parent}\{MenuVerbKeyName}", writable: true)
                                 ?? throw new InvalidOperationException($@"Could not create {parent}\{MenuVerbKeyName}.");

        verb.SetValue("MUIVerb", Strings.Get("shell.menu.root"), RegistryValueKind.String);

        //The exe's own first icon, so the submenu is branded even before the icons\
        //directory is consulted.
        verb.SetValue("Icon", $"{appPath},0", RegistryValueKind.String);

        //Resolved relative to HKCR, i.e. HKCU\Software\Classes\FlickGit.Menu.
        verb.SetValue("ExtendedSubCommandsKey", MenuKeyName, RegistryValueKind.String);

        //The bar under the whole FlickGit block.
        if (separators != 0)
            verb.SetValue("CommandFlags", unchecked((int)separators), RegistryValueKind.DWord);

        //No Position, for the reason the root verbs have none. See the class remarks.
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
    /// The separator flags for one root entry: a bar above the first, a bar below the last.
    ///
    /// <paramref name="rootActions"/> must already be in the order Explorer will draw them, or "first"
    /// and "last" name the wrong entries and the bars land inside the block instead of around it.
    /// </summary>
    /// <param name="hasSubmenu">
    /// True when the <c>FlickGit</c> submenu verb exists. It sorts after every root entry — its key is
    /// <c>zz.menu</c> — so it takes the closing bar and no root entry does.
    /// </param>
    private static uint SeparatorFor(GitAction action, IReadOnlyList<GitAction> rootActions, bool hasSubmenu)
    {
        uint flags = 0;

        if (ReferenceEquals(action, rootActions[0]))
            flags |= ShellCommandIds.SeparatorBefore;

        if (!hasSubmenu && ReferenceEquals(action, rootActions[^1]))
            flags |= ShellCommandIds.SeparatorAfter;

        return flags;
    }

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
