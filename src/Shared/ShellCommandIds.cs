namespace FlickGit.Shared;

/// <summary>
/// The CLSID of the context-menu handler, and the value names its configuration lives under.
///
/// <b>Shared as source, compiled into both assemblies</b>, exactly as <c>IpcMessages.cs</c> is: the
/// App writes these keys and the shell DLL reads them, so a GUID that disagreed between the two
/// would register a handler Explorer could never create. A third assembly is not an option — the
/// DLL is Native AOT and loads into <c>explorer.exe</c>, where a reference it would have to resolve
/// is exactly the thing that must not exist.
///
/// <b>This GUID is permanent.</b> Hard Requirement 1 licences breaking our own formats freely, and
/// this is the one exception in the product: a CLSID is written into the user's registry under
/// <c>HKCU\Software\Classes\CLSID</c>, and changing it leaves an orphan key that an uninstall can
/// no longer find to delete. Add a new entry rather than renumbering this one.
/// </summary>
internal static class ShellCommandIds
{
    /// <summary>
    /// The context-menu handler: <b>one</b> CLSID for the whole FlickGit block.
    ///
    /// <b>Why one and not one per verb.</b> This replaced two <c>IExplorerCommand</c> handlers, which
    /// Explorer asked about separately because each was registered on its own static verb. Those
    /// verbs could not be placed: a static verb reaches <c>Top</c>, the default, or <c>Bottom</c>,
    /// and Explorer draws the static-verb block above the shell-extension block, which it draws
    /// above <c>New</c> — so the slot every Git client sits in, immediately above <c>New</c>, is not
    /// addressable by a verb at all. A <c>ContextMenuHandler</c> is, and it contributes the whole
    /// block at once, so it is one object and one id.
    /// </summary>
    public const string MenuHandlerClsid = "{F1C7A6D2-3B84-4E5A-9C61-7D2E8A4B5C20}";

    /// <summary>
    /// The icon-overlay handler: a <b>second</b> COM object, in the same DLL.
    ///
    /// <b>Why a second CLSID and not another interface on the first.</b> Explorer asks about the two
    /// through completely different mechanisms -- a <c>ContextMenuHandler</c> is created when a menu is
    /// built, an overlay identifier is created once at Explorer startup and then asked about every
    /// visible item forever. One identity serving both would hold the menu's per-click state for the
    /// life of the desktop.
    ///
    /// <b>Permanent, for the same reason <see cref="MenuHandlerClsid"/> is.</b> It is written into the
    /// user's registry, and into <c>HKLM</c> as well, so a renumbered one is two keys nothing can find
    /// to delete.
    /// </summary>
    public const string OverlayHandlerClsid = "{82C3902A-734C-4E68-AC63-59AE1F70BF2D}";

    /// <summary>
    /// Where Windows enumerates icon-overlay handlers.
    ///
    /// <b>This is the only key in the product outside <c>HKCU</c></b>, and the only reason
    /// <c>install-overlay</c> needs administrator rights at all. Everything else the overlay needs --
    /// the CLSID, the <c>InprocServer32</c>, the icon path -- resolves through <c>CoCreateInstance</c>,
    /// which reads <c>HKCU\Software\Classes\CLSID</c> before the machine hive.
    /// </summary>
    public const string OverlayIdentifiersPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers";

    /// <summary>
    /// Our subkey name under <see cref="OverlayIdentifiersPath"/>.
    ///
    /// <b>The leading spaces are load-bearing, and there are five of them for a measured reason.</b>
    /// Windows sorts these names ordinally and there are two limits behind that sort, not one: it
    /// enumerates the first <see cref="OverlaySlotLimit"/> handlers, and separately it has only
    /// fifteen overlay <i>image list</i> indices, four of which it keeps for its own overlays -- the
    /// shortcut arrow, the share hand, the slow-file badge. Indices are handed out in sort order, so
    /// a handler can be loaded, be asked, answer <c>S_OK</c>, and still be composited with nothing.
    ///
    /// <b>That is not hypothetical, it is what shipped.</b> With one space this key sorted eleventh,
    /// behind OneDrive's seven handlers and three from Office. Every repository answered S_OK and no
    /// badge was ever drawn -- indistinguishable, from outside, from the handler not working at all.
    /// Five spaces beats the four OneDrive uses; one space beat only names starting with a letter,
    /// which is nobody who competes for these slots.
    ///
    /// <b>Sorting first is not a claim to the corner.</b> <c>GetPriority</c> still answers 50, so a
    /// sync engine's "not uploaded yet" wins on any item where both apply. The sort decides whether
    /// we get an index at all; the priority decides what happens per item, and only the second of
    /// those is a question about which badge matters more.
    /// </summary>
    public const string OverlayKeyName = "     FlickGit";

    /// <summary>
    /// How many overlay handlers can actually be <i>drawn</i>, out of however many are registered.
    ///
    /// <b>Eleven, not fifteen, and the difference is the whole risk of this feature.</b> Fifteen is
    /// the size of the shell's overlay image list; Windows keeps four of those indices for its own
    /// overlays -- the shortcut arrow, the share hand, the slow-file badge -- so eleven is what is
    /// left for handlers, handed out in key-name sort order.
    ///
    /// The number matters because a handler past it fails <i>silently and completely</i>: it is
    /// created, <c>GetOverlayInfo</c> succeeds, <c>IsMemberOf</c> is called and answered, and nothing
    /// is ever composited. <c>flick diag doctor</c> reports our position against this, and reporting
    /// fifteen is what let a build ship that answered S_OK for every repository and drew nothing.
    /// </summary>
    public const int OverlaySlotLimit = 11;

