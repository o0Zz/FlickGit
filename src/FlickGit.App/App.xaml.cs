using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using FlickGit.App.CommandLine;
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
using FlickGit.Diagnostics;
using FlickGit.Diff;
using FlickGit.Git;
using FlickGit.Ipc;
using FlickGit.Logging;
using FlickGit.Actions;
using FlickGit.Models;
using FlickGit.Palette;
using FlickGit.Pulls;
using FlickGit.Remotes;
using FlickGit.Repositories;
using FlickGit.Status;
using FlickGit.Tags;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;

namespace FlickGit.App;

/// <summary>
/// The composition root and the process lifecycle. Nothing else.
///
/// <b>This is the only file allowed to mention the container.</b> Everything else declares its
/// dependencies as constructor parameters and knows nothing about how it was built — Hard
/// Requirement 3. If a class needs something it does not have, the answer is a parameter here, not
/// an <c>IServiceProvider</c> there.
///
/// One process serves two roles, decided entirely by the command line:
///
/// <list type="bullet">
/// <item><description><b>No verb</b> — go resident. Tray icon, pipe listener, pre-warmed window,
/// open nothing, stay alive.</description></item>
/// <item><description><b>A verb</b> — hand it to <see cref="VerbRunner"/> and honour the result.
/// This is the path that must keep working with the resident service stopped, which CLAUDE.md,
/// "Definition of Done" requires of every feature.</description></item>
/// </list>
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

    /// <summary>The assembly version, which CI stamps from `git describe`. Shown by `flick version`.</summary>
    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        //A crash in this process takes every pre-warmed window with it, so nothing is allowed to
        //escape to the default handler. Both hooks report and continue where that is safe -- the
        //alternative is the tray icon vanishing mid-session with no trace of why.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.Error($"Unobserved task exception: {args.Exception.Flatten().Message}");
            args.SetObserved();
        };

        FlickSettings settings = FlickSettings.Load(out string? settingsError);

        //Before any window is constructed. Every view reads its text on construction and the
        //resident service keeps instances alive for the session, so a language applied later would
        //never reach them.
        Strings.Use(settings.Language);

        _services = BuildServices(settings);
        _log = _services.GetRequiredService<ILog>();

        if (settingsError is not null)
            _log.Warn(settingsError);

        Verb verb = Verb.Parse(e.Args);
        _log.Debug($"Starting: {verb.Kind} {verb.Path}");

        if (verb.Kind == VerbKind.Tray)
        {
            StartResident();
            return;
        }

        //A verb-driven launch is over when its window closes. The resident launch is not, which is
        //why the default in App.xaml is OnExplicitShutdown.
        ShutdownMode = ShutdownMode.OnLastWindowClose;

        if (settingsError is not null)
            VerbOutput.Direct().Notice(Strings.Get("app.name"), settingsError, compact: false);

        _ = RunLaunchVerbAsync(verb);
    }

    /// <summary>
    /// Every service, registered once.
    ///
    /// All singletons, and all of them registered whichever role the process is playing: a one-shot
    /// launch simply never resolves the ones it does not need, and a second registration path for
    /// "am I resident" would be a worse trade than a few unused objects.
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

        //Core: no UI, no WPF, and the only assembly under test.
        services.AddSingleton<RepositoryService>();
        services.AddSingleton<StatusService>();
        services.AddSingleton<PatchService>();
        services.AddSingleton<RepositoryScanner>();

        //The catalog needs two things FlickGit.Core deliberately cannot reach: where the user profile
        //is, and the string table. Both arrive as plain values, which is why the catalog itself stays
        //in Core where it can be reasoned about without a message pump.
        services.AddSingleton(provider => new ActionCatalog(
            FlickSettings.ActionsFilePath,
            Strings.Get,
            provider.GetRequiredService<ILog>()));
        services.AddSingleton<RepositoryOverviewCache>();
        services.AddSingleton<DiffService>();
        services.AddSingleton<BranchService>();
        services.AddSingleton<CommitService>();
        services.AddSingleton<CommitFlow>();
        services.AddSingleton<PullService>();
        services.AddSingleton<SwitchService>();
        services.AddSingleton<PushService>();
        services.AddSingleton<TagService>();
        services.AddSingleton<CloneService>();

        //The two that touch the working tree, and the one that sizes an untracked file. Instances
        //rather than statics for the reason Hard Requirement 3 gives: they do file I/O, so their
        //callers have to be able to receive them.
        services.AddSingleton<FileTextLoader>();
        services.AddSingleton<WorkingTreeWriter>();
        services.AddSingleton<UntrackedFileMeasurer>();

        //The Windows surfaces: the registry, the Task Scheduler, the pipe, the tray. Every one of
        //them touches something outside the process, which is exactly why none of them is a static.
        services.AddSingleton<ShellIntegration>();
        services.AddSingleton<Autostart>();
        services.AddSingleton<TriggerService>();
        services.AddSingleton<ExplorerFolderResolver>();
        services.AddSingleton<ApiKeyStore>();

        //AI. The provider is chosen here, once, from settings -- this is the only place allowed to
        //know which implementation a setting names, and the only place that could.
        services.AddSingleton<AiConfiguration>();
        services.AddSingleton<CommitContextBuilder>();
        services.AddSingleton(_ => BuildHttpClient());
        services.AddSingleton<ICommitMessageGenerator>(provider =>
        {
            var configuration = provider.GetRequiredService<AiConfiguration>();
            var http = provider.GetRequiredService<HttpClient>();
            var logger = provider.GetRequiredService<ILog>();

            //The key arrives as a delegate rather than as the store itself: an interface with one
            //implementation is forbidden by Hard Requirement 2, and ApiKeyStore is Windows-only
            //while FlickGit.Core deliberately is not.
            return configuration.Provider switch
            {
                AiProvider.Anthropic => new AnthropicCommitMessageGenerator(
                    http, configuration.Options, configuration.ReadKey, logger),

                AiProvider.OpenAi => new OpenAiCommitMessageGenerator(
                    http, configuration.Options, configuration.ReadKey, logger),

                _ => new DisabledCommitMessageGenerator(),
            };
        });
        services.AddSingleton<CommitMessageService>();
        services.AddSingleton<ResidentService>();
        services.AddSingleton<PipeServer>();
        services.AddSingleton<Notifier>();
        services.AddSingleton<RecentRepositories>();

        //The command line, and the window it can open.
        services.AddSingleton<UpstreamConsent>();
        services.AddSingleton<CommitViewModelFactory>();
        services.AddSingleton<CommitWindowHost>();
        services.AddSingleton<QuickCommitViewModelFactory>();
        services.AddSingleton<QuickCommitWindowHost>();
        services.AddSingleton<QuickCommitLauncher>();
        services.AddSingleton<PaletteViewModelFactory>();
        services.AddSingleton<PaletteWindowHost>();
        services.AddSingleton<ActionRunner>();
        services.AddSingleton<RepositoryVerbs>();
        services.AddSingleton<EnvironmentVerbs>();
        services.AddSingleton<WindowVerbs>();
        services.AddSingleton<VerbRunner>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The pooled HTTP client for the AI provider.
    ///
    /// CLAUDE.md's exact pooling settings, and one more that is not in it: an infinite
    /// <see cref="HttpClient.Timeout"/>. That timeout covers reading the streamed body too, so any
    /// finite value would cut a long message off mid-sentence — the 8 second hard timeout is a
    /// linked token inside each generator instead, where it can tell "the provider is not answering"
    /// from "the user closed the popup".
    ///
    /// Disposed by the container along with everything else, and <c>new HttpClient(handler)</c>
    /// disposes its handler, so the connection pool goes with it.
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

            //RequestVersionOrLower, so ALPN negotiates h2 and falls back to 1.1 rather than failing
            //outright against a proxy that does not speak it.
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

    /// <summary>
    /// The one-shot launch path: whatever the verb decides also decides the process's fate.
    /// </summary>
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
    /// The tray path: the service outlives the verb, whatever it returned.
    ///
    /// Separate from the launch path on purpose. A recent repository that has since been deleted
    /// answers <c>Exit(NotARepository)</c>, and honouring that here would quit the resident service
    /// because a menu entry went stale.
    /// </summary>
    private async Task RunTrayVerbAsync(Verb verb) =>
        await RunAsync(verb, VerbOutput.Direct()).ConfigureAwait(true);

    private Task<VerbResult> RunAsync(Verb verb, VerbOutput output) =>
        _services!.GetRequiredService<VerbRunner>().RunAsync(verb, output);

    private void StartResident()
    {
        //Per user. The name has to be unique to the account, or a second logged-on user's launch
        //would silently forward into the first user's process -- and what it would forward into is
        //a named pipe that must never be shared across accounts.
        //
        //`createdNew` is the whole test, and owning the mutex is what makes it mean something:
        //created unowned, a second launch would acquire the free mutex, conclude it was first, and
        //go resident alongside the real service -- two tray icons, and a listener that can never
        //have the pipe. The handle closing at process exit releases it.
        string mutexName = $"Local\\FlickGit.Resident.{Environment.UserName}";
        _singleInstanceMutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);

        if (!createdNew)
        {
            //A second resident launch is redundant: the first one owns the pipe, and every stub
            //invocation reaches it. Leaving it alone is the whole of the single-instance rule.
            _log.Info("Another FlickGit instance is already resident; exiting.");
            Shutdown(ExitCodes.Success);
            return;
        }

        ServiceProvider services = _services!;
        var recent = services.GetRequiredService<RecentRepositories>();
        VerbOutput output = VerbOutput.Direct();

        _trayIcon = TrayIconFactory.Create(
            //Zero: a tray click has no Explorer window behind it, so the launcher goes straight
            //to the recent list and the popup says so.
            onQuickCommit: () => _ = services.GetRequiredService<QuickCommitLauncher>().LaunchAsync(0),
            recent: () => recent.Paths,
            onOpenRecent: path => _ = RunTrayVerbAsync(new Verb(VerbKind.Commit, path, null)),

            //The same window `flick settings` opens, through the same verb.
            onSettings: () => _ = RunTrayVerbAsync(new Verb(VerbKind.Settings, null, null)),

            //About is a tab of that window rather than a notice of its own, so there is one place
            //the version, the help page and the repository link live. Straight to the verb class
            //because the tab is not something a command line can name -- and nothing else would be
            //served by inventing a verb for it.
            onAbout: () => services.GetRequiredService<EnvironmentVerbs>().Settings(output, SettingsTab.About),
            onExit: () => Shutdown(ExitCodes.Success));

        //The pipe before the pre-warm, so a right-click arriving during it is served rather than
        //falling back to a cold launch.
        _pipe = services.GetRequiredService<PipeServer>();
        _pipe.OnRequest = HandleRequestAsync;
        _pipe.Start();

        //The tray icon is how a notification reaches the user, and the commit window closes itself
        //on success by default -- so without this a successful commit would leave no trace at all.
        services.GetRequiredService<Notifier>().Tray = _trayIcon;

        //The pipe name is logged by the listener itself; this line is for the version.
        _log.Info($"FlickGit {Version} resident.");

        //After the tray and the pipe, so a trigger arriving during the pre-warm is served by the
        //same code path as one arriving an hour later.
        _trigger = services.GetRequiredService<TriggerService>();

        //The palette's only route to Git. It raises a catalog action; the runner confirms anything
        //destructive and then either opens the window through the same VerbRunner the CLI uses or runs
        //the argument list. That is what makes CLAUDE.md's "the palette is not a shortcut around these
        //rules" structurally true rather than a promise.
        var actions = services.GetRequiredService<ActionRunner>();
        actions.Confirm = (title, question, yes, no) =>
            Task.FromResult(ConfirmWindow.Ask(null, title, question, yes, no));

        //The other half of the loop the container cannot close: an action that opens a window goes
        //through the verb runner, and the verb runner reaches actions for `flick run`.
        actions.RunVerb = (verb, output) => services.GetRequiredService<VerbRunner>().RunAsync(verb, output);

        var palette = services.GetRequiredService<PaletteWindowHost>();
        palette.OnAction = (action, repository, argument) =>
            _ = actions.RunAsync(action, repository, VerbOutput.Direct(), argument);

        TriggerStartup trigger = _trigger.Start(
            foreground => _ = services.GetRequiredService<QuickCommitLauncher>().LaunchAsync(foreground),

            //The foreground window is of no interest here: the palette is the surface for when the
            //user is *not* looking at the folder they mean, so there is nothing to resolve from it.
            foreground => _ = palette.ShowAsync());

        _log.Info($"Trigger: {trigger.QuickCommit}   Palette: {trigger.Palette}");

        if (trigger.Error is not null)
        {
            //Reported, and startup continues. A hotkey somebody else owns must not cost the user
            //their tray icon, their context menu or their pipe.
            _log.Warn(trigger.Error);
            services.GetRequiredService<Notifier>().Warn(Strings.Get("app.name"), trigger.Error);
        }

        //After the tray icon exists and the pipe is listening. Queued at background priority so it
        //cannot delay either: the point is to be ready before the user asks, not busy while they do.
        //The provider connection, warmed alongside the windows. A cold TLS and HTTP/2 handshake
        //costs 100-300 ms, which is a third of the 400 ms first-token budget -- and it is only worth
        //doing when there is actually a key to use.
        if (services.GetRequiredService<CommitMessageService>().IsUsable)
        {
            _ = services.GetRequiredService<ICommitMessageGenerator>()
                .ProbeAsync(new CancellationTokenSource(TimeSpan.FromSeconds(2)).Token);
        }

        Dispatcher.BeginInvoke(
            () =>
            {
                //The popup first: it is the primary interaction and the window is the escape hatch,
                //so if only one of them gets warmed before the user asks, it should be this one.
                services.GetRequiredService<QuickCommitWindowHost>().Warm();
                services.GetRequiredService<CommitWindowHost>().Warm();
                palette.Warm();
            },
            DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Runs one request from the pipe, on the UI thread.
    ///
    /// The same parser and the same runner the stub's fallback launch would have used, so the two
    /// paths cannot disagree about what a verb means. What differs is only where the answer goes:
    /// into the response, for the stub to print.
    /// </summary>
    private Task<IpcResponse> HandleRequestAsync(IpcRequest request) =>
        Dispatcher.InvokeAsync(async () =>
        {
            Verb verb = Verb.Parse(request.Arguments, request.WorkingDirectory);
            _log.Debug($"Pipe request: {verb.Kind} {verb.Path}");

            VerbOutput output = VerbOutput.ForClient(request.HasConsole);

            VerbResult result = await RunAsync(verb, output).ConfigureAwait(true);

            //VerbResult.ShutDown is about the *invoking* process. Over the pipe that is the stub, so
            //the code travels back and this process stays up.
            return new IpcResponse(result.Code, output.Output, output.Error);
        }).Task.Unwrap();

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log.Error($"Unhandled UI exception: {e.Exception}");

        VerbOutput.Direct().Notice(
            Strings.Get("error.title"),
            $"{e.Exception.Message}\n\nThe log may hold more:\n{FileLog.DefaultDirectory}",
            compact: false);

        //Handled, so the process survives. A resident service that dies on one bad window takes the
        //tray icon and every pre-warmed window with it, leaving the user with a context menu whose
        //entries do nothing.
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
