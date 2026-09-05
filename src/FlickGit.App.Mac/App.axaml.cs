using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FlickGit.App.CommandLine;
using FlickGit.Cli;
using FlickGit.Ipc;
using FlickGit.App.Localization;
using FlickGit.App.Mac.Views;
using FlickGit.App.Resident;
using FlickGit.App.Settings;
using FlickGit.App.ViewModels;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace FlickGit.App.Mac;

/// <summary>
/// The Avalonia application object, and the process lifecycle. Nothing else — the same division
/// <c>App.xaml.cs</c> keeps on Windows.
/// </summary>
public sealed class App : Application
{
    private ServiceProvider? _services;
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// Kept for the life of the process. A <see cref="Avalonia.Controls.TrayIcon"/> that goes out of
    /// scope is collected, and the status item disappears from the menu bar with it.
    /// </summary>
    private Avalonia.Controls.TrayIcon? _menuBar;

    /// <summary>
    /// The Carbon hotkeys. Held for the same reason: disposing it unregisters them, and letting it be
    /// collected would unregister them at a moment nothing chose.
    /// </summary>
    private GlobalHotkey? _hotkeys;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            FlickSettings settings = FlickSettings.Load(out string? error);

            Strings.Use(settings.Language);

            _services = Composition.Build(settings);

            if (error is not null)
                _services.GetRequiredService<ILog>().Warn(error);

            //<b>Explicit shutdown, and this is the difference between an application and a service.</b>
            //Avalonia's default is OnLastWindowClose, which for a resident process means closing the
            //commit window kills the socket server with it — every later `flick` then pays a cold
            //start with no explanation. Found by running it: the process exited cleanly the moment its
            //one window went away, and the next verb was answered by the CLI's own refusals.
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;

            //The socket first: it is the reason this process exists, and a window is optional.
            StartServing();

            //Then the two ways in that do not go through the socket at all. Both after the socket, so
            //a hotkey pressed during startup is served by the same code path as one pressed an hour
            //later.
            StartMenuBar(desktop);

            //Guarded at the call site rather than inside: everything StartHotkeys touches is Carbon,
            //and the platform check has to be somewhere the analyser can see it.
            if (OperatingSystem.IsMacOS())
                StartHotkeys();

            //A path on the command line means "open the commit window here", which is how the Finder
            //extension and the hotkey will launch it. Started with none, it is a service with no
            //window, waiting.
            if (desktop.Args is { Length: > 0 } arguments)
                BuildCommitWindow(arguments).Show();