    /// <summary>
    /// Where the handler is registered under each parent class, and the name of the key.
    ///
    /// <c>shellex\ContextMenuHandlers</c> rather than <c>shell</c>: that is the difference between
    /// being a verb and being a handler, and therefore the difference between the two menu blocks.
    /// </summary>
    public const string ContextMenuHandlersPath = @"shellex\ContextMenuHandlers";

    /// <summary>The key name under it. Recognisable as ours, and what an uninstall looks for.</summary>
    public const string HandlerKeyName = "FlickGit";

    /// <summary>
    /// Where the DLL reads what it needs, under its own <c>CLSID\{guid}</c> key.
    ///
    /// The DLL holds no strings of its own: the label arrives here already localised from the
    /// <c>.lang</c> files, which is what keeps <c>Strings</c> the only place interface text lives.
    /// A prefix on every name so the values are recognisable as ours in <c>regedit</c>.
    /// </summary>
    public const string ValueLabel = "FlickGit.Label";

    /// <summary>The full path of <c>flick.exe</c>, which <c>Invoke</c> starts.</summary>
    public const string ValueExe = "FlickGit.Exe";

    /// <summary>The verb to pass it.</summary>
    public const string ValueVerb = "FlickGit.Verb";

    /// <summary>The <c>.ico</c> path, or absent for no icon.</summary>
    public const string ValueIcon = "FlickGit.Icon";

    /// <summary><c>1</c> to append the current branch to the label.</summary>
    public const string ValueShowBranch = "FlickGit.ShowBranch";

    /// <summary><c>1</c> to hide the entry outside a repository.</summary>
    public const string ValueNeedsRepository = "FlickGit.NeedsRepository";

    /// <summary><c>1</c> to put the entry under the <c>FlickGit</c> popup rather than at the top level.</summary>
    public const string ValueInSubmenu = "FlickGit.InSubmenu";

    /// <summary>
    /// Which click the item is drawn on. Three values rather than one scope word, because the flags
    /// they come from are independent: an action may sensibly be offered on more than one, and the
    /// handler is asked about one click at a time.
    ///
    /// A clicked <b>file</b>.
    /// </summary>
    public const string ValueOnFiles = "FlickGit.OnFiles";

    /// <summary>
    /// Any folder click at all — a folder, the background of the folder being browsed, or a drive.
    /// This is what the repository entries carry.
    /// </summary>
    public const string ValueOnFolders = "FlickGit.OnFolders";

    /// <summary>
    /// A folder the user <b>pointed at</b>: not a background, not a drive, and not the repository
    /// root.
    ///
    /// Its own value rather than a narrowing of <see cref="ValueOnFolders"/>, because the two answer
    /// different clicks and an item may want either. Add and Remove carry this one: each acts on
    /// everything below the folder, which must not be reachable from a right-click that named no
    /// folder in particular.
    /// </summary>
    public const string ValueOnClickedFolders = "FlickGit.OnClickedFolders";

    /// <summary>
    /// The item acts on the <b>whole selection</b> rather than on the item under the pointer.
    ///
    /// Add and Remove, and only those two: they are the entries whose operand is a set, and the CLI
    /// verbs behind them are the only two that read more than one positional path. Everything else —
    /// Commit, Blame, Log — keeps being handed the first item, which is what it has always been given
    /// and what its verb still expects in the slot after the path.
    ///
    /// It travels as a registry value rather than as a verb name the handler knows, for the reason
    /// every other flag here does: the DLL holds no interface text and no verb spellings of its own,
    /// so what it draws and what it launches are both the App's to decide.
    /// </summary>
    public const string ValueOnSelection = "FlickGit.OnSelection";

    /// <summary>The popup's own label, already localised.</summary>
    public const string ValueSubmenuLabel = "FlickGit.SubmenuLabel";

    /// <summary>
    /// The full path of the <c>.ico</c> drawn beside the popup's label.
    ///
    /// Its own value rather than an <see cref="ValueIcon"/> on some item, because the popup is not an
    /// item: it carries no command id and is not in <see cref="ItemsKeyName"/> at all. Written by the
    /// App for the reason every path here is — the DLL resolves nothing of its own.
    /// </summary>
    public const string ValueSubmenuIcon = "FlickGit.SubmenuIcon";

    /// <summary>
    /// The <c>.ico</c> the overlay draws, under the overlay handler's own CLSID key.
    ///
    /// Written by the App for the same reason every path here is: the DLL resolves nothing of its own,
    /// and <c>GetOverlayInfo</c> hands Explorer a path rather than loading anything itself.
    /// </summary>
    public const string ValueOverlayIcon = "FlickGit.OverlayIcon";

    /// <summary>The subkey under the handler's CLSID holding one subkey per menu entry.</summary>
    public const string ItemsKeyName = "Items";

    /// <summary>
    /// The file name the App looks for beside itself, and registers only if it is really there.
    ///
    /// A handler pointing at a CLSID whose DLL is missing is worse than no handler: Explorer cannot
    /// create the object and drops the entry. A plain `dotnet build` produces no native DLL, because
    /// Native AOT only runs on publish, so the App refuses to register at all rather than writing a
    /// registration that draws nothing.
    /// </summary>
    public const string DllFileName = "FlickGit.Shell.dll";
}
