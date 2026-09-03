using FlickGit.Actions;
using FlickGit.Ai;
using FlickGit.App.Ai;
using FlickGit.App.Resident;
using FlickGit.App.CommandLine;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.App.Settings;
using FlickGit.App.ViewModels;
using FlickGit.Blame;
using FlickGit.Branches;
using FlickGit.Commits;
using FlickGit.Config;
using FlickGit.Diagnostics;
using FlickGit.Diff;
using FlickGit.Files;
using FlickGit.Git;
using FlickGit.History;
using FlickGit.Logging;
using FlickGit.Merges;
using FlickGit.Palette;
using FlickGit.Pulls;
using FlickGit.Remotes;
using FlickGit.Repositories;
using FlickGit.Stashes;
using FlickGit.Status;
using FlickGit.Tags;
using Microsoft.Extensions.DependencyInjection;

namespace FlickGit.App.Mac;

/// <summary>
/// This process's composition root, and the only file here that mentions the container.
///
/// It is a third root — beside the Windows one and the CLI's — and that is still deliberate. Hard
/// Requirement 3 asks for one place per application, which this is. Folding the Core half into one
/// shared registration is worth doing when the two front ends have converged enough that the lists
/// are actually the same; doing it while the macOS windows are still being written would mean
/// rewriting the working Windows root to match a guess.
/// </summary>
internal static class Composition
{
    public static ServiceProvider Build(FlickSettings settings)
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

        //Where the user profile is, and the string table -- the two things FlickGit.Core cannot reach,
        //handed in as plain values exactly as the other two hosts hand them.
        services.AddSingleton(provider => new ActionCatalog(
            FlickSettings.ActionsFilePath,
            Strings.Get,
            provider.GetRequiredService<ILog>()));

        services.AddSingleton(provider => new PromptStore(
            FlickSettings.DirectoryPath,
            provider.GetRequiredService<ILog>()));

        services.AddSingleton<RepositoryConfigService>();
        services.AddSingleton<BranchService>();
        services.AddSingleton<SwitchService>();
        services.AddSingleton<PushService>();
        services.AddSingleton<RemoteService>();
        services.AddSingleton<PullService>();
        services.AddSingleton<TagService>();
        services.AddSingleton<StashService>();
        services.AddSingleton<TrackingService>();
        services.AddSingleton<CommitService>();
        services.AddSingleton<CommitFlow>();
        services.AddSingleton<HistoryService>();
        services.AddSingleton<BlameService>();
        services.AddSingleton<DiffService>();
        services.AddSingleton<FileTextLoader>();
        services.AddSingleton<WorkingTreeWriter>();
        services.AddSingleton<RestoreService>();
        services.AddSingleton<PatchService>();
        services.AddSingleton<RepositoryScanner>();
        services.AddSingleton<RepositoryOverviewCache>();

        services.AddSingleton<UpstreamConsent>();
        services.AddSingleton<RecentRepositories>();
        services.AddSingleton<EditorLauncher>();

        //The AI, whose connection and provider choice are shared with the Windows host so a provider
        //cannot work on one platform and fall through to disabled on the other.
        services.AddSingleton<AiConfiguration>();
        services.AddSingleton<AiContextBuilder>();
        services.AddSingleton(_ => AiHost.CreateHttpClient());
        services.AddSingleton(provider => AiHost.For(
            provider.GetRequiredService<AiConfiguration>(),
            provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<ILog>()));
        services.AddSingleton<AiTextService>();

        //The platform seams.
        services.AddSingleton<MenuBarNotifier>();
        services.AddSingleton<INotifier>(provider => provider.GetRequiredService<MenuBarNotifier>());
        services.AddSingleton<IDialogs, AvaloniaDialogs>();

        if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IAutostart, LaunchAgentAutostart>();
            services.AddSingleton<ITrash, FinderTrash>();
        }
        else
        {
            //A development run on a machine with no launchd and no Finder. The window layer is what
            //is under test there; nothing that needs either of these is reachable from it.
            services.AddSingleton<IAutostart, UnsupportedAutostart>();
            services.AddSingleton<ITrash, UnsupportedTrash>();
        }

        //Keychain is the one seam still without a macOS implementation, so the AI reads no key here
        //yet and AiConfiguration.HasKey answers false. That is the same answer a Mac with no key
        //stored would give, which is why the window can be built and exercised without it.
        services.AddSingleton<ISecretStore, UnavailableSecretStore>();

        services.AddSingleton<DiffCache>();
        services.AddSingleton<CommitViewModel>();
        services.AddSingleton<PaletteViewModel>();

        //The verb layer, so this process can answer what `flick` forwards to it. The same routing
        //the CLI uses -- one route to Git, so the GUI cannot become a shortcut around the safety
        //rules that route enforces.
        services.AddSingleton<EnvironmentReports>();
        services.AddSingleton<IEnvironmentVerbs, MacEnvironmentVerbs>();
        services.AddSingleton<IWindowVerbs, MacWindowVerbs>();
        services.AddSingleton<RepositoryVerbs>();
        services.AddSingleton<ActionRunner>();
        services.AddSingleton<VerbRunner>();
        services.AddSingleton<LocalEndpoint>();

        services.AddSingleton<Func<VerbRunner>>(provider => provider.GetRequiredService<VerbRunner>);
        services.AddSingleton<Func<ActionRunner>>(provider => provider.GetRequiredService<ActionRunner>);

        return services.BuildServiceProvider();
    }
}