            //Disposed with the process rather than with the window: the resident service outlives any
            //one window.
            desktop.Exit += (_, _) =>
            {
                _stopping.Cancel();

                //Before the container: unregistering the hotkeys and dropping the status item are
                //both calls into AppKit, and doing them after the log has been disposed would leave a
                //failure in either with nowhere to be recorded.
                if (OperatingSystem.IsMacOS())
                    _hotkeys?.Dispose();

                _menuBar?.Dispose();

                _services.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The menu bar item, and the four things it offers.
    ///
    /// <b>It is also where a notification comes from</b>, which is why the notifier is handed the
    /// item rather than creating one: the commit window closes itself on success by default, so
    /// without somewhere for an outcome to land a successful commit would leave no trace at all.
    ///
    /// Built here rather than in <see cref="Composition"/> for the reason the settings window is
    /// handed in there: this is the composition root's job, and Exit is a lifetime decision that
    /// belongs to whoever owns the lifetime.
    /// </summary>
    private void StartMenuBar(IClassicDesktopStyleApplicationLifetime desktop)
    {
        ServiceProvider services = _services!;

        var recent = services.GetRequiredService<RecentRepositories>();
        var environment = services.GetRequiredService<IEnvironmentVerbs>();

        _menuBar = MenuBar.Create(
            recent: () => recent.Paths,
            onOpenRecent: path => _ = RunVerbAsync(new Verb(VerbKind.Commit, path, null)),

            onSettings: () => _ = RunVerbAsync(new Verb(VerbKind.Settings, null, null)),

            //About is a tab of that window rather than a notice of its own, so the version, the help
            //page and the repository link live in one place.
            onAbout: () => environment.Settings(Output(), SettingsTab.About),

            //Shutdown rather than closing a window: ShutdownMode is OnExplicitShutdown, which is what
            //keeps the socket alive when the last window goes away — so this is the only thing in the
            //product that ends the resident process.
            onExit: () => desktop.Shutdown(ExitCodes.Success));

        services.GetRequiredService<MenuBarNotifier>().Item = _menuBar;
    }

    /// <summary>
    /// The two global hotkeys: Cmd+Alt+G opens the commit window on the folder Finder is showing,
    /// Cmd+Alt+R opens the palette.
    ///
    /// <b>A hotkey that could not be registered is a feature the user does not have, not a reason to
    /// refuse to start.</b> <see cref="GlobalHotkey.Install"/> answers with a sentence rather than
    /// throwing, and it is logged: another application holding Cmd+Alt+G is a configuration, and the
    /// socket, the menu bar and every window still work without it.
    ///
    /// <b>The commit hotkey opens nothing when Finder is not in front.</b> That is CLAUDE.md's rule
    /// rather than a limitation of the resolver — acting on a repository the user is not looking at
    /// is the one thing a global trigger must never do — which is why there is no fallback to the
    /// working directory here.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private void StartHotkeys()
    {
        ServiceProvider services = _services!;

        var log = services.GetRequiredService<ILog>();
        var finder = services.GetRequiredService<FinderFolder>();

        _hotkeys = new GlobalHotkey(log);

        _hotkeys.CommitRequested += () =>
        {
            //Off the run loop immediately. The resolver starts a process and waits on Finder, and
            //the Carbon handler this is raised from is the loop that delivers every other keystroke.
            //
            //`async` rather than returning the inner task: Task.Run over a Func<Task> would hand back
            //a Task<Task> whose inner failure nobody observes, which is the shape that turns a logged
            //error into a silent one.
            _ = Task.Run(async () =>
            {
                if (finder.Resolve() is not { Length: > 0 } folder)
                {
                    log.Debug("The commit hotkey was pressed with no Finder folder in front of it.");

                    return;
                }

                await RunVerbAsync(new Verb(VerbKind.Commit, folder, null)).ConfigureAwait(false);
            });
        };

        _hotkeys.PaletteRequested += () => _ = RunVerbAsync(new Verb(VerbKind.Palette, null, null));

        if (_hotkeys.Install() is { } failure)
            log.Warn(failure);
    }

    /// <summary>
    /// Runs a verb this process started itself, through the same <see cref="VerbRunner"/> the socket
    /// uses. One route to Git, so the menu bar and the hotkey cannot become shortcuts around the
    /// safety rules that route enforces.
    /// </summary>
    private async Task RunVerbAsync(Verb verb)
    {
        ServiceProvider services = _services!;

        try
        {
            await services.GetRequiredService<VerbRunner>().RunAsync(verb, Output()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            //Swallowed for the reason the socket handler swallows: this is a fire-and-forget click
            //handler, and an exception escaping it reaches the dispatcher and takes the resident
            //process with it.
            services.GetRequiredService<ILog>().Error($"Running `{verb.Kind}` failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Somewhere for a verb this process launched to answer. The notifier travels with it because an
    /// ordinary outcome is a notification rather than a window — see <c>VerbOutput.Say</c>.
    /// </summary>
    private VerbOutput Output() =>
        VerbOutput.Direct(
            _services!.GetRequiredService<INotifier>(),
            _services!.GetRequiredService<IDialogs>());

    /// <summary>
    /// Serves the local socket for the life of the process.
    ///
    /// The same handler shape the CLI uses when it plays host: parse with the same parser, run
    /// through the same <see cref="VerbRunner"/>, capture the output for the client. What differs is
    /// that a window verb here actually opens a window — <c>MacWindowVerbs</c> rather than the CLI's
    /// refusals — which is the whole point of the resident process.
    ///
    /// Failures are logged and swallowed: a service that stopped listening because one request threw
    /// would turn every later `flick` into a cold start with no explanation.
    /// </summary>
    private void StartServing()
    {
        ServiceProvider services = _services!;

        var endpoint = services.GetRequiredService<LocalEndpoint>();
        var runner = services.GetRequiredService<VerbRunner>();
        var notifier = services.GetRequiredService<INotifier>();
        var dialogs = services.GetRequiredService<IDialogs>();
        var log = services.GetRequiredService<ILog>();

        _ = Task.Run(() => endpoint.ServeAsync(
            async request =>
            {
                Verb verb = Verb.Parse(request.Arguments, request.WorkingDirectory);
                VerbOutput captured = VerbOutput.ForClient(notifier, dialogs, request.HasConsole);

                try
                {
                    VerbResult result = await runner.RunAsync(verb, captured).ConfigureAwait(false);

                    return new IpcResponse(result.Code, captured.Output, captured.Error);
                }
                catch (Exception ex)
                {
                    log.Error($"Serving `{verb.Kind}` failed: {ex.Message}");

                    return new IpcResponse(ExitCodes.GitError, captured.Output, ex.Message);
                }
            },
            _stopping.Token));
    }

    /// <summary>
    /// The commit window for the folder this was launched on, or for the working directory.
    ///
    /// <b>Resolving the repository blocks here, and only here.</b> The window cannot be populated
    /// without a root and there is nothing to show in the meantime; every later refresh is async.
    /// A folder that is not a repository is currently an empty window rather than the clone dialog
    /// the Windows host offers — that dialog is part of the remaining-windows pass.
    /// </summary>
    private CommitWindow BuildCommitWindow(string[] arguments)
    {
        ServiceProvider services = _services!;

        var repositories = services.GetRequiredService<RepositoryService>();
        var viewModel = services.GetRequiredService<CommitViewModel>();

        string path = arguments.Length > 0 ? arguments[0] : Environment.CurrentDirectory;

        RepositoryInfo? repository = repositories
            .ResolveAsync(path, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var window = new CommitWindow(viewModel, services.GetRequiredService<CommandLine.IDialogs>());

        if (repository is not null)
        {
            //Reset before Show: the view model is built once and re-populated per repository, which is
            //what lets the window be pre-warmed later. Through the window rather than straight at the
            //view model, because the diff pane has to be cleared in the same breath -- otherwise the
            //new repository opens with the previous one's file still rendered in it.
            window.Reset(repository);

            _ = window.RefreshAsync();
        }

        return window;
    }
}
