using System.Text.Json;
using FlickGit.Cli;
using FlickGit.Logging;

namespace FlickGit.Actions;

/// <summary>
/// Every action FlickGit can perform, defined once here and projected onto each surface.
///
/// CLAUDE.md, "Action Catalog": "The context menu, the palette and the CLI must not each define their
/// own list of operations." Before this, all three did — a hard-coded array in
/// <c>ShellIntegration</c>, another in <c>PaletteAction</c>, and the verb table. Two of those even
/// carried separate language keys for the same words, which is how "Switch branch…" came to exist
/// twice with a chance of disagreeing.
///
/// Built-ins ship in code and can be hidden, relabelled or reordered — never deleted. User actions
/// come from <c>actions.json</c>, which is a trust boundary: it can start arbitrary processes, so a
/// failure to read it leaves the built-ins working rather than leaving the product with no actions at
/// all, and anything destructive in it is forced to confirm whatever the file claims.
/// </summary>
public sealed class ActionCatalog
{
    /// <summary>
    /// Refused rather than migrated, per Hard Requirement 1. A file from a future build is read by
    /// nothing.
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    private readonly string _filePath;
    private readonly Func<string, string> _localise;
    private readonly ILog _log;

    private IReadOnlyList<GitAction> _actions = [];

    /// <param name="filePath">
    /// Where <c>actions.json</c> lives. Passed in rather than derived, because the directory is named
    /// by the settings layer in the WPF assembly and <c>FlickGit.Core</c> deliberately knows nothing
    /// about Windows or about where a user profile is.
    /// </param>
    /// <param name="localise">
    /// Resolves a built-in's label key. A delegate rather than a service: it is a pure function of its
    /// argument, and the string table lives in the executable that has the windows in it.
    /// </param>
    public ActionCatalog(string filePath, Func<string, string> localise, ILog log)
    {
        _filePath = filePath;
        _localise = localise;
        _log = log;

        Reload();
    }

    /// <summary>
    /// Why <c>actions.json</c> was not used, or null when it was.
    ///
    /// Surfaced by `flick diag doctor` and warned about once at startup. A user action that silently
    /// stopped appearing is a bug report; a user action that says why is a typo.
    /// </summary>
    public string? LoadError { get; private set; }

    /// <summary>Everything, including hidden entries — that is what the settings list edits.</summary>
    public IReadOnlyList<GitAction> All => _actions;

    /// <summary>What <paramref name="surface"/> should offer, in menu order.</summary>
    public IReadOnlyList<GitAction> For(ActionSurfaces surface) =>
        [.. _actions
            .Where(a => !a.Hidden && a.Surfaces.HasFlag(surface))
            .OrderBy(a => a.MenuOrder)];

    public GitAction? ById(string id) =>
        _actions.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds the list from the built-ins and the file.
    ///
    /// Private, because the only caller is the constructor: there is no settings window yet to write
    /// the file and ask for a re-read, and a public method with no caller is a guess about one.
    /// Never throws — a broken file leaves <see cref="LoadError"/> set and the built-ins intact.
    /// </summary>
    private void Reload()
    {
        LoadError = null;

        ActionsFileDto? file = Read();
        Dictionary<string, BuiltInOverrideDto> overrides = file?.BuiltIns ?? [];

        var actions = new List<GitAction>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (BuiltIn builtIn in BuiltIns)
        {
            overrides.TryGetValue(builtIn.Id, out BuiltInOverrideDto? over);
            actions.Add(builtIn.ToAction(_localise, over));
            ids.Add(builtIn.Id);
        }

        foreach (ActionDto dto in file?.Actions ?? [])
        {
            if (Convert(dto, ids) is { } action)
                actions.Add(action);
        }

        _actions = actions;
    }

    private ActionsFileDto? Read()
    {
        try
        {
            if (!File.Exists(_filePath))
                return null;

            ActionsFileDto? file = JsonSerializer.Deserialize(
                File.ReadAllText(_filePath),
                (System.Text.Json.Serialization.Metadata.JsonTypeInfo<ActionsFileDto>)
                    ActionsJson.Default.GetTypeInfo(typeof(ActionsFileDto))!);

            if (file is null)
                return null;

            if (file.SchemaVersion > CurrentSchemaVersion)
            {
                LoadError =
                    $"actions.json was written by a newer FlickGit (schema {file.SchemaVersion}, " +
                    $"this build understands {CurrentSchemaVersion}). Your custom actions are not " +
                    "loaded. Update FlickGit, or move the file aside.";

                _log.Warn(LoadError);
                return null;
            }

            return file;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            //Named, with the reason, because the alternative is a user action that stopped existing
            //for no visible cause.
            LoadError = $"actions.json could not be read, so only the built-in actions are available: {ex.Message}";
            _log.Warn(LoadError);
            return null;
        }
    }

