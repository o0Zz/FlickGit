namespace FlickGit.Cli;

//ExitCodes lives in src/Shared/ExitCodes.cs, not here: the AOT stub exits with the same numbers and
//may not reference this assembly. See that file.

/// <summary>The actions reachable from every surface.</summary>
public enum VerbKind
{
    /// <summary>No verb: start resident, show the tray icon, open nothing.</summary>
    Tray,

    Commit,
    PullRebase,

    /// <summary>
    /// Switch to the branch this repository treats as primary, then <c>pull --rebase</c> on it — the
    /// end of a feature branch, in one gesture.
    ///
    /// <b>The branch is <c>BranchService.ResolvePrimaryBranchAsync</c>'s answer, and there is no
    /// token for it.</b> A second positional argument would make this a slower spelling of
    /// <see cref="Switch"/> with a pull glued on, and the whole point is that the user does not have
    /// to say where "back" is. <c>flick switch &lt;path&gt; develop</c> is still there for anyone who
    /// wants to name it.
    /// </summary>
    Back,

    Push,
    Switch,
    Status,

    Log,

    /// <summary>The blame window. Its path is a <b>file</b>, not a directory.</summary>
    Blame,

    /// <summary>
    /// <c>git add</c> on the selection, which for anything Git has never seen is what starts tracking
    /// it. Files or folders, unlike <see cref="Blame"/>, and it asks nothing either way: staging
    /// discards nothing. It is also the way back from <see cref="Remove"/>, which leaves the file on
    /// disk for this to pick up again.
    ///
    /// <b>The two verbs that take a path list.</b> Explorer hands over every item that was selected,
    /// and acting on the first one and silently dropping the rest is what this used to do.
    /// </summary>
    Add,

    /// <summary>
    /// <c>git rm --cached</c> on the selection: out of the index, and <b>every file left exactly where
    /// it is</b>. Files or folders — <c>-r</c> covers a folder and <c>--cached</c> is what keeps the
    /// flag away from the working tree.
    ///
    /// <b>It destroys nothing, so it asks nothing.</b> Each path becomes a staged deletion beside the
    /// untracked file it left behind, and <see cref="Add"/> on the same path is the way back.
    ///
    /// Spelled <c>rm</c> rather than <c>remove</c> or <c>delete</c>, because it is <i>exactly</i>
    /// <c>git rm</c> and a second word for it would be a second thing to remember. What it is not is a
    /// delete: a path Git has nothing under is reported rather than removed, because Explorer's own
    /// Delete is the thing that removes a file.
    /// </summary>
    Remove,

    Clone,

    /// <summary>
    /// Spelled <c>pr</c>, with no alias for the long form: two spellings of one verb is a grammar with
    /// a right answer and a tolerated one.
    /// </summary>
    PullRequest,

    /// <summary>
    /// Tags: the picker when no name is given, otherwise create that one.
    ///
    /// <b>Deletion has no command-line spelling on purpose.</b> Creating cannot overwrite anything --
    /// there is no <c>--force</c> anywhere in <c>TagService</c> -- while deleting a published tag
    /// needs intent expressed in the moment, and a flag in a script is the opposite of that.
    /// </summary>
    Tag,

    /// <summary>
    /// The submodules window: what is declared, what is checked out, what has moved.
    ///
    /// <b>The picker and nothing else -- there is no second token.</b> Adding takes a URL <i>and</i>
    /// a path, and neither is the obvious one to put in the single positional slot; removing needs
    /// intent expressed in the moment, the same reason <see cref="Tag"/> gives for having no
    /// deletion spelling.
    /// </summary>
    Submodule,

    /// <summary>
    /// The stash: the window when nothing follows, otherwise put the working tree away under that
    /// message.
    ///
    /// <b>Popping and dropping have no command-line spelling</b>, and for the two halves of the reason
    /// <see cref="Tag"/> gives. A stash cannot overwrite anything, so creating one from a script is
    /// safe; but both of the others name an existing stash by a reflog selector, and a selector in a
    /// script is a position that will have moved by the time it runs -- which is the one mistake this
    /// feature is built to make impossible.
    /// </summary>
    Stash,

