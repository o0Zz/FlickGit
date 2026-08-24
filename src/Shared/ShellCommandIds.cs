namespace FlickGit.Shared;

/// <summary>
/// The CLSIDs of the <c>IExplorerCommand</c> handlers, and the value names their configuration
/// lives under.
///
/// <b>Shared as source, compiled into both assemblies</b>, exactly as <c>IpcMessages.cs</c> is: the
/// App writes these keys and the shell DLL reads them, so a GUID that disagreed between the two
/// would register a handler Explorer could never create. A third assembly is not an option — the
/// DLL is Native AOT and loads into <c>explorer.exe</c>, where a reference it would have to resolve
/// is exactly the thing that must not exist.
///
/// <b>These GUIDs are permanent.</b> Hard Requirement 1 licences breaking our own formats freely,
/// and this is the one exception in the product: a CLSID is written into the user's registry under
/// <c>HKCU\Software\Classes\CLSID</c>, and changing one leaves an orphan key that
/// <see cref="Verbs"/> can no longer find to delete. Add a new entry rather than renumbering an
/// existing one.
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
    /// The CLSIDs of the per-verb <c>IExplorerCommand</c> handlers this replaced.
    ///
    /// Listed only so an uninstall can remove them from a registry that an earlier version wrote
    /// into. Nothing creates them any more; see <b>These GUIDs are permanent</b> above for why they
    /// cannot simply be forgotten.
    /// </summary>
    public static readonly string[] RetiredClsids =
    [
        "{F1C7A6D2-3B84-4E5A-9C61-7D2E8A4B5C10}",
        "{F1C7A6D2-3B84-4E5A-9C61-7D2E8A4B5C11}",
    ];

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
    /// Whether the item is drawn on a clicked <b>file</b>, and whether on a folder.
    ///
    /// Two values rather than one scope word, because the flags they come from are independent: an
    /// action may sensibly be offered on both, and the handler is asked about one click at a time.
    /// Today Blame is the only file item and everything else is folder-only.
    /// </summary>
    public const string ValueOnFiles = "FlickGit.OnFiles";

    public const string ValueOnFolders = "FlickGit.OnFolders";

    /// <summary>The popup's own label, already localised.</summary>
    public const string ValueSubmenuLabel = "FlickGit.SubmenuLabel";

    /// <summary>The subkey under the handler's CLSID holding one subkey per menu entry.</summary>
    public const string ItemsKeyName = "Items";

    /// <summary>
    /// The <c>EXPCMDFLAGS</c> value <c>IExplorerCommand::GetFlags</c> reports, as a decimal string.
    ///
    /// In practice one of <see cref="SeparatorBefore"/> or <see cref="SeparatorAfter"/>, which is
    /// what draws the bar that gives the FlickGit entries a block of their own. Decided by the App,
    /// because it is the App that knows which entry is first and which is last — the DLL sees one
    /// CLSID at a time and could not work it out.
    /// </summary>
    public const string ValueCommandFlags = "FlickGit.CommandFlags";

    /// <summary><c>ECF_SEPARATORBEFORE</c>. A bar above this entry.</summary>
    public const uint SeparatorBefore = 0x20;

    /// <summary><c>ECF_SEPARATORAFTER</c>. A bar below it.</summary>
    public const uint SeparatorAfter = 0x40;

    /// <summary>
    /// The file name the App looks for beside itself, and registers only if it is really there.
    ///
    /// A handler pointing at a CLSID whose DLL is missing is worse than no handler: Explorer cannot
    /// create the object and drops the entry, so a plain `dotnet build` -- which produces no native
    /// DLL, because Native AOT only runs on publish -- would silently delete two working menu
    /// entries. So the registration is conditional, and the static verbs stay exactly as they were.
    /// </summary>
    public const string DllFileName = "FlickGit.Shell.dll";
}
