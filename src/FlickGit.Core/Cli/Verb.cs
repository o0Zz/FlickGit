namespace FlickGit.Cli;

/// <summary>
/// Process exit codes, fixed by CLAUDE.md, "Command Line Interface".
///
/// These are a contract, not an implementation detail: the whole point of the CLI is that
/// scripts, PowerToys Run and future integrations can drive the same actions Explorer
/// does, and they can only branch on the number.
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

/// <summary>The actions reachable from every surface. Phase 1 implements a subset; the rest are declared so `flick --help` is honest about what exists.</summary>
public enum VerbKind
{
    /// <summary>No verb: start resident, show the tray icon, open nothing.</summary>
    Tray,

    Commit,
    PullRebase,
    Push,
    Switch,
    Status,

    /// <summary>The log window: commit history, and the combined diff over a selected range.</summary>
    Log,

    /// <summary>The blame window. Its path is a <b>file</b>, not a directory.</summary>
    Blame,

    Clone,

    /// <summary>
    /// Tags: the picker when no name is given, otherwise create that one.
    ///
    /// <b>Deletion has no command-line spelling on purpose.</b> `flick tag &lt;path&gt; v1.0` creates,
    /// which cannot overwrite anything — there is no `--force` anywhere in <c>TagService</c>. Deleting
    /// a published tag is on the far side of CLAUDE.md's "any destructive operation requires explicit
    /// user intent, expressed in the moment", and a flag in a script is the opposite of in the moment.
    /// The window asks, so the window is where it lives.
    /// </summary>
    Tag,

    /// <summary>
    /// The repository window: the identity it commits as, its remotes, and FlickGit's own
    /// per-repository defaults.
    ///
    /// <c>repo</c> rather than <c>config</c>, because <c>flick settings</c> is already FlickGit's own
    /// configuration and two verbs a token apart from meaning opposite things is a grammar nobody
    /// remembers.
    /// </summary>
    Repo,

    /// <summary>Opens a terminal at the folder. Present in the menu since Phase 1.</summary>
    Terminal,
    Palette,
    Settings,
    InstallShell,
    UninstallShell,

    /// <summary>Registers or removes the logon task that starts the resident service.</summary>
    Autostart,

    /// <summary>Reports the AI configuration, or stores and clears the API key.</summary>
    Ai,

    /// <summary>Lists the interface languages, or switches to one.</summary>
    Language,
    /// <summary>
    /// Runs a catalog action by id: <c>flick run custom.fetch-prune &lt;path&gt;</c>.
    ///
    /// The CLI half of the Action Catalog, and the only way a user action can reach the Explorer
    /// context menu — a registry verb is a command line, so an action with no verb of its own needs
    /// one that names it. Built-ins do not need this: their id <i>is</i> their verb, which is what
    /// makes <c>flick commit</c> and the Commit action the same thing.
    /// </summary>
    RunAction,

    DiagTimings,
    DiagDoctor,
    Help,
    Version,
}

/// <summary>
/// A parsed command line.
/// </summary>
/// <param name="Kind">Which action.</param>
/// <param name="Path">The repository or folder it applies to. Defaults to the working directory.</param>
/// <param name="Argument">The optional second token: a branch for `switch`, a URL for `clone`.</param>
/// <param name="Error">Set when the command line could not be understood. Nothing else is valid then.</param>
public sealed record Verb(VerbKind Kind, string? Path, string? Argument, string? Error = null)
{
    /// <summary>
    /// Parses <c>flick &lt;verb&gt; [path] [argument]</c>.
    ///
    /// Kept deliberately hand-rolled. A command-line library would be another assembly to
    /// load before the first window appears, and the grammar is one verb plus at most two
    /// positional arguments. CLAUDE.md: "&lt;path&gt; defaults to the current working
    /// directory when omitted."
    /// </summary>
    /// <param name="workingDirectory">
    /// What <c>&lt;path&gt;</c> defaults to when omitted. Passed in rather than read from the
    /// environment, because a request arriving over the pipe carries the <i>stub's</i> directory and
    /// the resident service's own is wherever it was started at logon.
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

        //`run` takes the action id first and the path second, which is the opposite way round from
        //every other verb -- so it is resolved here rather than bent into the positional grammar.
        if (head.Equals("run", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count < 2 || args[1].Trim().Length == 0)
                return new Verb(VerbKind.Help, null, null, "'run' needs an action id, as in 'flick run custom.fetch-prune'.");

            string? target = args.Count > 2 ? args[2].Trim().Trim('"') : null;

            if (string.IsNullOrWhiteSpace(target))
                target = fallbackPath;

            return new Verb(VerbKind.RunAction, target, args[1].Trim());
        }

        //`autostart on` and `ai key set` put their sub-tokens where `path` and `argument`
        //normally go, which the positional grammar handles without a special case: those verbs read
        //args[1] and args[2] themselves.
        VerbKind? kind = head.ToLowerInvariant() switch
        {
            "commit" => VerbKind.Commit,
            "pull-rebase" => VerbKind.PullRebase,
            "push" => VerbKind.Push,
            "switch" => VerbKind.Switch,
            "tag" => VerbKind.Tag,
            "status" => VerbKind.Status,
            "log" => VerbKind.Log,
            "blame" => VerbKind.Blame,
            "repo" => VerbKind.Repo,
            "terminal" => VerbKind.Terminal,
            "clone" => VerbKind.Clone,
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

        //Explorer hands over "%V", which for a drive root arrives as `C:\` -- and a
        //trailing backslash before a closing quote is why that would otherwise reach here
        //as `C:"`. Trimming quotes here rather than at every call site keeps that one
        //quirk in one place.
        path = path?.Trim().Trim('"');

        if (string.IsNullOrWhiteSpace(path))
            path = null;

        return new Verb(kind.Value, path ?? DefaultPathFor(kind.Value, fallbackPath), argument);
    }

    private static string? DefaultPathFor(VerbKind kind, string fallbackPath) =>
        kind switch
        {
            //The path-less verbs. Defaulting them to the working directory would make
            //`flick settings` look like it applies to a repository.
            VerbKind.Tray or VerbKind.Palette or VerbKind.Settings or VerbKind.Help
                or VerbKind.Version or VerbKind.InstallShell or VerbKind.UninstallShell
                or VerbKind.Autostart or VerbKind.Ai or VerbKind.Language
                or VerbKind.DiagTimings or VerbKind.DiagDoctor => null,

            _ => fallbackPath,
        };

    /// <summary>The help text, printed by the CLI stub and by `flick help`.</summary>
    public const string HelpText = """
        flick — fast Git actions from Windows Explorer and the command line.

          flick commit <path>                 commit window
          flick pull-rebase <path>            pull --rebase --autostash (+ submodules)
          flick push <path>
          flick switch <path> [branch]        branch picker when omitted
          flick tag <path> [name]             tag picker when omitted, else creates it
          flick status <path>
          flick log <path>                    commit history; multi-select for a combined diff
          flick blame <file>                  who last touched each line, and what was there before
          flick repo <path>                   the identity it commits as, its remotes, its defaults
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