    /// <summary>
    /// <c>repo</c> rather than <c>config</c>, because <c>flick settings</c> is already FlickGit's own
    /// configuration and two verbs a token apart from meaning opposite things is a grammar nobody
    /// remembers.
    /// </summary>
    Repo,

    Terminal,
    Palette,
    Settings,
    InstallShell,
    UninstallShell,

    /// <summary>
    /// Registers or removes the icon overlay Explorer draws on a repository folder.
    ///
    /// Separate from <see cref="InstallShell"/> and not folded into it, because this is the one
    /// operation in the product that writes to <c>HKLM</c> and therefore the one that can prompt for
    /// administrator rights. The installer never runs it: the overlay is something the user turns on.
    /// </summary>
    InstallOverlay,
    UninstallOverlay,

    Autostart,

    Ai,

    Language,
    /// <summary>
    /// Runs a catalog action by id. The only way a user action can reach the Explorer context menu --
    /// a registry verb is a command line, so an action with no verb of its own needs one that names
    /// it. Built-ins do not need this: their id <i>is</i> their verb.
    /// </summary>
    RunAction,

    DiagTimings,
    DiagDoctor,
    Help,
    Version,
}

/// <param name="Path">
/// The repository or folder it applies to. Defaults to the working directory. For the two verbs that
/// take a selection this is its first entry, so repository resolution has one path to work from
/// whatever the verb.
/// </param>
/// <param name="Argument">The optional second token: a branch for `switch`, a URL for `clone`.</param>
/// <param name="Error">Set when the command line could not be understood. Nothing else is valid then.</param>
public sealed record Verb(VerbKind Kind, string? Path, string? Argument, string? Error = null)
{
    private readonly IReadOnlyList<string>? _paths;

    /// <summary>
    /// Every path the verb applies to.
    ///
    /// <b>For all but two verbs that is exactly <see cref="Path"/></b>, which is why the default is
    /// derived rather than written out at a dozen construction sites: <c>commit</c> and <c>blame</c>
    /// and the rest apply to one thing, and a list there would be a second spelling of the same fact.
    /// <c>add</c> and <c>rm</c> set it, because Explorer hands them a selection.
    ///
    /// Empty means the selection was <i>refused</i> rather than absent — see the <c>--too-many</c>
    /// spelling in <c>Parse</c>. It never means "so use the working directory": that default is
    /// applied while parsing, where the difference between "no path given" and "too many to carry" is
    /// still known.
    /// </summary>
    public IReadOnlyList<string> Paths
    {
        get => _paths ?? (Path is null ? [] : [Path]);
        init => _paths = value;
    }

    /// <summary>
    /// Parses <c>flick &lt;verb&gt; [path] [argument]</c>, or <c>flick &lt;verb&gt; &lt;path&gt;...</c>
    /// for the two that take a selection. Hand-rolled: a command-line library would be another assembly
    /// to load before the first window appears, and the grammar is one verb plus its positional
    /// arguments.
    /// </summary>
    /// <param name="workingDirectory">
    /// What <c>&lt;path&gt;</c> defaults to. Passed in rather than read from the environment, because a
    /// request arriving over the pipe carries the <i>stub's</i> directory and the resident service's
    /// own is wherever it was started at logon.
    /// </param>
    public static Verb Parse(IReadOnlyList<string> args, string? workingDirectory = null)
    {
        string fallbackPath = workingDirectory ?? Environment.CurrentDirectory;

        if (args.Count == 0)
            return new Verb(VerbKind.Tray, null, null);

        string head = args[0].Trim();

        //`diag` takes a subcommand, so it is resolved before the flat table below.
        if (head.Equals("diag", StringComparison.OrdinalIgnoreCase))
        {
            string sub = args.Count > 1 ? args[1].Trim().ToLowerInvariant() : string.Empty;
            return sub switch
            {
                "timings" => new Verb(VerbKind.DiagTimings, null, null),
                "doctor" => new Verb(VerbKind.DiagDoctor, null, null),
                _ => new Verb(VerbKind.Help, null, null, $"Unknown diag subcommand '{sub}'. Try 'timings' or 'doctor'."),
            };
        }

        //`run` takes the action id first and the path second, the opposite way round from every other
        //verb -- so it is resolved here rather than bent into the positional grammar.
        if (head.Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count < 2 || args[1].Trim().Length == 0)
                return new Verb(VerbKind.Help, null, null, "'run' needs an action id, as in 'flick run custom.fetch-prune'.");

            string? target = args.Count > 2 ? args[2].Trim().Trim('"') : null;

            if (string.IsNullOrWhiteSpace(target))
                target = fallbackPath;

            return new Verb(VerbKind.RunAction, target, args[1].Trim());
        }

