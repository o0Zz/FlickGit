using System.Text.Json;
using FlickGit.Cli;
using FlickGit.Logging;

namespace FlickGit.Actions;

/// <summary>
/// Every action FlickGit can perform, defined once here and projected onto each surface -- the
/// context menu, the palette and the CLI must not each define their own list.
///
/// Built-ins ship in code and can be hidden, relabelled or reordered, never deleted. User actions
/// come from <c>actions.json</c>, which is a trust boundary: it can start arbitrary processes, so
/// a failure to read it leaves the built-ins working, and anything destructive in it is forced to
/// confirm whatever the file claims.
/// </summary>
public sealed class ActionCatalog
{
    /// <summary>Refused rather than migrated. A file from a future build is read by nothing.</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly string _filePath;
    private readonly Func<string, string> _localise;
    private readonly ILog _log;

    private IReadOnlyList<GitAction> _actions = [];

    /// <param name="filePath">
    /// Where <c>actions.json</c> lives. Passed in rather than derived, because <c>FlickGit.Core</c>
    /// deliberately knows nothing about Windows or about where a user profile is.
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
    /// Why <c>actions.json</c> was not used, or null when it was. Surfaced by `flick diag doctor`: a
    /// user action that silently stopped appearing is a bug report; one that says why is a typo.
    /// </summary>
    public string? LoadError { get; private set; }

    /// <summary>Everything, including hidden entries.</summary>
    public IReadOnlyList<GitAction> All => _actions;

    public IReadOnlyList<GitAction> For(ActionSurfaces surface) =>
        [.. _actions
            .Where(a => !a.Hidden && a.Surfaces.HasFlag(surface))
            .OrderBy(a => a.MenuOrder)];

