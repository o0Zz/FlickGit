namespace FlickGit.Actions;

/// <summary>
/// Which surfaces an action appears on.
///
/// There is no <c>Cli</c> value. The command line reaches an action by its verb (<c>flick commit</c>)
/// or by its id (<c>flick run custom.x</c>) rather than by asking the catalog what to offer, so a flag
/// for it would be read by nothing — and a flag nothing reads is worse than no flag, because the next
/// person assumes it means something.
/// </summary>
[Flags]
public enum ActionSurfaces
{
    None = 0,

    /// <summary>The Explorer context menu on a folder, a drive, or a folder's background.</summary>
    Menu = 1,

    Palette = 2,

    /// <summary>
    /// The Explorer context menu on a <b>file</b>.
    ///
    /// Its own surface rather than a flag on <see cref="Menu"/>, because the two lists are drawn on
    /// different clicks and share nothing: committing a folder makes sense and blaming one does not.
    ///
    /// Reachable only through the <c>IContextMenu</c> handler, never as a static registry verb. A
    /// static verb cannot hide itself, so registering one under <c>*</c> would put a FlickGit
    /// submenu on every file on the machine, repository or not.
    /// </summary>
    File = 4,

    /// <summary>
    /// Both the folder menu and the palette — what an action gets when it does not say.
    ///
    /// Deliberately <i>not</i> including <see cref="File"/>: adding it here would turn every
    /// existing action, and every user action in actions.json, into a file entry.
    /// </summary>
    All = Menu | Palette,
}

/// <summary>
/// What a second token after an action means, and where its completions come from.
///
/// CLAUDE.md's palette completes a second token from "the action's declared parameter kinds", and
/// lists branches, tags, remotes and stashes. Only the one with an action that takes it is declared:
/// a value nothing can produce is a value nothing can be tested against.
/// </summary>
public enum ActionParameter
{
    None,

    /// <summary>A branch, completed from the repository's own refs.</summary>
    Branch,

    /// <summary>
    /// A tag name, typed rather than completed.
    ///
    /// <b>The one parameter kind with no completion source, and that is the point.</b> The second
    /// token after <c>tag</c> is a tag being <i>created</i>, so the repository's existing tags are the
    /// one set of values it will never be — offering them would complete the user towards the only
    /// answer Git is certain to refuse. The palette validates what was typed instead, which is the
    /// same live feedback the branch ComboBox gives for a new branch name.
    /// </summary>
    Tag,
}

/// <summary>
/// What to do with an action's output.
///
/// A <c>GitRun</c> that prunes remotes has something to say and nowhere to say it, and silence is
/// indistinguishable from failure.
/// </summary>
public enum ActionOutput
{
    /// <summary>A notification. The default: brief, and it does not steal focus.</summary>
    Toast,

    /// <summary>A window with the whole of stdout and stderr, for something worth reading.</summary>
    Window,

    /// <summary>Nothing on success. Failures are always reported.</summary>
    None,
}

/// <summary>
/// One thing FlickGit can do, defined once and projected onto every surface.
///
/// CLAUDE.md, "Action Catalog": "The context menu, the palette and the CLI must not each define their
/// own list of operations." Before this existed they did — the menu had a hard-coded array in
/// <c>ShellIntegration</c>, the palette had one in <c>PaletteAction</c>, and the CLI had the verb
/// table. Three lists meant three places to add an entry and three chances to disagree about what it
/// was called.
/// </summary>
/// <param name="Id">Stable. <c>"commit"</c> for a built-in, <c>"custom.fetch-prune"</c> from the file.</param>
/// <param name="Label">Display text, already localised. User actions supply their own literal.</param>
/// <param name="IconFileName">
/// A file name inside <c>icons\</c>, not a path: the directory is resolved beside the running
/// executable, and an action file that could name an absolute path could name one outside it.
/// </param>
/// <param name="RequiresConfirmation">
/// A second explicit confirmation before anything runs, on <i>every</i> surface. Forced true for
/// anything <see cref="ActionSafety"/> recognises as destructive, whatever the file said.
/// </param>
/// <param name="MenuOrder">
/// The numeric stride Explorer sorts keys by, on both levels. Strided in tens so inserting between
/// two entries needs no other key rewritten.
/// </param>
/// <param name="InMoreSubmenu">
/// False means a root verb: an entry of its own in the folder context menu, one click from the
/// right-click. True means inside the FlickGit submenu. Windows 11 accepts only one level of
/// submenu, so this is a flag rather than a tree: the catalog has to stay projectable onto the
/// stricter surface.
/// </param>
public sealed record GitAction
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required ActionRun Run { get; init; }

    public string? IconFileName { get; init; }

    public ActionSurfaces Surfaces { get; init; } = ActionSurfaces.All;

    /// <summary>
    /// Whether the clicked folder has to be inside a working tree.
    ///
    /// One flag rather than the set of conditions CLAUDE.md sketches, because this is the only
    /// distinction anything in the product actually draws. The registry context menu cannot evaluate
    /// even this one — a verb is written once and shown on every folder — so it is honoured by the
    /// command line, which refuses with a reason, and by <c>IExplorerCommand::GetState</c> when that
    /// arrives in Phase 6.
    /// </summary>
    public bool RequiresRepository { get; init; }

    public bool RequiresConfirmation { get; init; }

    public ActionOutput Output { get; init; } = ActionOutput.Toast;

    /// <summary>What a second token means, for the palette's completion.</summary>
    public ActionParameter Parameter { get; init; } = ActionParameter.None;

    public int MenuOrder { get; init; }

    public bool InMoreSubmenu { get; init; }

    /// <summary>
    /// Built-ins can be hidden and reordered, never deleted — CLAUDE.md. So "delete" is this flag,
    /// and the entry stays in the settings list where the user can put it back.
    /// </summary>
    public bool Hidden { get; init; }

    public bool IsBuiltIn { get; init; }

    /// <summary>
    /// The CLI spelling, for a built-in that has one.
    ///
    /// Both what `flick &lt;verb&gt;` accepts and what the palette's footer shows, so the command the
    /// user is told about is the command that runs.
    /// </summary>
    public string? Cli { get; init; }

}