        //`autostart on` and `ai key set` put their sub-tokens where `path` and `argument` normally go,
        //which the positional grammar handles without a special case.
        VerbKind? kind = head.ToLowerInvariant() switch
        {
            "commit" => VerbKind.Commit,
            "pull-rebase" => VerbKind.PullRebase,
            "back" => VerbKind.Back,
            "push" => VerbKind.Push,
            "switch" => VerbKind.Switch,
            "tag" => VerbKind.Tag,
            "stash" => VerbKind.Stash,
            "submodule" => VerbKind.Submodule,
            "status" => VerbKind.Status,
            "log" => VerbKind.Log,
            "blame" => VerbKind.Blame,
            "add" => VerbKind.Add,
            "rm" => VerbKind.Remove,
            "repo" => VerbKind.Repo,
            "terminal" => VerbKind.Terminal,
            "clone" => VerbKind.Clone,
            "pr" => VerbKind.PullRequest,
            "palette" => VerbKind.Palette,
            "settings" => VerbKind.Settings,
            "install-shell" => VerbKind.InstallShell,
            "uninstall-shell" => VerbKind.UninstallShell,
            "install-overlay" => VerbKind.InstallOverlay,
            "uninstall-overlay" => VerbKind.UninstallOverlay,
            "autostart" => VerbKind.Autostart,
            "ai" => VerbKind.Ai,
            "language" => VerbKind.Language,
            "tray" => VerbKind.Tray,
            "help" or "--help" or "-h" or "/?" => VerbKind.Help,
            "version" or "--version" or "-v" => VerbKind.Version,
            _ => null,
        };

        if (kind is null)
            return new Verb(VerbKind.Help, null, null, $"Unknown command '{head}'.");

        //`add` and `rm` act on a selection, so every trailing token is a path. Every other verb keeps
        //`args[2]` for its own second token -- a branch for `switch`, a name for `tag`, a message for
        //`stash` -- which is why this is a switch on the kind and not a rule about trailing arguments.
        //Made general, `flick tag . v1.0` would read the tag name as a second path.
        if (kind.Value is VerbKind.Add or VerbKind.Remove)
            return Selection(kind.Value, args, fallbackPath);

        string? path = args.Count > 1 ? args[1] : null;
        string? argument = args.Count > 2 ? args[2] : null;

        path = path is null ? null : NormalisePath(path);

        if (string.IsNullOrWhiteSpace(path))
            path = null;