    public GitAction? ById(string id) =>
        _actions.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Builds the list from the built-ins and the file. Never throws -- a broken file leaves
    /// <see cref="LoadError"/> set and the built-ins intact.
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
            //Named, with the reason, because the alternative is a user action that stopped existing for no
            //visible cause.
            LoadError = $"actions.json could not be read, so only the built-in actions are available: {ex.Message}";
            _log.Warn(LoadError);
            return null;
        }
    }

    /// <summary>
    /// Turns one file entry into an action, or drops it with a reason. Every rejection is logged: a
    /// user hand-editing this file needs to know which entry was wrong.
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
            //Refused rather than overriding: a user action that shadows "commit" would replace the product's
            //most safety-critical entry with an argument list from a file.
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

            //Defaulted past the built-ins rather than to zero, so an entry with no menuOrder lands after them
            //instead of ahead of Commit.
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
    /// Reads the <c>surfaces</c> list. An empty or entirely unrecognised list means both surfaces
    /// rather than neither: an action nobody can see is a worse answer to a typo than an action in
    /// one place too many.
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
                "folder" => ActionSurfaces.Folder,
                _ => ActionSurfaces.None,
            };
        }

        return surfaces == ActionSurfaces.None ? ActionSurfaces.All : surfaces;
    }

    /// <summary>
    /// Reduces whatever the file said to a bare file name, looked for in the install's own
    /// <c>icons\</c> directory and nowhere else. An icon path from a file must not be able to name a
    /// location outside it.
    /// </summary>
    private static string? FileNameOnly(string? icon) =>
        icon is { Length: > 0 } ? Path.GetFileName(icon) : null;

    /// <summary>
    /// One shipped action. <b>There is no <c>Cli</c> column</b>: a built-in's id <i>is</i> its verb
    /// spelling, so <see cref="GitAction.Cli"/> derives it.
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

                //A relabelled built-in keeps the user's words; everything else comes from the language file, so
                //switching language relabels it.
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
    /// Order values are strided in tens and the submenu entries continue past 100, so one number
    /// sorts the whole catalog. Explorer sorts submenu keys as <i>strings</i>, so every entry within
    /// one submenu has to have the same number of digits -- which the two ranges give for free.
    ///
    /// Every built-in is a <see cref="WindowRun"/>: one that ran Git directly would be a second path
    /// to an operation the verb already implements with its guardrails.
    /// </summary>
    private static readonly BuiltIn[] BuiltIns =
    [
        //Pull first: it is what you do on arriving at a repository and Commit is what you do on
        //leaving it, so the two root entries read in the order the day goes.
        new("pull-rebase", VerbKind.PullRebase, 10, ActionSurfaces.All, "pull.ico", NeedsRepository: true),
        //One pull entry, and it autostashes -- see PullService for why there is no plain one.

        new("commit", VerbKind.Commit, 20, ActionSurfaces.All, "commit.ico", NeedsRepository: true),

        //Everything else, in the submenu. Log first: the most-reached, and still not a root entry because
        //the two root entries are the two the user *performs* all day.
        new("log", VerbKind.Log, 105, ActionSurfaces.All, "log.ico", InMore: true, NeedsRepository: true),

        //Read what is there. File only: blaming a folder is not a thing, which is the example
        //ActionSurfaces.File's own doc gives for why the two clicks are separate surfaces.
        new("blame", VerbKind.Blame, 100, ActionSurfaces.File, "blame.ico", InMore: true, NeedsRepository: true),

        new("switch", VerbKind.Switch, 110, ActionSurfaces.All, "branch.ico", InMore: true,
            NeedsRepository: true, Parameter: ActionParameter.Branch),

        //Beside switch, because both are "go somewhere in the ref graph". Its parameter is a *new* tag
        //name, which is why the kind is Tag rather than Branch.
        new("tag", VerbKind.Tag, 115, ActionSurfaces.All, "tag.ico", InMore: true,
            NeedsRepository: true, Parameter: ActionParameter.Tag),

        //117, immediately after Tags: this is the third of the "one screen per kind of ref" set, and
        //the menu should read that way. Three digits, because Explorer sorts submenu keys as strings.
        //A submodule is a nested clone, which is why it borrows that icon rather than shipping a
        //twelfth one.
        //
        //NeedsRepository only, deliberately not gated on RepositoryInfo.HasSubmodules: this window is
        //where the *first* submodule is added, so hiding it in a repository with none hides the way in.
        new("submodule", VerbKind.Submodule, 117, ActionSurfaces.All, "clone.ico", InMore: true,
            NeedsRepository: true),

        //118, just past the three ref pickers and just short of Push -- the two things either side of
        //it are where the work goes, and this is where it waits. Not a ref, so it is not one of those
        //three; not a push, so it is not the next one.
        //
        //Its own icon rather than one borrowed from another entry: every other .ico in the folder is
        //already the picture of a different row in this same menu, and a Stashes row wearing the
        //commit or branch icon would read as a second way to do that.
        new("stash", VerbKind.Stash, 118, ActionSurfaces.All, "stash.ico", InMore: true,
            NeedsRepository: true),

        new("push", VerbKind.Push, 120, ActionSurfaces.All, "push.ico", InMore: true, NeedsRepository: true),

        new("pr", VerbKind.PullRequest, 125, ActionSurfaces.All, "pr.ico", InMore: true,
            NeedsRepository: true),

        new("repo", VerbKind.Repo, 130, ActionSurfaces.All, "status.ico", InMore: true,
            NeedsRepository: true),


        //Not on the palette: the palette lists repositories, and cloning is what you do when there is not
        //one yet.
        new("clone", VerbKind.Clone, 140, ActionSurfaces.Menu, "clone.ico", InMore: true),

        //No `status` entry, and 150 is left free. `flick status` prints text, and a menu or palette click
        //has no console to print into.

        //No repository requirement: a terminal in a folder is useful whatever the folder is.
        new("terminal", VerbKind.Terminal, 160, ActionSurfaces.All, "terminal.ico", InMore: true),

        //The two operations that put a path under Git's control or take it out again -- on a clicked
        //file, and on a clicked folder. Last, and `rm` last of the two: these are the only entries in
        //the submenu that act on something smaller than the repository.
        //
        //Neither deletes anything: `add` stages, and `rm` is `git rm --cached`, which takes the path
        //out of the index and leaves every file where it is. `Folder` rather than `Menu` is still what
        //keeps both off the folder background, the drive and the repository root -- one click on a
        //directory reaches everything under it, and Commit is already the entry that stages a whole
        //repository. See TrackingService for the rest of it.
        //
        //One entry each carrying both surfaces, not two entries: a built-in's id *is* its CLI verb,
        //so `add` can only be spelled once.
        new("add", VerbKind.Add, 170, ActionSurfaces.File | ActionSurfaces.Folder, "add.ico",
            InMore: true, NeedsRepository: true),
        new("rm", VerbKind.Remove, 180, ActionSurfaces.File | ActionSurfaces.Folder, "remove.ico",
            InMore: true, NeedsRepository: true),
    ];

}
