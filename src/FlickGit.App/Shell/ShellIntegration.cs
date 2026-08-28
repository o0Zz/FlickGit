using System.IO;
using FlickGit.Actions;
using FlickGit.Shared;
using FlickGit.App.Localization;
using FlickGit.Logging;
using Microsoft.Win32;

namespace FlickGit.App.Shell;

/// <summary>
/// Registers and removes the Explorer context-menu entries. Every entry is a thin trigger: it
/// launches <c>flick.exe</c> with a verb and a path, and contains no logic of any kind.
///
/// <b>The whole block is one <c>IContextMenu</c> handler</b> -- <c>FlickGit.Shell.dll</c>, under
/// <c>shellex\ContextMenuHandlers</c> -- and that is the only thing that reaches the right
/// placement. A static verb gets <c>Top</c>, the default, or <c>Bottom</c>; Explorer draws the
/// static-verb block above the shell-extension block, which it draws above <c>New</c>. So no verb
/// setting can land in the slot every Git client occupies.
///
/// <b>There is no static-verb fallback, and there must not be one.</b> Registering both layouts is
/// the menu twice over, and a fallback could not fire anyway: whether Explorer managed to load an
/// unsigned in-process server is not knowable from outside <c>explorer.exe</c>. So
/// <see cref="Install"/> refuses when the DLL is missing beside <c>flick.exe</c> -- which is only
/// ever a <c>dotnet build</c> working tree, since Native AOT runs on publish.
///
/// On Windows 11 the block appears under "Show more options". Reaching the primary menu needs a
/// sparse MSIX package, which is the one part of Phase 6 still open.
/// </summary>
public sealed class ShellIntegration(ActionCatalog catalog, ILog log)
{
    private const string ClassesPath = @"Software\Classes";

    /// <summary>
    /// The classes the handler is registered under.
    ///
    /// <c>*</c> is what puts Blame on a right-clicked file, and only a handler can be registered
    /// there: a static verb cannot hide itself, so one under <c>*</c> would draw a FlickGit submenu
    /// on every file on the machine. The cost is that Explorer loads this DLL on every file
    /// right-click, which is the price of a file entry at all.
    /// </summary>
    private static readonly string[] HandlerOwners =
    [
        "Directory",
        @"Directory\Background",
        "Drive",

        //All files. The handler draws only what the click asks for -- see ValueOnFiles.
        "*",
    ];

    private const string ClsidPath = "CLSID";

    /// <summary>
    /// The menu, projected from the Action Catalog.
    ///
    /// <see cref="GitAction.RequiresRepository"/> is written out rather than resolved here, as
    /// <c>FlickGit.NeedsRepository</c>: the handler is asked on every right-click and drops those
    /// items outside a repository. Hidden entries never reach the registry at all.
    ///
    /// <b>One entry per action, whatever mixture of clicks it answers.</b> Which click an item is
    /// drawn on is a set of flags <i>on the item</i> — see <see cref="WriteItem"/> — not the list it
    /// was written from, so an action offered on both a file and a folder must be written once and
    /// not once per surface, or the menu draws it twice.
    ///
    /// <c>MenuOrder</c>, because that is the order the handler draws in: it enumerates the numbered
    /// <c>Items</c> subkeys, and those are numbered as they are written.
    /// </summary>
    private IReadOnlyList<GitAction> MenuActions() =>
    [
        .. catalog
            .For(ActionSurfaces.Menu)
            .Concat(catalog.For(ActionSurfaces.File))
            .Concat(catalog.For(ActionSurfaces.Folder))
            .DistinctBy(a => a.Id)
            .OrderBy(a => a.MenuOrder),
    ];

