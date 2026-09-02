using System.Runtime.InteropServices;
using FlickGit.Actions;
using FlickGit.App.CommandLine;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.App.Resident;
using FlickGit.App.Settings;
using FlickGit.Blame;
using FlickGit.Branches;
using FlickGit.Cli;
using FlickGit.Commits;
using FlickGit.Config;
using FlickGit.Diff;
using FlickGit.Files;
using FlickGit.Git;
using FlickGit.History;
using FlickGit.Logging;
using FlickGit.Merges;
using FlickGit.Diagnostics;
using FlickGit.Ipc;
using FlickGit.Pulls;
using FlickGit.Remotes;
using FlickGit.Repositories;
using FlickGit.Stashes;
using FlickGit.Status;
using FlickGit.Tags;
using Microsoft.Extensions.DependencyInjection;

namespace FlickGit.App.Mac;

/// <summary>
/// The macOS entry point, and this project's composition root.
///
/// It is a second composition root rather than a share of FlickGit.App's, and that is deliberate at
/// this stage. Hard Requirement 3 says the container is mentioned in one place, which it now is
/// per host: this file registers what a text-only CLI needs and the three platform seams it answers
/// them with, and knows nothing about WPF. Consolidating the Core half into one shared registration
/// is worth doing once the windows exist and the two lists have actually converged — doing it now
/// would mean guessing at what the macOS UI will want and rewriting the working Windows root to
/// match the guess.
///
/// <b>No resident service, no socket, no pre-warm.</b> Every verb runs in this process and exits.
/// CLAUDE.md is explicit that the resident service is an optimisation and never a dependency, so
/// this is the path that has to work anyway — it is the cold fallback, standing on its own before
/// there is anything to fall back from.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] arguments)
    {
        //The same two-step the Windows host uses: settings first, because the log's verbosity and
        //the language both come out of them, and a failure to read them must not stop the tool.
        FlickSettings settings = FlickSettings.Load(out string? settingsError);

        Strings.Use(settings.Language);

        Verb verb = Verb.Parse(arguments, Environment.CurrentDirectory);

        //Hand it to a service that is already running, before paying for a container of our own.
        //Skipped for `tray`, which *is* the service: forwarding it would ask the running instance to
        //start a second one.
        if (verb.Kind != VerbKind.Tray && await ForwardAsync(arguments).ConfigureAwait(false) is { } forwarded)
            return forwarded;

        await using ServiceProvider services = BuildServices(settings);

        if (settingsError is not null)
            services.GetRequiredService<ILog>().Warn(settingsError);

        if (verb.Kind == VerbKind.Tray)
            return await ServeAsync(services).ConfigureAwait(false);

        VerbOutput output = VerbOutput.Direct(
            services.GetRequiredService<INotifier>(),
            services.GetRequiredService<IDialogs>());

        VerbResult result = await services
            .GetRequiredService<VerbRunner>()
            .RunAsync(verb, output)
            .ConfigureAwait(false);

        return result.Code;
    }

    /// <summary>
    /// The exit code a running service answered with, or null when there is nobody to ask.
    ///
    /// Null is the ordinary case and not a failure: no service running, one still starting, one
    /// wedged past the 250 ms budget. Every one of them means "do it here instead", which is the
    /// path that has to work anyway.
    /// </summary>
    private static async Task<int?> ForwardAsync(string[] arguments)
    {
        //Whether this process can print is something only it knows, and the service has no console
        //of its own -- without this it would answer a terminal and a double-click the same way.
        var request = new IpcRequest(arguments, Environment.CurrentDirectory, ConsoleOutput.IsAvailable);

        IpcResponse? response = await LocalEndpoint
            .SendAsync(request, CancellationToken.None)
            .ConfigureAwait(false);

        if (response is null)
            return null;

        //Written rather than returned, because the service captured them for exactly this.
        if (response.Output.Length > 0)
            ConsoleOutput.WriteLine(response.Output.TrimEnd('\n'));

        if (response.Error.Length > 0)
            ConsoleOutput.WriteError(response.Error.TrimEnd('\n'));

        return response.ExitCode;
    }

    /// <summary>
    /// Runs as the resident service until the system asks it to stop.
    ///
    /// <b>No menu bar item yet, and that is a real gap rather than an omission.</b> An NSStatusItem
    /// needs AppKit, which arrives with the UI toolkit — so for now this is a headless daemon, and
    /// <c>INotifier.CanNotify</c> is false, which routes every outcome to text exactly as it does for
    /// a one-shot run. Nothing is lost silently; there is simply nothing to click yet.
    /// </summary>
    private static async Task<int> ServeAsync(ServiceProvider services)
    {
        var log = services.GetRequiredService<ILog>();

        //Single instance, asked the only way that cannot be wrong: if something answers on the
        //endpoint, something is already serving it. A lock file would have to be reconciled with the
        //socket anyway, and a stale one is the failure mode that keeps a service from ever starting.
        if (await LocalEndpoint.SendAsync(
                new IpcRequest(["version"], Environment.CurrentDirectory, HasConsole: false),
                CancellationToken.None).ConfigureAwait(false) is not null)
        {
            log.Warn("Another FlickGit service is already listening. This one is exiting.");

            return ExitCodes.Success;
        }

        using var stopping = new CancellationTokenSource();

        //launchd stops an agent with SIGTERM. Ctrl+C is for running it by hand.
        using PosixSignalRegistration term =
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop);
        using PosixSignalRegistration interrupt =
            PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop);

        void Stop(PosixSignalContext context)
        {
            //Handled here, so the runtime does not take the process down before the socket file is
            //cleaned up and the current request has answered.
            context.Cancel = true;

            // ReSharper disable once AccessToDisposedClosure
            stopping.Cancel();
        }

        var endpoint = services.GetRequiredService<LocalEndpoint>();
        var runner = services.GetRequiredService<VerbRunner>();
        var notifier = services.GetRequiredService<INotifier>();
        var dialogs = services.GetRequiredService<IDialogs>();

        await endpoint.ServeAsync(
            async request =>
            {
                //The same parser the local path uses, so the two cannot disagree about a verb. The
                //working directory is the *client's*: <path> defaults to it, and the service's own is
                //wherever launchd started it.
                Verb requested = Verb.Parse(request.Arguments, request.WorkingDirectory);

                VerbOutput captured = VerbOutput.ForClient(notifier, dialogs, request.HasConsole);

                try
                {
                    VerbResult result = await runner.RunAsync(requested, captured).ConfigureAwait(false);

                    return new IpcResponse(result.Code, captured.Output, captured.Error);
                }
                catch (Exception ex)
                {
                    //A failed verb must not take the service with it: the next request is somebody
                    //else's and has nothing to do with this one.
                    log.Error($"Serving `{requested.Kind}` failed: {ex.Message}");

                    return new IpcResponse(ExitCodes.GitError, captured.Output, ex.Message);
                }
            },
            stopping.Token).ConfigureAwait(false);

        log.Info("The service has stopped.");

        return ExitCodes.Success;
    }

    private static ServiceProvider BuildServices(FlickSettings settings)
    {
        var services = new ServiceCollection();

        services.AddSingleton(settings);
        services.AddSingleton<ILog>(_ => new FileLog(FlickSettings.LogsDirectoryPath, settings.VerboseLogging));
        services.AddSingleton<OperationTimings>();

        services.AddSingleton(provider => new GitExecutable(
            settings.GitPath,
            provider.GetRequiredService<ILog>()));

        services.AddSingleton<IGitProcessRunner>(provider => new GitProcessRunner(
            provider.GetRequiredService<GitExecutable>(),
            provider.GetRequiredService<ILog>(),
            provider.GetRequiredService<OperationTimings>()));

        services.AddSingleton<RepositoryService>();

        //Before StatusService, which takes them: the merge state and any prepared commit message
        //both ride along on every status read.
        services.AddSingleton<MergeStateService>();
        services.AddSingleton<PreparedMessageService>();
        services.AddSingleton<ConflictService>();
        services.AddSingleton<UntrackedFileMeasurer>();
        services.AddSingleton<StatusService>();

        //Where the user profile is, and the string table — the two things FlickGit.Core deliberately
        //cannot reach, handed in as plain values exactly as the Windows host hands them.
        services.AddSingleton(provider => new ActionCatalog(
            FlickSettings.ActionsFilePath,
            Strings.Get,
            provider.GetRequiredService<ILog>()));

        services.AddSingleton<RepositoryConfigService>();
        services.AddSingleton<BranchService>();
        services.AddSingleton<SwitchService>();
        services.AddSingleton<PushService>();
        services.AddSingleton<TagService>();
        services.AddSingleton<StashService>();
        services.AddSingleton<TrackingService>();
        services.AddSingleton<CommitService>();
        services.AddSingleton<HistoryService>();
        services.AddSingleton<BlameService>();
        services.AddSingleton<DiffService>();
        services.AddSingleton<FileTextLoader>();
        services.AddSingleton<RemoteService>();
        services.AddSingleton<PullService>();

        services.AddSingleton<LocalEndpoint>();

        //Guarded, and the guard is enforced rather than polite: both classes are
        //[SupportedOSPlatform("macos")], so CA1416 fails the build if they are constructed on a
        //path reachable from Windows. This project is net9.0 and does run on Windows -- which is
        //what let the socket transport be tested at all -- so the analyser is the only thing
        //between a development run and a dlopen of a library that is not there.
        if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IAutostart, LaunchAgentAutostart>();
            services.AddSingleton<ITrash, FinderTrash>();
        }
        else
        {
            //A development run on Windows. EnvironmentReports needs an IAutostart whatever platform
            //this is, and there is no launchd here to give it.
            services.AddSingleton<IAutostart, UnsupportedAutostart>();
        }


        services.AddSingleton<UpstreamConsent>();
        services.AddSingleton<RecentRepositories>();

        //The three seams. A CLI has no notification area and no windows, and every verb that needs
        //one is refused by name rather than silently doing nothing.
        services.AddSingleton<INotifier, SilentNotifier>();
        services.AddSingleton<IDialogs, ConsoleDialogs>();
        services.AddSingleton<EnvironmentReports>();
        services.AddSingleton<IEnvironmentVerbs, MacEnvironmentVerbs>();
        services.AddSingleton<UnavailableVerbs>();
        services.AddSingleton<IWindowVerbs>(provider => provider.GetRequiredService<UnavailableVerbs>());

        services.AddSingleton<RepositoryVerbs>();
        services.AddSingleton<ActionRunner>();
        services.AddSingleton<VerbRunner>();

        //The same cycle the Windows host breaks the same way: an action can open a verb, and a verb
        //can run an action.
        services.AddSingleton<Func<VerbRunner>>(provider => provider.GetRequiredService<VerbRunner>);
        services.AddSingleton<Func<ActionRunner>>(provider => provider.GetRequiredService<ActionRunner>);

        return services.BuildServiceProvider();
    }
}
