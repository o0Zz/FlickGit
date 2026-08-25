namespace FlickGit.Actions;

/// <summary>
/// Which surfaces an action appears on.
///
/// There is no <c>Cli</c> value: the command line reaches an action by its verb or by its id
/// rather than by asking the catalog what to offer, so a flag for it would be read by nothing.
/// </summary>
[Flags]
public enum ActionSurfaces
{
    None = 0,

    Menu = 1,

    Palette = 2,

    /// <summary>
    /// The Explorer context menu on a <b>file</b>. Its own surface rather than a flag on
    /// <see cref="Menu"/>, because the two lists are drawn on different clicks and share nothing:
    /// committing a folder makes sense and blaming one does not.
    /// </summary>
    File = 4,

    /// <summary>
    /// Both the folder menu and the palette -- what an action gets when it does not say. Deliberately
    /// <i>not</i> including <see cref="File"/>: that would turn every existing action, and every user
    /// action in actions.json, into a file entry.
    /// </summary>
    All = Menu | Palette,
}

/// <summary>
/// What a second token after an action means. Only the kinds with an action that takes one are
/// declared: a value nothing can produce is a value nothing can be tested against.
/// </summary>
public enum ActionParameter
{
    None,

    Branch,

    /// <summary>
    /// A tag name, typed rather than completed. <b>The one parameter kind with no completion source,
    /// and that is the point:</b> the token after <c>tag</c> is a tag being <i>created</i>, so the
    /// existing tags are the one set of values it will never be. The palette validates what was typed
    /// instead.
    /// </summary>
    Tag,
}

/// <summary>
/// What to do with an action's output. A <c>GitRun</c> that prunes remotes has something to say
/// and nowhere to say it, and silence is indistinguishable from failure.
/// </summary>
public enum ActionOutput
{
    Toast,

    Window,

    /// <summary>Nothing on success. Failures are always reported.</summary>
    None,
}

/// <summary>One thing FlickGit can do, defined once and projected onto every surface.</summary>
/// <param name="Id">Stable. <c>"commit"</c> for a built-in, <c>"custom.fetch-prune"</c> from the file.</param>
/// <param name="IconFileName">
/// A file name inside <c>icons\</c>, not a path: an action file that could name an absolute path
/// could name one outside it.
/// </param>
/// <param name="RequiresConfirmation">
/// A second explicit confirmation before anything runs, on <i>every</i> surface. Forced true for
/// anything <see cref="ActionSafety"/> recognises as destructive, whatever the file said.
/// </param>
/// <param name="InMoreSubmenu">
/// False means a root verb, true means inside the FlickGit submenu. Windows 11 accepts only one
/// level of submenu, so this is a flag rather than a tree.
/// </param>
public sealed record GitAction
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public required ActionRun Run { get; init; }

    public string? IconFileName { get; init; }

    public ActionSurfaces Surfaces { get; init; } = ActionSurfaces.All;

    /// <summary>
    /// Whether the clicked folder has to be inside a working tree. One flag rather than the set of
    /// conditions CLAUDE.md sketches, because this is the only distinction anything actually draws.
    /// </summary>
    public bool RequiresRepository { get; init; }

    public bool RequiresConfirmation { get; init; }

    public ActionOutput Output { get; init; } = ActionOutput.Toast;

    public ActionParameter Parameter { get; init; } = ActionParameter.None;

    public int MenuOrder { get; init; }

    public bool InMoreSubmenu { get; init; }

    /// <summary>
    /// Built-ins can be hidden and reordered, never deleted -- so "delete" is this flag, and the entry
    /// stays in the settings list where the user can put it back.
    /// </summary>
    public bool Hidden { get; init; }

    public bool IsBuiltIn { get; init; }

    /// <summary>
    /// The CLI spelling for a built-in, null for a user action. Derived from <see cref="Id"/> rather
    /// than stored beside it: a built-in's id <i>is</i> its verb, which is what makes `flick commit`
    /// and the Commit action the same code path.
    /// </summary>
    public string? Cli => IsBuiltIn ? Id : null;
}
