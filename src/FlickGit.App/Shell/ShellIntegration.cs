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
/// <b>root</b> items in the folder's context menu, and everything else is one submenu below them. A
/// submenu costs a hover and a second aim, so putting Commit behind one taxes the most frequent
/// action in the product in order to tidy up the least frequent ones.
///
/// <b>The whole block is one <c>IContextMenu</c> handler</b> — <c>FlickGit.Shell.dll</c>, registered
/// under <c>shellex\ContextMenuHandlers</c> — and there is no longer a static-verb layout beside it.
/// That is the placement, and three attempts established that nothing else provides it: a static verb
/// reaches <c>Top</c>, the default, or <c>Bottom</c>, and Explorer draws the static-verb block above
/// the shell-extension block, which it draws above <c>New</c>. So the default left FlickGit up among
/// <c>Open with Code</c> and <c>Git GUI Here</c>, <c>Bottom</c> pushed it past <c>New</c> down beside
/// <c>Properties</c>, and <c>CommandFlags</c> drew the separators around the entries in that same
/// wrong block. The slot every Git client occupies belongs to handlers.
///
/// The handler contributes the block at once, which makes this simpler than what it replaced rather
/// than more complex: one CLSID instead of one per verb, the branch folded into the label as the item
/// is inserted, a repository-requiring item simply not inserted outside a repository, and the
/// separators drawn with <c>MF_SEPARATOR</c> instead of asked for.
///
/// <b>The static verbs were deleted rather than kept as a fallback.</b> They were written when
/// <c>FlickGit.Shell.dll</c> was absent — which is only ever a <c>dotnet build</c> working tree, since
/// Native AOT runs on publish — and the safety net they were also credited with does not exist: the
/// choice is made by the file being present, not by Explorer managing to load it, so a machine where
/// Smart App Control or WDAC refuses the unsigned DLL gets the handler registered and no verbs, and
/// therefore no menu rather than a degraded one. A second write path, a second read-back and a second
/// shape for <see cref="IsInstalled"/> bought a developer convenience and nothing else; per Hard
/// Requirement 1 it went in one change. <see cref="Install"/> now refuses when the DLL is missing and
/// says to publish, and <see cref="Uninstall"/> still removes verbs an earlier version wrote.
///
/// On Windows 11 the block appears under "Show more options" (Shift+F10). That is a limitation of the
/// classic menu, not of this code — the Windows 11 <i>primary</i> menu needs a sparse MSIX package,
/// which is the one part of Phase 6 still open.
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

    private const string ClassesPath = @"Software\Classes";

    /// <summary>
    /// The <c>shell</c> parents an earlier version registered static verbs under.
    ///
    /// <b>Read by <see cref="Uninstall"/> only.</b> Nothing writes a verb any more — see the class
    /// remarks — but a machine that ran a version which did still has the keys, and an uninstall
    /// that could not reach them would leave a menu entry behind pointing at a deleted exe.
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
    /// <b><see cref="GitAction.RequiresRepository"/> is written out rather than resolved here</b>, as
    /// <c>FlickGit.NeedsRepository</c>: the handler is asked on every right-click and drops those items
    /// when the clicked folder is not a repository. A static verb could not — it is written once and
    /// drawn on every folder on the machine — which is one of the things that layout could not do.
    ///
    /// Hidden entries never reach the registry at all: those the user turned off really are absent.
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

        if (!File.Exists(cliPath))
        {
            //Registering a command line pointing at an exe that is not there would produce
            //a context menu entry that silently does nothing -- the worst possible failure
            //mode for a shell extension.
            return new InstallResult(false,
                $"flick.exe was not found in:\n\n{installDirectory}\n\n" +
                "The context menu was not modified.");
        }

        //Both refusals come before Uninstall, so an install that cannot proceed leaves a working
        //registration alone rather than removing it and then failing to write a new one.
        if (ShellHandlerAvailable(installDirectory) is not { } handlerDll)
        {
            return new InstallResult(false,
                $"{ShellCommandIds.DllFileName} was not found in:\n\n{installDirectory}\n\n" +
                "The menu is drawn by that handler, and Native AOT only builds it on publish, so a " +
                "`dotnet build` working tree does not have one. Run `dotnet publish` and register " +
                "from the published output.\n\nThe context menu was not modified.");
        }

        try
        {
            Uninstall();

            using RegistryKey classes = Registry.CurrentUser.CreateSubKey(ClassesPath, writable: true)
                                        ?? throw new InvalidOperationException($@"Could not open HKCU\{ClassesPath}.");

            IReadOnlyList<GitAction> actions = MenuActions();

            //The catalog is already in MenuOrder, and that is the order the handler draws in: it
            //enumerates the Items subkeys, which are numbered as they are written. Nothing re-sorts
            //them here -- the verb layout did, alphabetically by key name, because that is what
            //Explorer sorted verbs on, and "FlickGit.100.x" sorts before "FlickGit.20.x".
            GitAction[] rootActions = [.. actions.Where(a => !a.InMoreSubmenu)];
            GitAction[] submenuActions = [.. actions.Where(a => a.InMoreSubmenu)];

            WriteContextMenuHandler(classes, cliPath, handlerDll, rootActions, submenuActions);

            //Read back what was written. CLAUDE.md, "Registry synchronisation" step 4: "Verify by
            //reading back; report failures in the UI." A registry write that silently did nothing --
            //group policy, a locked hive -- must not be reported as success.
            if (Verify(handlerDll) is { } verification)
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

    /// <summary>
    /// True when the handler is registered. Asked by `flick diag doctor` and by the settings
    /// window's context-menu checkbox.
    ///
    /// <b>One probe, because <see cref="Install"/> writes one layout.</b> It briefly had to ask about
    /// two: the static verbs were what this looked for while the handler was what an install actually
    /// wrote, so a working menu reported "not installed" and the checkbox sat unticked. Deleting the
    /// verb layout removed the second question rather than answering it.
    ///
    /// Static verbs left by an earlier version do not count as installed — nothing draws them the way
    /// this code means any more, and ticking the box re-registers properly. <see cref="Uninstall"/>
    /// still removes them.
    /// </summary>
    public bool IsInstalled()
    {
        using RegistryKey? handler = Registry.CurrentUser.OpenSubKey(
            $@"{ClassesPath}\{HandlerOwners[0]}\{ShellCommandIds.ContextMenuHandlersPath}\{ShellCommandIds.HandlerKeyName}",
            writable: false);

        return handler is not null;
    }

    /// <summary>
    /// The full path of the shell DLL if it is there to be registered, or null.
    ///
    /// Null means a `dotnet build` working tree: Native AOT only runs on publish, so the DLL beside
    /// the executables exists only in a published layout. <see cref="Install"/> refuses on null rather
    /// than registering a CLSID with no DLL behind it, which would leave Explorer unable to create the
    /// object and drop the whole block.
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

        //The popup's own icon, named as a file rather than as `FlickGit.exe,0`: `InsertMenu` takes no
        //icon at all, and `MenuIcons` loads an .ico *file*, so the file the exe's resource was built
        //from is what the DLL is pointed at. Written only when it is there, so a missing file leaves
        //the value absent rather than naming nothing.
        if (AppIconPath(cliPath) is { } appIcon)
            clsid.SetValue(ShellCommandIds.ValueSubmenuIcon, appIcon, RegistryValueKind.String);

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

    /// <summary>
    /// The product icon beside the executables, or null when it is not there.
    ///
    /// Not in <c>icons\</c> with the per-action ones: it is the icon the two executables were built
    /// with, and the tray and the About tab already read it from this path.
    /// </summary>
    private static string? AppIconPath(string cliPath)
    {
        string icon = Path.Combine(
            Path.GetDirectoryName(cliPath) ?? string.Empty, "Resources", "flickgit.ico");

        return File.Exists(icon) ? icon : null;
    }

    /// <summary>
    /// Reads the registration back out of the registry, and returns what to tell the user when it is
    /// not what was just written.
    ///
    /// Two values, which are the two halves a right-click needs: the handler key Explorer looks for
    /// under the clicked class, and the DLL path behind the CLSID it names. Neither says whether
    /// Explorer can actually <i>create</i> the object — that cannot be answered from here without
    /// loading the DLL into this process, which is the one thing this assembly must not do.
    /// </summary>
    private static string? Verify(string expectedDll)
    {
        using RegistryKey? handler = Registry.CurrentUser.OpenSubKey(
            $@"{ClassesPath}\{HandlerOwners[0]}\{ShellCommandIds.ContextMenuHandlersPath}\{ShellCommandIds.HandlerKeyName}",
            writable: false);

        if (handler?.GetValue(string.Empty) as string != ShellCommandIds.MenuHandlerClsid)
            return "The context menu keys were written but could not be read back. " +
                   "Group policy or another tool may be blocking HKCU\\Software\\Classes.";

        using RegistryKey? server = Registry.CurrentUser.OpenSubKey(
            $@"{ClassesPath}\{ClsidPath}\{ShellCommandIds.MenuHandlerClsid}\InprocServer32", writable: false);

        string? dll = server?.GetValue(string.Empty) as string;

        return string.Equals(dll, expectedDll, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"The context menu was registered, but points somewhere unexpected:\n\n{dll}";
    }
}

/// <param name="Succeeded">False means the registry was not left in the intended state.</param>
/// <param name="Message">Shown verbatim. Never a generic string.</param>
public sealed record InstallResult(bool Succeeded, string Message);