        return new Verb(kind.Value, path ?? DefaultPathFor(kind.Value, fallbackPath), argument);
    }

    /// <summary>
    /// <c>flick add &lt;path&gt;...</c> and <c>flick rm &lt;path&gt;...</c>, whose whole tail is a path
    /// list.
    ///
    /// <b><c>--too-many</c> is the shell handler saying the selection would not fit on a command
    /// line</b>, and it arrives carrying the count and no paths at all. A truncated list is the one
    /// answer that must never reach a removal, so the handler sends none rather than some — see
    /// <c>Launcher</c> for the budget, and <c>VerbRunner</c>, which refuses this by name before it
    /// resolves a repository.
    /// </summary>
    private static Verb Selection(VerbKind kind, IReadOnlyList<string> args, string fallbackPath)
    {
        if (args.Count > 1 && args[1].Trim().Equals("--too-many", StringComparison.OrdinalIgnoreCase))
            return new Verb(kind, null, args.Count > 2 ? args[2].Trim() : null) { Paths = [] };

        List<string> paths = [];

        for (int i = 1; i < args.Count; i++)
        {
            //The same normalisation the single path has, per entry.
            string one = NormalisePath(args[i]);

            if (one.Length > 0)
                paths.Add(one);
        }

        //No path given at all is the working directory, exactly as for every other verb. Applied here
        //rather than left to an empty list, because empty now means something else entirely.
        if (paths.Count == 0)
            paths.Add(fallbackPath);

        return new Verb(kind, paths[0], null) { Paths = paths };
    }

    /// <summary>
    /// One path as it arrives from Explorer or a shell.
    ///
    /// <b>Both halves are about the drive root, and neither is cosmetic.</b> Explorer hands over
    /// <c>%V</c>, which for a drive root is <c>C:\</c>, and the trailing backslash before the closing
    /// quote escapes it -- so the token would reach here as <c>C:"</c>. The shell handler strips that
    /// backslash to avoid it, which leaves <c>C:</c>, and <b><c>C:</c> is not the root of the
    /// drive</b>: it is the drive-<i>relative</i> path, meaning whichever directory happens to be
    /// current on C: for the process that resolves it. <c>Path.GetFullPath("C:")</c> in the resident
    /// service therefore answers with the service's own directory, and a right-click on a drive root
    /// commits, logs, pushes or pulls somewhere else entirely.
    ///
    /// Here rather than in a verb, because every verb takes a path and only one of them used to put
    /// the separator back.
    /// </summary>
    private static string NormalisePath(string path)
    {
        string trimmed = path.Trim().Trim('"').Trim();

        //Windows only. Off it there are no drives, and "C:" is an ordinary two-character directory
        //name -- appending a separator there would rewrite a path the user meant literally.
        return OperatingSystem.IsWindows()
               && trimmed.Length == 2 && trimmed[1] == ':' && char.IsLetter(trimmed[0])
            ? trimmed + System.IO.Path.DirectorySeparatorChar
            : trimmed;
    }

    private static string? DefaultPathFor(VerbKind kind, string fallbackPath) =>
        kind switch
        {
            //The path-less verbs. Defaulting them to the working directory would make `flick settings` look
            //like it applies to a repository.
            VerbKind.Tray or VerbKind.Palette or VerbKind.Settings or VerbKind.Help
                or VerbKind.Version or VerbKind.InstallShell or VerbKind.UninstallShell
                or VerbKind.InstallOverlay or VerbKind.UninstallOverlay
                or VerbKind.Autostart or VerbKind.Ai or VerbKind.Language
                or VerbKind.DiagTimings or VerbKind.DiagDoctor => null,

            _ => fallbackPath,
        };

    public const string HelpText = """
        flick — fast Git actions from Windows Explorer and the command line.

          flick commit <path>                 commit window
          flick pull-rebase <path>            pull --rebase --autostash (+ submodules)
          flick back <path>                   switch to the primary branch, then pull
          flick push <path>
          flick pr <path>                     open a pull request for this branch
          flick switch <path> [branch]        branch picker when omitted
          flick tag <path> [name]             tag picker when omitted, else creates it
          flick stash <path> [message]        stash window when omitted, else stashes your changes
          flick status <path>
          flick log <path>                    commit history; multi-select for a combined diff
          flick blame <file>                  who last touched each line, and what was there before
          flick add <path>...                 stage files or folders, tracking what is new
          flick rm <path>...                  delete files or folders and stage the deletions; asks first
          flick repo <path>                   the identity it commits as, its remotes, its defaults
          flick submodule <path>              submodules: add, remove, initialise
          flick terminal <path>               open a terminal there
          flick clone <path> [url]
          flick run <id> [path]               run a catalog action by id
          flick palette                       repository palette
          flick settings                      settings, help and about
          flick install-shell                 register the Explorer context menu
          flick uninstall-shell
          flick install-overlay [system]      badge repository folders in Explorer (asks for admin)
          flick uninstall-overlay [system]
          flick autostart [on|off]            start the resident service at logon
          flick ai                            what the AI is configured to do
          flick ai key [set|clear]            store or remove the API key
          flick language [code|auto]          interface language; lists them when omitted
          flick diag timings                  recent latency measurements
          flick diag doctor                   environment health check
          flick version                       the build, also --version and -v
          flick help                          this list, also --help, -h and /?

        <path> defaults to the current directory.

        Exit codes: 0 ok · 1 git error · 2 not a repository · 3 cancelled
                    4 configuration error · 5 refused for safety
        """;
}
