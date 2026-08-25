namespace FlickGit.Cli;

/// <summary>
/// Process exit codes. A contract, not an implementation detail: scripts and launchers drive the
/// same actions Explorer does, and they can only branch on the number.
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int GitError = 1;
    public const int NotARepository = 2;
    public const int UserCancelled = 3;
    public const int ConfigurationError = 4;

    /// <summary>Refused for safety: a blocked switch, a diverged push.</summary>
    public const int RefusedForSafety = 5;
}

/// <summary>The actions reachable from every surface.</summary>
public enum VerbKind
{
    /// <summary>No verb: start resident, show the tray icon, open nothing.</summary>
    Tray,

    Commit,
    PullRebase,
    Push,
    Switch,
    Status,

    Log,

    /// <summary>The blame window. Its path is a <b>file</b>, not a directory.</summary>
    Blame,

    /// <summary>
    /// <c>git add</c> on one file, which for a file Git has never seen is what starts tracking it.
    /// A <b>file</b> path, like <see cref="Blame"/> and <see cref="Remove"/>.
    /// </summary>
    Add,

    /// <summary>
    /// <c>git rm</c> on one file: gone from the working tree, and the deletion staged.
    ///
    /// Spelled <c>rm</c> rather than <c>remove</c> or <c>delete</c>, because it is <i>exactly</i>
    /// <c>git rm</c> and a second word for it would be a second thing to remember. It asks before it
    /// runs, on every surface — see <c>RepositoryVerbs</c> — which is what CLAUDE.md's "explicit user
    /// intent, expressed in the moment" means for a verb a script can also reach.
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

/// <param name="Path">The repository or folder it applies to. Defaults to the working directory.</param>
/// <param name="Argument">The optional second token: a branch for `switch`, a URL for `clone`.</param>
/// <param name="Error">Set when the command line could not be understood. Nothing else is valid then.</param>
public sealed record Verb(VerbKind Kind, string? Path, string? Argument, string? Error = null)
{
    /// <summary>
    /// Parses <c>flick &lt;verb&gt; [path] [argument]</c>. Hand-rolled: a command-line library would be
    /// another assembly to load before the first window appears, and the grammar is one verb plus at
    /// most two positional arguments.
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
            "push" => VerbKind.Push,
            "switch" => VerbKind.Switch,
            "tag" => VerbKind.Tag,
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

        string? path = args.Count > 1 ? args[1] : null;
        string? argument = args.Count > 2 ? args[2] : null;

        //Explorer hands over "%V", which for a drive root arrives as `C:\` -- and a trailing backslash
        //before a closing quote is why that would otherwise reach here as `C:"`.
        path = path?.Trim().Trim('"');

        if (string.IsNullOrWhiteSpace(path))
            path = null;

        return new Verb(kind.Value, path ?? DefaultPathFor(kind.Value, fallbackPath), argument);
    }

    private static string? DefaultPathFor(VerbKind kind, string fallbackPath) =>
        kind switch
        {
            //The path-less verbs. Defaulting them to the working directory would make `flick settings` look
            //like it applies to a repository.
            VerbKind.Tray or VerbKind.Palette or VerbKind.Settings or VerbKind.Help
                or VerbKind.Version or VerbKind.InstallShell or VerbKind.UninstallShell
                or VerbKind.Autostart or VerbKind.Ai or VerbKind.Language
                or VerbKind.DiagTimings or VerbKind.DiagDoctor => null,

            _ => fallbackPath,
        };

    public const string HelpText = """
        flick — fast Git actions from Windows Explorer and the command line.

          flick commit <path>                 commit window
          flick pull-rebase <path>            pull --rebase --autostash (+ submodules)
          flick push <path>
          flick pr <path>                     open a pull request for this branch
          flick switch <path> [branch]        branch picker when omitted
          flick tag <path> [name]             tag picker when omitted, else creates it
          flick status <path>
          flick log <path>                    commit history; multi-select for a combined diff
          flick blame <file>                  who last touched each line, and what was there before
          flick add <file>                    stage one file, tracking it if it is new
          flick rm <file>                     delete one file and stage the deletion; asks first
          flick repo <path>                   the identity it commits as, its remotes, its defaults
          flick submodule <path>              submodules: add, remove, initialise
          flick terminal <path>               open a terminal there
          flick clone <path> [url]
          flick run <id> [path]               run a catalog action by id
          flick palette                       repository palette
          flick settings                      settings, help and about
          flick install-shell                 register the Explorer context menu
          flick uninstall-shell
          flick autostart [on|off]            start the resident service at logon
          flick ai                            what the AI is configured to do
          flick ai key [set|clear]            store or remove the API key
          flick language [code|auto]          interface language; lists them when omitted
          flick diag timings                  recent latency measurements
          flick diag doctor                   environment health check

        <path> defaults to the current directory.

        Exit codes: 0 ok · 1 git error · 2 not a repository · 3 cancelled
                    4 configuration error · 5 refused for safety
        """;
}