    /// <summary>
    /// Turns one file entry into an action, or drops it with a reason.
    ///
    /// Every rejection is logged rather than silent. A user hand-editing this file needs to know which
    /// entry was wrong, and "my action doesn't appear" is otherwise unanswerable.
    /// </summary>
    private GitAction? Convert(ActionDto dto, HashSet<string> takenIds)
    {
        if (dto.Id is not { Length: > 0 } id)
        {
            _log.Warn("An action in actions.json has no id and was ignored.");
            return null;
        }

        if (!takenIds.Add(id))
        {
            //Refused rather than overriding: a user action that shadows "commit" would replace the
            //product's most safety-critical entry with an argument list from a file.
            _log.Warn($"actions.json defines '{id}', which is already taken. It was ignored.");
            return null;
        }

        if (dto.Label is not { Length: > 0 } label)
        {
            _log.Warn($"Action '{id}' has no label and was ignored.");
            return null;
        }

        if (ConvertRun(dto.Run, id) is not { } run)
            return null;

        return new GitAction
        {
            Id = id,
            Label = label,
            Run = run,
            IconFileName = FileNameOnly(dto.Icon),
            Surfaces = ParseSurfaces(dto.Surfaces),
            RequiresRepository = dto.RequiresRepo,

            //The one direction this flag travels. ActionSafety can turn it on; nothing turns it off.
            RequiresConfirmation = dto.RequiresConfirmation || ActionSafety.IsDestructive(run),

            Output = dto.ShowOutput?.ToLowerInvariant() switch
            {
                "window" => ActionOutput.Window,
                "none" => ActionOutput.None,
                _ => ActionOutput.Toast,
            },

            //Defaulted past the built-ins rather than to zero, so an entry with no menuOrder lands
            //after them instead of ahead of Commit.
            MenuOrder = dto.MenuOrder > 0 ? dto.MenuOrder : 900,
            InMoreSubmenu = dto.InMore,
            IsBuiltIn = false,
        };
    }

    private ActionRun? ConvertRun(ActionRunDto? dto, string id)
    {
        switch (dto?.Type?.ToLowerInvariant())
        {
            case "git":
                if (dto.Args is not { Length: > 0 } gitArgs)
                {
                    _log.Warn($"Action '{id}' is a git action with no args and was ignored.");
                    return null;
                }

                return new GitRun(gitArgs);

            case "process":
                if (dto.File is not { Length: > 0 } file)
                {
                    _log.Warn($"Action '{id}' is a process action with no file and was ignored.");
                    return null;
                }

                return new ProcessRun(file, dto.Args ?? []);

            case "window":
                if (Verb.Parse([dto.Verb ?? string.Empty], ".") is { Error: null } parsed)
                    return new WindowRun(parsed.Kind);

                _log.Warn($"Action '{id}' names an unknown verb '{dto.Verb}' and was ignored.");
                return null;

            case "composite":
                var steps = new List<ActionRun>();

                foreach (ActionRunDto step in dto.Steps ?? [])
                {
                    if (ConvertRun(step, id) is not { } converted)
                        return null;

                    steps.Add(converted);
                }

                if (steps.Count == 0)
                {
                    _log.Warn($"Action '{id}' is a composite with no steps and was ignored.");
                    return null;
                }

                return new CompositeRun(steps);

            default:
                _log.Warn($"Action '{id}' has run type '{dto?.Type}', which is not one of " +
                          "git, process, window or composite. It was ignored.");
                return null;
        }
    }

    /// <summary>
    /// Reads the <c>surfaces</c> list.
    ///
    /// An empty or entirely unrecognised list means both surfaces rather than neither: an action
    /// nobody can see is a worse answer to a typo than an action in one place too many.
    /// </summary>
    private static ActionSurfaces ParseSurfaces(string[]? names)
    {
        ActionSurfaces surfaces = ActionSurfaces.None;

        foreach (string name in names ?? [])
        {
            surfaces |= name.ToLowerInvariant() switch
            {
                "menu" => ActionSurfaces.Menu,
                "palette" => ActionSurfaces.Palette,
                "file" => ActionSurfaces.File,
                _ => ActionSurfaces.None,
            };
        }

        return surfaces == ActionSurfaces.None ? ActionSurfaces.All : surfaces;
    }

    /// <summary>
    /// Reduces whatever the file said to a bare file name.
    ///
    /// <c>"icons/fetch.ico"</c>, <c>"fetch.ico"</c> and <c>"..\..\Windows\evil.ico"</c> all become
    /// <c>fetch.ico</c> or <c>evil.ico</c>, which is then looked for in the install's own
    /// <c>icons\</c> directory and nowhere else. An icon path from a file must not be able to name a
    /// location outside it.
    /// </summary>
    private static string? FileNameOnly(string? icon) =>
        icon is { Length: > 0 } ? Path.GetFileName(icon) : null;

    // ---- the built-ins --------------------------------------------------------------

