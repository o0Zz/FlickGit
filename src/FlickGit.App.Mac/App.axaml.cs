using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

            desktop.MainWindow = BuildCommitWindow(desktop.Args ?? []);

            //Disposed with the process rather than with the window: the resident service outlives any
            //one window, and this is where that will hang once the socket server moves in here.
            desktop.Exit += (_, _) => _services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
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
            //what lets the window be pre-warmed later.
            viewModel.Reset(repository);

            _ = viewModel.RefreshAsync();
        }

        return window;
    }
}
