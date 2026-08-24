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
            or VerbKind.Blame or VerbKind.Repo;

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
            ?? new RepositoryInfo(path ?? string.Empty, string.Empty, HasSubmodules: false, IsBare: false);

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

            VerbKind.PullRebase => await windowVerbs.PullAsync(output, repository!).ConfigureAwait(true),
            VerbKind.Log => await windowVerbs.LogAsync(repository!).ConfigureAwait(true),
            VerbKind.Repo => windowVerbs.Repo(repository!),

            //`verb.Path` is the clicked *file*, which routing carries through untouched --
            //`repository` was resolved from its directory.
            VerbKind.Blame => await windowVerbs.BlameAsync(output, repository!, verb.Path!).ConfigureAwait(true),

            //`status` is text when there is a console to print into, and the commit window when
            //there is not: it is reachable from the context menu, where a window is what the user
            //expects.
            VerbKind.Status => output.HasConsole
                ? await repositoryVerbs.StatusAsync(output, repository!).ConfigureAwait(true)
                : await windowVerbs.CommitAsync(output, repository!).ConfigureAwait(true),

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