    /// <summary>
    /// Writes the whole menu. Idempotent: the existing keys are removed first, so a re-apply after a
    /// settings change cannot leave a stale entry behind.
    /// </summary>
    public InstallResult Install()
    {
        string installDirectory = InstallDirectory;
        string cliPath = Path.Combine(installDirectory, "flick.exe");

        if (!File.Exists(cliPath))
        {
            //A command line pointing at an exe that is not there produces a context menu entry that
            //silently does nothing -- the worst failure mode for a shell extension.
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

            WriteContextMenuHandler(classes, cliPath, handlerDll, MenuActions());

            //Read back what was written. A registry write that silently did nothing -- group policy, a
            //locked hive -- must not be reported as success.
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
    /// Removes exactly the keys this tool created, and nothing else. Every key removed here is named
    /// by <see cref="HandlerOwners"/> or by <see cref="ShellCommandIds"/>, the same two lists
    /// <see cref="Install"/> writes from, so the two cannot disagree about what belongs to FlickGit.
    /// </summary>
    public InstallResult Uninstall()
    {
        try
        {
            using RegistryKey? classes = Registry.CurrentUser.OpenSubKey(ClassesPath, writable: true);
            if (classes is null)
                return new InstallResult(true, "Nothing to remove.");

            //The same list the install uses, or the one that is only in the other list is the one that leaks.
            foreach (string owner in HandlerOwners)
            {
                using RegistryKey? handlers = classes.OpenSubKey(
                    $@"{owner}\{ShellCommandIds.ContextMenuHandlersPath}", writable: true);

                handlers?.DeleteSubKeyTree(ShellCommandIds.HandlerKeyName, throwOnMissingSubKey: false);
            }

            //By name, never by enumerating CLSID, which is every COM class on the machine. This is why
            //ShellCommandIds calls its GUID permanent: a renumbered one is a key nothing can find to delete.
            using (RegistryKey? clsids = classes.OpenSubKey(ClsidPath, writable: true))
                clsids?.DeleteSubKeyTree(ShellCommandIds.MenuHandlerClsid, throwOnMissingSubKey: false);

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
    /// Where flick.exe, FlickGit.exe and <c>icons\</c> live: beside the running module, not the
    /// working directory -- Explorer sets that to the clicked folder, so anything relative would look
    /// for the executables inside the user's repository.
    /// </summary>
    private static string InstallDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>True when the handler is registered. Asked by `flick diag doctor` and by the settings
    /// window's context-menu checkbox.</summary>
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
    /// Null means a `dotnet build` working tree: Native AOT only runs on publish.
    /// <see cref="Install"/> refuses rather than registering a CLSID with no DLL behind it, which
    /// would leave Explorer unable to create the object and drop the whole block.
    /// </summary>
    private static string? ShellHandlerAvailable(string installDirectory)
    {
        string path = Path.Combine(installDirectory, ShellCommandIds.DllFileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Registers the context-menu handler, and writes the whole menu into its CLSID key.
    ///
    /// The DLL holds no interface text: every label written here is already localised from the
    /// <c>.lang</c> file in force, so <c>flick language de</c> plus a re-register changes the menu
    /// without that assembly knowing a word of German.
    /// </summary>
    private void WriteContextMenuHandler(
        RegistryKey classes,
        string cliPath,
        string dllPath,
        IReadOnlyList<GitAction> actions)
    {
        using RegistryKey clsid = classes.CreateSubKey($@"{ClsidPath}\{ShellCommandIds.MenuHandlerClsid}", writable: true)
                                  ?? throw new InvalidOperationException("Could not register the context-menu handler.");

        clsid.SetValue(string.Empty, "FlickGit context menu", RegistryValueKind.String);
        clsid.SetValue(ShellCommandIds.ValueExe, cliPath, RegistryValueKind.String);
        clsid.SetValue(ShellCommandIds.ValueSubmenuLabel, Strings.Get("shell.menu.root"), RegistryValueKind.String);

        //The popup's own icon, named as a file rather than as `FlickGit.exe,0`: MenuIcons loads an .ico
        //*file*, so the file the exe's resource was built from is what the DLL is pointed at. Written
        //only when it is there, so a missing file leaves the value absent rather than naming nothing.
        if (AppIconPath(cliPath) is { } appIcon)
            clsid.SetValue(ShellCommandIds.ValueSubmenuIcon, appIcon, RegistryValueKind.String);

        using (RegistryKey server = clsid.CreateSubKey("InprocServer32", writable: true)
                                   ?? throw new InvalidOperationException("Could not register the handler's server."))
        {
            server.SetValue(string.Empty, dllPath, RegistryValueKind.String);

            //Apartment: what a shell extension is called on. The shell then marshals for us rather than
            //expecting this code to be free-threaded.
            server.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
        }

        //Rewritten from scratch, so an action the user has since hidden does not survive as an item
        //nothing removed.
        clsid.DeleteSubKeyTree(ShellCommandIds.ItemsKeyName, throwOnMissingSubKey: false);

        using RegistryKey items = clsid.CreateSubKey(ShellCommandIds.ItemsKeyName, writable: true)
                                  ?? throw new InvalidOperationException("Could not write the menu items.");

        int order = 0;

        //One pass, in MenuOrder, for every click an action answers -- see MenuActions. The root
        //entries come first because their order values do; nothing here re-sorts them.
        foreach (GitAction action in actions)
            WriteItem(items, ref order, cliPath, action, inSubmenu: action.InMoreSubmenu);

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

        //Which clicks this item answers. The handler is registered on files and on folders alike, so
        //without these every folder action would appear on a file -- and Blame on a directory.
        item.SetValue(
            ShellCommandIds.ValueOnFiles,
            action.Surfaces.HasFlag(ActionSurfaces.File) ? "1" : "0",
            RegistryValueKind.String);

        item.SetValue(
            ShellCommandIds.ValueOnFolders,
            action.Surfaces.HasFlag(ActionSurfaces.Menu) ? "1" : "0",
            RegistryValueKind.String);

        //Narrower than OnFolders, and not implied by it: a folder the user pointed at. Add and Remove
        //are the two that act on everything below it, so they are the two that must not be reachable
        //from a background, a drive or the repository root.
        item.SetValue(
            ShellCommandIds.ValueOnClickedFolders,
            action.Surfaces.HasFlag(ActionSurfaces.Folder) ? "1" : "0",
            RegistryValueKind.String);

        //Whether the handler hands over everything that was selected or only the item under the
        //pointer. Keyed off the verb rather than off a surface flag, because it is a fact about the
        //verb's own grammar -- `add` and `rm` are the two that read more than one positional path,
        //and handing a selection to a verb whose second slot means a branch or a tag name would turn
        //the second file into an argument.
        item.SetValue(
            ShellCommandIds.ValueOnSelection,
            action.Cli is "add" or "rm" ? "1" : "0",
            RegistryValueKind.String);

        //Only the Commit entry. On Pull it would read as "pull *into* this branch" -- true, and saying
        //nothing the entry above it has not already said.
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
    /// The product icon beside the executables, or null when it is not there. Not in <c>icons\</c>
    /// with the per-action ones: it is the icon the two executables were built with.
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
    /// Neither value says whether Explorer can actually <i>create</i> the object -- that cannot be
    /// answered from here without loading the DLL into this process, which is the one thing this
    /// assembly must not do.
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

public sealed record InstallResult(bool Succeeded, string Message);
