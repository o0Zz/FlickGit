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
    /// Which verbs get a handler, and the CLSID each one is registered as.
    ///
    /// <b>Only the two root entries.</b> They are the ones the user sees without hovering, so they
    /// are where a live branch name and a hidden-outside-a-repository entry are worth the cost of a
    /// DLL in <c>explorer.exe</c>. The <c>FlickGit</c> submenu stays static registry verbs: its
    /// items are one hover away, already grouped under a name that says what they are, and every
    /// handler added here is another object Explorer builds on every right-click.
    ///
    /// The verb is the key: it is the CLI spelling, which is also the built-in action's id, so this
    /// table joins to the Action Catalog without carrying a second copy of anything from it.
    /// </summary>
    public static readonly (string Verb, string Clsid, bool ShowBranch)[] Handlers =
    [
        //Commit / Push -- the entry the branch name is actually for.
        ("commit", "{F1C7A6D2-3B84-4E5A-9C61-7D2E8A4B5C10}", true),

        //Pull (rebase). No branch in the label: it would read as "pull *into* this branch", which is
        //true but says nothing the Commit entry above it has not already said, and two parenthesised
        //branch names one under the other is noise. It is here for GetState, so that neither root
        //entry appears on a folder that is not a repository.
        ("pull-rebase", "{F1C7A6D2-3B84-4E5A-9C61-7D2E8A4B5C11}", false),
    ];

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
