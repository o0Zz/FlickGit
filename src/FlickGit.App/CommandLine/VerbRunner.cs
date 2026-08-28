using FlickGit.Actions;
using FlickGit.App.Localization;
using FlickGit.App.Resident;
using FlickGit.Cli;
using FlickGit.Git;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;

namespace FlickGit.App.CommandLine;

/// <summary>
/// What a verb wants to happen to the process afterwards.
/// </summary>
/// <param name="Code">The exit code, whether it is acted on now or when the last window closes.</param>
/// <param name="ShutDown">
/// True to end the process now. False when a window is up: the code is remembered, and WPF ends the
/// process when the user closes it.
/// </param>
public readonly record struct VerbResult(int Code, bool ShutDown)
{
    public static VerbResult Exit(int code) => new(code, ShutDown: true);

    /// <summary>A window is on screen. The process outlives this call.</summary>
    public static VerbResult Stay(int code = ExitCodes.Success) => new(code, ShutDown: false);
}

/// <summary>
/// Turns a parsed command line into one action.
///
/// This is the whole of the routing: which verbs need a repository resolved first, which answer
/// with text and which open a window. Keeping it in one small class is what stops that knowledge
/// from being spread across a dozen methods that each re-derive it.
///
/// A singleton. Everything that varies between two invocations — the verb, and where the answer
/// goes — arrives as a parameter to <see cref="RunAsync"/>, so there is no per-request state here
/// to leak from one right-click into the next.
/// </summary>
public sealed class VerbRunner(
    ActionCatalog catalog,
    ActionRunner actions,
    RepositoryVerbs repositoryVerbs,
    EnvironmentVerbs environmentVerbs,
    WindowVerbs windowVerbs,
    RepositoryService repositories,
    RecentRepositories recent,
    ILog log)
{
    /// <summary>
    /// The verbs that operate on a repository, and so cannot start without one.
    ///
    /// `clone` is not here on purpose: it wants a folder that is <i>not</i> a repository. Neither is
    /// `terminal`, which works anywhere.
    /// </summary>
    private static bool NeedsRepository(VerbKind kind) =>
        kind is VerbKind.Commit or VerbKind.Status or VerbKind.PullRebase
            or VerbKind.Switch or VerbKind.Push or VerbKind.Tag or VerbKind.Log
            or VerbKind.Blame or VerbKind.Add or VerbKind.Remove
            or VerbKind.Repo or VerbKind.PullRequest
            or VerbKind.Submodule or VerbKind.Stash;

    /// <summary>
    /// Runs a catalog action by id.
    ///
    /// The repository is resolved here rather than by <see cref="NeedsRepository"/>, because whether
    /// one is needed depends on the action: <c>terminal</c> works anywhere, and <c>clone</c> wants a
    /// folder that is <i>not</i> a repository. An action that declares it needs one and does not get
    /// one is refused with the reason.
    /// </summary>
    private async Task<VerbResult> RunActionAsync(VerbOutput output, string? id, string? path)
    {
        if (id is null || catalog.ById(id) is not { } action)
        {
            output.Fail(Strings.Get("app.name"), $"There is no action with the id '{id}'.");
            return VerbResult.Exit(ExitCodes.ConfigurationError);
        }

        RepositoryInfo? resolved = path is null
            ? null
            : await repositories.ResolveAsync(path, CancellationToken.None).ConfigureAwait(true);

        if (action.RequiresRepository && resolved is null)
        {
            output.Fail(action.Label, Strings.Get("error.notarepository", path ?? string.Empty));
            return VerbResult.Exit(ExitCodes.NotARepository);
        }

        //The folder itself when it is not a repository, so `clone` and `terminal` still have somewhere
        //to run. Root carries it either way, which is all an action needs.
        RepositoryInfo repository = resolved
            ?? new RepositoryInfo(
                path ?? string.Empty,
                string.Empty,
                HasSubmodules: false,
                IsBare: false,
                //There is no Git directory, because there is no repository. Empty is the honest
                //answer and every reader takes it as "nothing in progress".
                GitDirectory: string.Empty);

        await actions.RunAsync(action, repository, output).ConfigureAwait(true);

        //A window action stays; everything else has finished by the time this returns.
        return action.Run is WindowRun ? VerbResult.Stay() : VerbResult.Exit(ExitCodes.Success);
    }

    /// <param name="output">
    /// Where the answer goes. A direct launch prints to its own console; a pipe request collects the
    /// text for the response instead.
    /// </param>
    public async Task<VerbResult> RunAsync(Verb verb, VerbOutput output)
    {
        try
        {
            if (verb.Error is not null)
            {
                output.Fail(Strings.Get("app.name"), verb.Error);
                output.Line();
                output.Line(Verb.HelpText);
                return VerbResult.Exit(ExitCodes.ConfigurationError);
            }

            //A selection the command line could not carry. Answered here, before the repository is
            //resolved, because there is deliberately nothing to resolve one *from*: the shell handler
            //sent the count instead of a shortened list, and a path defaulted in from the working
            //directory would turn "too many files" into an operation on whatever directory this
            //process happens to have started in.
            //
            //Exit 5 rather than 4. Nothing was acted on, and refusing rather than truncating is the
            //safety property itself -- a removal carrying the first four hundred of five hundred
            //selected files is a removal the user never asked for.
            if (verb.Kind is VerbKind.Add or VerbKind.Remove && verb.Paths.Count == 0)
            {
                output.Fail(
                    Strings.Get(verb.Kind is VerbKind.Add ? "action.add" : "action.rm"),
                    Strings.Get("selection.toomany", verb.Argument ?? "?"));

                return VerbResult.Exit(ExitCodes.RefusedForSafety);
            }

            //Resolved once, here, rather than at the top of every verb. A path that is not in a
            //repository is reported the same way whatever was asked of it.
            RepositoryInfo? repository = null;

            if (NeedsRepository(verb.Kind))
            {
                repository = await ResolveAsync(output, verb.Path!).ConfigureAwait(true);

                if (repository is null)
                    return VerbResult.Exit(ExitCodes.NotARepository);
            }

            return await RouteAsync(verb, output, repository).ConfigureAwait(true);
        }
        catch (GitNotFoundException ex)
        {
            //Its message already says what to install and where to set the path.
            log.Error(ex.Message);
            output.Fail(Strings.Get("error.gitmissing"), ex.Message);
            return VerbResult.Exit(ExitCodes.ConfigurationError);
        }
        catch (Exception ex)
        {
            log.Error($"{verb.Kind} failed: {ex}");
            output.Fail(Strings.Get("error.title"), ex.Message);
            return VerbResult.Exit(ExitCodes.GitError);
        }
    }

    private async Task<VerbResult> RouteAsync(Verb verb, VerbOutput output, RepositoryInfo? repository) =>
        verb.Kind switch
        {
            VerbKind.Help => environmentVerbs.Help(output),
            VerbKind.Version => environmentVerbs.Version(output),
            VerbKind.InstallShell => environmentVerbs.ContextMenu(output, install: true),
            VerbKind.UninstallShell => environmentVerbs.ContextMenu(output, install: false),
            VerbKind.InstallOverlay => await environmentVerbs.OverlayAsync(output, install: true, verb.Path).ConfigureAwait(true),
            VerbKind.UninstallOverlay => await environmentVerbs.OverlayAsync(output, install: false, verb.Path).ConfigureAwait(true),
            VerbKind.Autostart => environmentVerbs.Autostart(output, verb.Path),
            VerbKind.Ai => await environmentVerbs.AiAsync(output, verb.Path, verb.Argument).ConfigureAwait(true),
            VerbKind.DiagDoctor => await environmentVerbs.DoctorAsync(output).ConfigureAwait(true),
            VerbKind.DiagTimings => environmentVerbs.Timings(output),
            VerbKind.Settings => environmentVerbs.Settings(output),
            VerbKind.Language => environmentVerbs.Language(output, verb.Path),

            VerbKind.Commit => await windowVerbs.CommitAsync(output, repository!).ConfigureAwait(true),
            VerbKind.Palette => await windowVerbs.PaletteAsync().ConfigureAwait(true),
            VerbKind.RunAction => await RunActionAsync(output, verb.Argument, verb.Path).ConfigureAwait(true),
            VerbKind.Clone => windowVerbs.Clone(verb.Path!, verb.Argument),
            VerbKind.Terminal => windowVerbs.Terminal(output, verb.Path),

            VerbKind.PullRebase => await windowVerbs.PullAsync(repository!).ConfigureAwait(true),
            VerbKind.Log => await windowVerbs.LogAsync(repository!).ConfigureAwait(true),
            VerbKind.Repo => windowVerbs.Repo(repository!),
            VerbKind.Submodule => windowVerbs.Submodules(repository!),
            VerbKind.PullRequest => await windowVerbs.PullRequestAsync(repository!).ConfigureAwait(true),

            //`verb.Path` is the clicked *file*, which routing carries through untouched --
            //`repository` was resolved from its directory.
            VerbKind.Blame => await windowVerbs.BlameAsync(output, repository!, verb.Path!).ConfigureAwait(true),

            //The other two path verbs, and the only two carrying a *selection* -- files or folders,
            //which each of them tells apart for itself. They answer in text rather than in a window,
            //which is why they are the repository verbs' and not the window verbs'.
            VerbKind.Add => await repositoryVerbs.AddAsync(output, repository!, verb.Paths).ConfigureAwait(true),
            VerbKind.Remove => await repositoryVerbs.RemoveAsync(output, repository!, verb.Paths).ConfigureAwait(true),

            //`status` is text, always. It used to open the commit window when there was no console to
            //print into, which is every click -- so the catalog's entry for it was the root Commit
            //entry under a second name. The entry is gone and the verb is the terminal's.
            VerbKind.Status => await repositoryVerbs.StatusAsync(output, repository!).ConfigureAwait(true),

            //`switch` with a branch named is a script's command and answers with an exit code;
            //bare, it is a picker. CLAUDE.md: "branch picker when omitted".
            VerbKind.Switch => string.IsNullOrWhiteSpace(verb.Argument)
                ? await windowVerbs.SwitchPickerAsync(repository!).ConfigureAwait(true)
                : await repositoryVerbs.SwitchAsync(output, repository!, verb.Argument).ConfigureAwait(true),

            //`tag` reads the same way `switch` does: a name given is a script's command and answers
            //with an exit code, bare it is the picker.
            VerbKind.Tag => string.IsNullOrWhiteSpace(verb.Argument)
                ? windowVerbs.TagPicker(repository!)
                : await repositoryVerbs.TagAsync(output, repository!, verb.Argument).ConfigureAwait(true),

            //`stash` reads the way `tag` does: a message given is a script's command and answers with
            //an exit code, bare it is the window. Popping and dropping name a stash that already
            //exists, and those stay in the window -- see VerbKind.Stash.
            VerbKind.Stash => string.IsNullOrWhiteSpace(verb.Argument)
                ? windowVerbs.StashPicker(repository!)
                : await repositoryVerbs.StashAsync(output, repository!, verb.Argument).ConfigureAwait(true),

            VerbKind.Push => await repositoryVerbs.PushAsync(output, repository!).ConfigureAwait(true),

            //Tray never reaches here: App.xaml.cs answers it before the runner is asked. Every
            //other VerbKind has an arm above, which is what makes this unreachable rather than a
            //place for a verb to go missing quietly.
            _ => output.Report(Strings.Get("app.name"), false, $"`{verb.Kind}` has no handler."),
        };

    /// <summary>
    /// The repository containing <paramref name="path"/>, or null after saying so.
    ///
    /// CLAUDE.md: "If the path is not inside a repository, fail gracefully — a one-line message,
    /// never a full window." Phase 4 shows the clone popup here instead.
    /// </summary>
    private async Task<RepositoryInfo?> ResolveAsync(VerbOutput output, string path)
    {
        RepositoryInfo? repository = await repositories
            .ResolveAsync(path, CancellationToken.None)
            .ConfigureAwait(true);

        if (repository is not null)
        {
            //Every verb that touches a repository comes through here, which makes this the only
            //honest definition of "recently used": the context menu, the CLI and the tray alike.
            recent.Remember(repository);
            return repository;
        }

        string message = Strings.Get("error.notarepository", path);
        log.Info(message);
        output.Say(Strings.Get("app.name"), message);

        return null;
    }
}