    /// <summary>
    /// One shipped action.
    ///
    /// <b>There is no <c>Cli</c> column.</b> A built-in's id <i>is</i> its verb spelling -- CLAUDE.md
    /// -- so <see cref="GitAction.Cli"/> derives it. It used to be a second parameter, and every
    /// entry below passed the same string into both.
    /// </summary>
    /// <param name="Id">The verb spelling, and the language key, as <c>action.&lt;id&gt;</c>.</param>
    private sealed record BuiltIn(
        string Id,
        VerbKind Verb,
        int MenuOrder,
        ActionSurfaces Surfaces,
        string? Icon = null,
        bool InMore = false,
        bool NeedsRepository = false,
        ActionParameter Parameter = ActionParameter.None)
    {
        public GitAction ToAction(Func<string, string> localise, BuiltInOverrideDto? over) =>
            new()
            {
                Id = Id,
                Run = new WindowRun(Verb),
                IsBuiltIn = true,
                Surfaces = Surfaces,
                RequiresRepository = NeedsRepository,
                IconFileName = Icon,

                //A relabelled built-in keeps the user's words; everything else comes from the
                //language file, so switching language relabels it.
                Label = over?.Label is { Length: > 0 } custom ? custom : localise($"action.{Id}"),

                Parameter = Parameter,
                MenuOrder = over?.MenuOrder ?? MenuOrder,
                InMoreSubmenu = over?.InMore ?? InMore,
                Hidden = over?.Hidden ?? false,
            };
    }

    /// <summary>
    /// The shipped list, and the single definition of FlickGit's own menu.
    ///
    /// The order values are strided in tens, and the More entries continue past 100 so that one
    /// number sorts the whole catalog. Explorer sorts submenu keys as <i>strings</i>, so every entry
    /// within one submenu has to have the same number of digits — which the two ranges give for free.
    ///
    /// Every built-in is a <see cref="WindowRun"/>. That is not a coincidence: a built-in that ran Git
    /// directly would be a second path to an operation the verb already implements with its
    /// guardrails, and CLAUDE.md's whole point about the palette not being "a shortcut around these
    /// rules" applies just as much to the menu.
    /// </summary>
    private static readonly BuiltIn[] BuiltIns =
    [
        //The two the user performs all day, at the root of the menu.
        new("commit", VerbKind.Commit, 10, ActionSurfaces.All, "commit.ico", NeedsRepository: true),
        new("pull-rebase", VerbKind.PullRebase, 20, ActionSurfaces.All, "pull.ico", NeedsRepository: true),
        //One pull entry, and it autostashes -- see PullService for why there is no plain one.

        //The fast path. Not on the context menu: CLAUDE.md's layout has one commit entry, and a
        //second beside it would be a decision the user has to make before seeing their changes.

        //Everything else, under More. Log first: it is the most-reached of the eight, and it is
        //still not a root entry because CLAUDE.md's two root entries are the two the user
        //*performs* all day -- and this one performs nothing.
        new("log", VerbKind.Log, 105, ActionSurfaces.All, "log.ico", InMore: true, NeedsRepository: true),

        //The file menu, which is this entry and nothing else so far. Its order only sorts it against
        //other file entries, of which there are none -- but the stride is kept so a second one has
        //somewhere to go.
        new("blame", VerbKind.Blame, 100, ActionSurfaces.File, "blame.ico", InMore: true, NeedsRepository: true),

        new("switch", VerbKind.Switch, 110, ActionSurfaces.All, "branch.ico", InMore: true,
            NeedsRepository: true, Parameter: ActionParameter.Branch),

        //Beside switch, because both are "go somewhere in the ref graph" and that is where the hand
        //will look. Its parameter is a *new* tag name, which is why the kind is Tag rather than
        //Branch: see ActionParameter.
        new("tag", VerbKind.Tag, 115, ActionSurfaces.All, "tag.ico", InMore: true,
            NeedsRepository: true, Parameter: ActionParameter.Tag),

        new("push", VerbKind.Push, 120, ActionSurfaces.All, "push.ico", InMore: true, NeedsRepository: true),

        //Immediately after push, because it is the next thing that happens to a branch and because
        //it *is* a push most of the time -- the flow publishes the branch before it opens anything.
        //Not a root entry: CLAUDE.md's two root entries are the two performed all day, and a pull
        //request is opened once per branch.
        new("pr", VerbKind.PullRequest, 125, ActionSurfaces.All, "pr.ico", InMore: true,
            NeedsRepository: true),

        //Next to push on purpose: "which origin does this go to" is the question that sends the user
        //looking for it, and the answer is one row of this window.
        new("repo", VerbKind.Repo, 130, ActionSurfaces.All, "status.ico", InMore: true,
            NeedsRepository: true),

        //Not on the palette: the palette lists repositories, and cloning is what you do when there
        //is not one yet.
        new("clone", VerbKind.Clone, 140, ActionSurfaces.Menu, "clone.ico", InMore: true),

        //No `status` entry, and 150 is left free for whatever wants it. `flick status` prints text,
        //and a menu or palette click has no console to print into -- so the entry fell back to the
        //commit window, which is the root entry above it under a second name. The verb stays; the
        //duplicate does not.

        //No repository requirement: a terminal in a folder is useful whatever the folder is.
        new("terminal", VerbKind.Terminal, 160, ActionSurfaces.All, "terminal.ico", InMore: true),
    ];

}
