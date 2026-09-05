using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using FlickGit.App.CommandLine;
using FlickGit.Cli;
using FlickGit.Ipc;
using FlickGit.App.Localization;
using FlickGit.App.Mac.Views;
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
                _services.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

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
