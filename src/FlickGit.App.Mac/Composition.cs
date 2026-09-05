using FlickGit.Actions;
using FlickGit.Ai;
using FlickGit.App.Ai;
using FlickGit.App.Resident;
using FlickGit.App.CommandLine;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.App.Mac.Views;
using FlickGit.App.Settings;
using FlickGit.App.ViewModels;
using FlickGit.Blame;
using FlickGit.Branches;
using FlickGit.Commits;
using FlickGit.Clone;
using FlickGit.Config;
using FlickGit.Diagnostics;
using FlickGit.Diff;
using FlickGit.Files;
using FlickGit.Forges;
using FlickGit.Git;
using FlickGit.History;
using FlickGit.Logging;
using FlickGit.Merges;
using FlickGit.Palette;
using FlickGit.Pulls;
using FlickGit.Remotes;
using FlickGit.Repositories;
using FlickGit.Stashes;
using FlickGit.Submodules;
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
        services.AddSingleton<PrimaryBranchFlow>();
        services.AddSingleton<PushService>();
        services.AddSingleton<RemoteService>();
        services.AddSingleton<PullService>();
        services.AddSingleton<TagService>();
        services.AddSingleton<StashService>();
        services.AddSingleton<SubmoduleService>();
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
        services.AddSingleton<CloneService>();

        //Both clients registered whichever forge this machine happens to use: they are two small
        //objects over the shared HttpClient, and a registration that depended on a repository's
        //remote could not be a singleton at all.
        services.AddSingleton<IPullRequestClient, GitHubClient>();
        services.AddSingleton<IPullRequestClient, AzureDevOpsClient>();
        services.AddSingleton<PullRequestClients>();
        services.AddSingleton<PullRequestService>();
        services.AddSingleton<PullRequestFlow>();
        services.AddSingleton<GitCredentialFill>();
        services.AddSingleton<ISecretPrompt, AvaloniaSecretPrompt>();
        services.AddSingleton<ForgeCredentials>();

        services.AddSingleton<MenuBarNotifier>();
        services.AddSingleton<INotifier>(provider => provider.GetRequiredService<MenuBarNotifier>());
        services.AddSingleton<IDialogs, AvaloniaDialogs>();

        if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IAutostart, LaunchAgentAutostart>();
            services.AddSingleton<ITrash, FinderTrash>();
            services.AddSingleton<ISecretStore, KeychainSecretStore>();
        }
        else
        {
            //A development run on a machine with no launchd and no Finder. The window layer is what
            //is under test there; nothing that needs either of these is reachable from it.
            services.AddSingleton<IAutostart, UnsupportedAutostart>();
            services.AddSingleton<ITrash, UnsupportedTrash>();

            //No Keychain here either, so AiConfiguration.HasKey answers false -- the same answer a
            //Mac with no key stored would give, which is what keeps a development run usable.
            services.AddSingleton<ISecretStore, UnavailableSecretStore>();
        }

        services.AddSingleton<DiffCache>();
        services.AddSingleton<CommitViewModel>();
        services.AddSingleton<PaletteViewModel>();

        //The verb layer, so this process can answer what `flick` forwards to it. The same routing
        //the CLI uses -- one route to Git, so the GUI cannot become a shortcut around the safety
        //rules that route enforces.
        services.AddSingleton<EnvironmentReports>();
        //The settings window is handed in rather than referenced: FlickGit.App.Mac.Platform has no UI
        //toolkit, which is the whole reason the launchd and socket code lives there.
        //
        //One window, reused while it is open. A second Settings request has to reach the one already
        //on screen, or the user ends up with two of them disagreeing about what the checkboxes say.
        services.AddSingleton<IEnvironmentVerbs>(provider =>
        {
            SettingsWindow? open = null;

            return new MacEnvironmentVerbs(provider.GetRequiredService<EnvironmentReports>())
            {
                OpenSettings = tab => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (open is null)
                    {
                        open = new SettingsWindow(
                            settings,
                            provider.GetRequiredService<IAutostart>(),
                            provider.GetRequiredService<ISecretStore>(),
                            provider.GetRequiredService<ISecretPrompt>());

                        open.Closed += (_, _) => open = null;
                        open.Show();
                    }
                    else
                    {
                        open.Activate();
                    }

                    open.Select(tab);
                }),
            };
        });
        services.AddSingleton<IWindowVerbs, MacWindowVerbs>();
        services.AddSingleton<RepositoryVerbs>();
        services.AddSingleton<ActionRunner>();
        services.AddSingleton<VerbRunner>();
        services.AddSingleton<LocalEndpoint>();

        services.AddSingleton<Func<VerbRunner>>(provider => provider.GetRequiredService<VerbRunner>);
        services.AddSingleton<Func<ActionRunner>>(provider => provider.GetRequiredService<ActionRunner>);

        //<b>Validated eagerly.</b> A missing registration is otherwise a run-time failure at the
        //moment the graph is first walked -- which for the resident service meant it died on its
        //first request and every later `flick` silently fell back to the CLI's own refusals. That is
        //exactly how a whole window looked unwired when the real fault was one absent line.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
