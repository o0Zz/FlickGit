using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using FlickGit.App.CommandLine;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.App.Resident;
using FlickGit.App.Settings;
using FlickGit.App.Shell;
using FlickGit.App.Tray;
using FlickGit.App.Trigger;
using FlickGit.App.Views;
using FlickGit.Ai;
using FlickGit.App.Ai;
using FlickGit.App.ViewModels;
using FlickGit.Branches;
using FlickGit.Cli;
using FlickGit.Clone;
using FlickGit.Commits;
using FlickGit.Config;
using FlickGit.Diagnostics;
using FlickGit.Forges;
using FlickGit.Blame;
using FlickGit.Diff;
using FlickGit.Files;
using FlickGit.History;
using FlickGit.Git;
using FlickGit.Ipc;
using FlickGit.Logging;
using FlickGit.Actions;
using FlickGit.Palette;
using FlickGit.Pulls;
using FlickGit.Remotes;
using FlickGit.Repositories;
using FlickGit.Stashes;
using FlickGit.Status;
using FlickGit.Submodules;
using FlickGit.Tags;
using FlickGit.Worktrees;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;

namespace FlickGit.App;

/// <summary>
/// The composition root and the process lifecycle. Nothing else.
///
/// <b>This is the only file allowed to mention the container.</b> Everything else declares its
/// dependencies as constructor parameters. If a class needs something it does not have, the
/// answer is a parameter here, not an <c>IServiceProvider</c> there.
///
/// One process, two roles, decided by the command line: no verb goes resident and opens nothing;
/// a verb is handed to <see cref="VerbRunner"/>, which is the path that must keep working with
/// the resident service stopped.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;
    private Mutex? _singleInstanceMutex;
    private TaskbarIcon? _trayIcon;
    private PipeServer? _pipe;
    private TriggerService? _trigger;
    private ILog _log = NullLog.Instance;

    /// <summary>
    /// Set by the verb and read by <see cref="OnExit"/>, so a failure reaches whatever launched
    /// flick.exe even when a window kept the process alive in between.
    /// </summary>
    private int _exitCode = ExitCodes.Success;

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        //A crash in this process takes every pre-warmed window with it, so nothing is allowed to escape
        //to the default handler.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.Error($"Unobserved task exception: {args.Exception.Flatten().Message}");
            args.SetObserved();
        };

        FlickSettings settings = FlickSettings.Load(out string? settingsError);

        //Before any window is constructed. Every view reads its text on construction and the resident
        //service keeps instances alive for the session, so a language applied later reaches nothing.
        Strings.Use(settings.Language);

        _services = BuildServices(settings);
        _log = _services.GetRequiredService<ILog>();

        if (settingsError is not null)
            _log.Warn(settingsError);

        //Both prompt files, when they are not there yet. Before any verb runs, so `flick ai` and the
        //settings window can name files that exist -- and idempotent, so every launch after the first
        //costs two File.Exists calls.
        _services.GetRequiredService<PromptStore>().SeedMissingFiles(settings.AiConventionalCommits);

        Verb verb = Verb.Parse(e.Args);
        _log.Debug($"Starting: {verb.Kind} {verb.Path}");

        if (verb.Kind == VerbKind.Tray)
        {
            StartResident();
            return;
        }

        //A verb-driven launch is over when its window closes. The resident launch is not, which is why
        //the default in App.xaml is OnExplicitShutdown.
        ShutdownMode = ShutdownMode.OnLastWindowClose;

        if (settingsError is not null)
            VerbOutput.Direct().Notice(Strings.Get("app.name"), settingsError, compact: false);

        _ = RunLaunchVerbAsync(verb);
    }

    /// <summary>
    /// Every service, registered once -- all singletons, and all of them registered whichever role
    /// the process is playing. A one-shot launch never resolves the ones it does not need.
    /// </summary>
    private static ServiceProvider BuildServices(FlickSettings settings)
    {
        var services = new ServiceCollection();

        services.AddSingleton(settings);
        services.AddSingleton<ILog>(_ => new FileLog(FileLog.DefaultDirectory, settings.VerboseLogging));
        services.AddSingleton<OperationTimings>();

        services.AddSingleton(provider => new GitExecutable(
            settings.GitPath,
            provider.GetRequiredService<ILog>()));

        services.AddSingleton<IGitProcessRunner>(provider => new GitProcessRunner(
            provider.GetRequiredService<GitExecutable>(),
            provider.GetRequiredService<ILog>(),
            provider.GetRequiredService<OperationTimings>()));

        services.AddSingleton<RepositoryService>();
        services.AddSingleton<StatusService>();
        services.AddSingleton<PatchService>();
        services.AddSingleton<RepositoryScanner>();

        //The catalog needs two things FlickGit.Core deliberately cannot reach: where the user profile is,
        //and the string table. Both arrive as plain values, so the catalog stays in Core.
        services.AddSingleton(provider => new ActionCatalog(
            FlickSettings.ActionsFilePath,
            Strings.Get,
            provider.GetRequiredService<ILog>()));

        //The same shape, and the same reason: the prompt files sit beside settings.json, and where
        //that is is a fact about Windows that FlickGit.Core does not get to know.
        services.AddSingleton(provider => new PromptStore(
            FlickSettings.DirectoryPath,
            provider.GetRequiredService<ILog>()));
        services.AddSingleton<RepositoryOverviewCache>();
        services.AddSingleton<DiffService>();
        services.AddSingleton<HistoryService>();
        services.AddSingleton<BlameService>();
        services.AddSingleton<BranchService>();
        services.AddSingleton<CommitService>();
        services.AddSingleton<CommitFlow>();
        services.AddSingleton<PullService>();
        services.AddSingleton<SwitchService>();
        services.AddSingleton<WorktreeService>();
        services.AddSingleton<SubmoduleService>();
        services.AddSingleton<PushService>();
        services.AddSingleton<RemoteService>();
        services.AddSingleton<RepositoryConfigService>();
        services.AddSingleton<TagService>();
        services.AddSingleton<StashService>();
        services.AddSingleton<CloneService>();

        services.AddSingleton<FileTextLoader>();
        services.AddSingleton<WorkingTreeWriter>();
        services.AddSingleton<WorkingTreeDeleter>();
        services.AddSingleton<EditorLauncher>();
        services.AddSingleton<RestoreService>();
        services.AddSingleton<TrackingService>();
        services.AddSingleton<FolderRemovalFlow>();
        services.AddSingleton<UntrackedFileMeasurer>();

        services.AddSingleton<ShellIntegration>();
        services.AddSingleton<OverlayIntegration>();
        services.AddSingleton<Autostart>();
        services.AddSingleton<TriggerService>();
        services.AddSingleton<ExplorerFolderResolver>();
        services.AddSingleton<CredentialStore>();

        //Three clients registered whichever forge this machine happens to use: they are three small
        //objects over the shared HttpClient, and a registration that depended on a repository's remote
        //could not be a singleton at all.
        services.AddSingleton<IPullRequestClient, GitHubClient>();
        services.AddSingleton<IPullRequestClient, GitLabClient>();
        services.AddSingleton<IPullRequestClient, AzureDevOpsClient>();
        services.AddSingleton<PullRequestClients>();
        services.AddSingleton<PullRequestService>();
        services.AddSingleton<PullRequestFlow>();
        services.AddSingleton<GitCredentialFill>();
        services.AddSingleton<ForgeCredentials>();

        //The provider is chosen here, once, from settings -- the only place allowed to know which
        //implementation a setting names.
        services.AddSingleton<AiConfiguration>();
        services.AddSingleton<AiContextBuilder>();
        services.AddSingleton(_ => BuildHttpClient());
        services.AddSingleton<IAiGenerator>(provider =>
        {
            var configuration = provider.GetRequiredService<AiConfiguration>();
            var http = provider.GetRequiredService<HttpClient>();
            var logger = provider.GetRequiredService<ILog>();

            //The key arrives as a delegate rather than as the store itself: CredentialStore is Windows-only
            //and FlickGit.Core deliberately is not.
            return configuration.Provider switch
            {
                AiProvider.Anthropic => new AnthropicGenerator(
                    http, configuration.Options, configuration.ReadKey, logger),

                AiProvider.OpenAi => new OpenAiGenerator(
                    http, configuration.Options, configuration.ReadKey, logger),

                //Copilot is the one provider whose stored credential is not what gets sent, so it takes a
                //CopilotToken rather than the key delegate.
                AiProvider.Copilot => new CopilotGenerator(
                    http,
                    configuration.Options,
                    new CopilotToken(http, configuration.ReadKey, logger),
                    logger),

                //The local one. No key delegate at all: there is nobody to authenticate to.
                AiProvider.Ollama => new OllamaGenerator(http, configuration.Options, logger),

                _ => new DisabledAiGenerator(),
            };
        });
        services.AddSingleton<AiTextService>();
        services.AddSingleton<ResidentService>();
        services.AddSingleton<PipeServer>();
        services.AddSingleton<Notifier>();
        services.AddSingleton<RecentRepositories>();

        services.AddSingleton<UpstreamConsent>();

        //There is exactly one commit window and one palette per process -- both pre-warmed at logon and
        //reused -- so "per window" and "per process" are the same lifetime here.
        services.AddSingleton<DiffCache>();
        services.AddSingleton(provider =>
        {
            var viewModel = ActivatorUtilities.CreateInstance<CommitViewModel>(provider);
            var notifier = provider.GetRequiredService<Notifier>();

            //Here rather than inside the view model: its job is the outcome, not which surfaces hear about
            //it. The window closes itself after a successful commit, so this is the only thing left to say
            //it worked.
            //
            //Through CommitOutcomeReporter rather than formatting commit.success directly, which is what
            //this did and why a Commit & Push reported only the hash: the footer used the reporter and
            //said "Pushed x to origin/x", the toast did not, and the toast is the half that outlives the
            //window. One phrasing, one place, so the two surfaces cannot disagree again.
            //
            //Titled with the repository rather than "FlickGit". The premise of the product is a user
            //across five to ten repositories a day, so which one this happened in is the first thing the
            //notification has to answer -- and the body already names the operation.
            viewModel.Committed += result =>
            {
                if (CommitOutcomeReporter.SuccessText(result) is { Length: > 0 } text)
                    notifier.Success(viewModel.RepositoryName, text);
            };

            return viewModel;
        });
        services.AddSingleton<PaletteViewModel>();

        services.AddSingleton<CommitWindowHost>();
        services.AddSingleton<CommitLauncher>();
        services.AddSingleton<PaletteWindowHost>();
        services.AddSingleton<ActionRunner>();
        services.AddSingleton<RepositoryVerbs>();
        services.AddSingleton<EnvironmentVerbs>();
        services.AddSingleton<WindowVerbs>();
        services.AddSingleton<VerbRunner>();

        //The two cycles in the graph, each broken on one side by a factory rather than a settable
        //property somebody has to remember to assign. ActionRunner opens a window through the verb
        //runner and the verb runner reaches actions for `flick run`; the palette host runs an action and
        //an action can open the palette.
        services.AddSingleton<Func<VerbRunner>>(provider => provider.GetRequiredService<VerbRunner>);
        services.AddSingleton<Func<ActionRunner>>(provider => provider.GetRequiredService<ActionRunner>);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The pooled HTTP client for the AI provider.
    ///
    /// <b>An infinite <see cref="HttpClient.Timeout"/>.</b> That timeout covers reading the streamed
    /// body, so any finite value would cut a long message off mid-sentence. The 8 second silence
    /// budget is a linked token inside each generator instead, where it can tell "the provider is not
    /// answering" from "the user closed the window".
    /// </summary>
    private static HttpClient BuildHttpClient() =>
        new(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
            EnableMultipleHttp2Connections = true,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version20,

            //RequestVersionOrLower, so ALPN negotiates h2 and falls back to 1.1 rather than failing outright
            //against a proxy that does not speak it.
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

    private async Task RunLaunchVerbAsync(Verb verb)
    {
        VerbResult result = await RunAsync(verb, VerbOutput.Direct()).ConfigureAwait(true);

        _exitCode = result.Code;

        //A window verb leaves the code for OnExit and lets WPF end the process when the last window
        //closes. Shutting down here would close the window it just opened.
        if (result.ShutDown)
            Shutdown(result.Code);
    }

    /// <summary>
    /// The tray path: the service outlives the verb, whatever it returned. A recent repository that
    /// has since been deleted answers <c>Exit(NotARepository)</c>, and honouring that here would quit
    /// the resident service because a menu entry went stale.
    /// </summary>
    private async Task RunTrayVerbAsync(Verb verb) =>
        await RunAsync(verb, VerbOutput.Direct()).ConfigureAwait(true);

    private Task<VerbResult> RunAsync(Verb verb, VerbOutput output) =>
        _services!.GetRequiredService<VerbRunner>().RunAsync(verb, output);

    private void StartResident()
    {
        //Per user: the name has to be unique to the account, or a second logged-on user's launch would
        //forward into the first user's process -- and into a named pipe that must never be shared across
        //accounts.
        //
        //Owning the mutex is what makes `createdNew` mean something. Created unowned, a second launch
        //would acquire the free mutex, conclude it was first, and go resident alongside the real
        //service -- two tray icons, and a listener that can never have the pipe.
        string mutexName = $"Local\\FlickGit.Resident.{Environment.UserName}";
        _singleInstanceMutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);

        if (!createdNew)
        {
            _log.Info("Another FlickGit instance is already resident; exiting.");
            Shutdown(ExitCodes.Success);
            return;
        }

        ServiceProvider services = _services!;
        var recent = services.GetRequiredService<RecentRepositories>();
        VerbOutput output = VerbOutput.Direct();

        _trayIcon = TrayIconFactory.Create(
            recent: () => recent.Paths,
            onOpenRecent: path => _ = RunTrayVerbAsync(new Verb(VerbKind.Commit, path, null)),

            onSettings: () => _ = RunTrayVerbAsync(new Verb(VerbKind.Settings, null, null)),

            //About is a tab of that window rather than a notice of its own, so the version, the help page
            //and the repository link live in one place.
            onAbout: () => services.GetRequiredService<EnvironmentVerbs>().Settings(output, SettingsTab.About),
            onExit: () => Shutdown(ExitCodes.Success));

        //The pipe before the pre-warm, so a right-click arriving during it is served rather than falling
        //back to a cold launch.
        _pipe = services.GetRequiredService<PipeServer>();
        _pipe.Start(HandleRequestAsync);

        //The tray icon is how a notification reaches the user, and the commit window closes itself on
        //success by default -- so without this a successful commit would leave no trace at all.
        services.GetRequiredService<Notifier>().Tray = _trayIcon;

        _log.Info($"FlickGit {Version} resident.");

        //After the tray and the pipe, so a trigger arriving during the pre-warm is served by the same
        //code path as one arriving an hour later.
        _trigger = services.GetRequiredService<TriggerService>();

        var palette = services.GetRequiredService<PaletteWindowHost>();

        TriggerStartup trigger = _trigger.Start(
            foreground => _ = services.GetRequiredService<CommitLauncher>().LaunchAsync(foreground),

            //The foreground window is of no interest here: the palette is the surface for when the user is
            //*not* looking at the folder they mean.
            foreground => _ = palette.ShowAsync());

        _log.Info($"Trigger: {trigger.Commit}   Palette: {trigger.Palette}");

        if (trigger.Error is not null)
        {
            //Reported, and startup continues. A hotkey somebody else owns must not cost the user their tray
            //icon, their context menu or their pipe.
            _log.Warn(trigger.Error);
            services.GetRequiredService<Notifier>().Warn(Strings.Get("app.name"), trigger.Error);
        }

        //After the tray icon exists and the pipe is listening, at background priority so it cannot delay
        //either. A cold TLS and HTTP/2 handshake costs 100-300 ms, a third of the 400 ms first-token
        //budget, and it is only worth doing when there is a key to use.
        if (services.GetRequiredService<AiTextService>().IsUsable)
        {
            //The budget is the provider's, not a constant: for the hosted three this is a TLS handshake,
            //while for Ollama the warm-up *is* the model load -- tens of seconds, and the whole reason to do
            //it at logon rather than inside the user's first commit.
            var warmup = new CancellationTokenSource(
                services.GetRequiredService<AiConfiguration>().Options.WarmUpBudget);

            _ = services.GetRequiredService<IAiGenerator>()
                .ProbeAsync(warmup.Token)
                .ContinueWith(_ => warmup.Dispose(), TaskScheduler.Default);
        }

        Dispatcher.BeginInvoke(
            () =>
            {
                //The commit window first: it is the surface the trigger, the context menu and `flick commit` all
                //land on.
                services.GetRequiredService<CommitWindowHost>().Warm();
                palette.Warm();
            },
            DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Runs one request from the pipe, on the UI thread. The same parser and runner the stub's
    /// fallback launch would have used, so the two paths cannot disagree about what a verb means.
    /// </summary>
    private Task<IpcResponse> HandleRequestAsync(IpcRequest request) =>
        Dispatcher.InvokeAsync(async () =>
        {
            Verb verb = Verb.Parse(request.Arguments, request.WorkingDirectory);
            _log.Debug($"Pipe request: {verb.Kind} {verb.Path}");

            VerbOutput output = VerbOutput.ForClient(request.HasConsole);

            VerbResult result = await RunAsync(verb, output).ConfigureAwait(true);

            //VerbResult.ShutDown is about the *invoking* process. Over the pipe that is the stub, so the
            //code travels back and this process stays up.
            return new IpcResponse(result.Code, output.Output, output.Error);
        }).Task.Unwrap();

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log.Error($"Unhandled UI exception: {e.Exception}");

        VerbOutput.Direct().Notice(
            Strings.Get("error.title"),
            $"{e.Exception.Message}\n\nThe log may hold more:\n{FileLog.DefaultDirectory}",
            compact: false);

        //Handled, so the process survives. A resident service that dies on one bad window takes the tray
        //icon and every pre-warmed window with it.
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        //The trigger first: it owns a window handle and a global hotkey registration, and both
        //should be released before anything else goes away.
        _trigger?.Dispose();
        _pipe?.Dispose();
        _trayIcon?.Dispose();
        _singleInstanceMutex?.Dispose();
        _services?.Dispose();

        //The verb's outcome, not WPF's. Whatever launched flick.exe branches on this.
        e.ApplicationExitCode = _exitCode;

        base.OnExit(e);
    }
}
