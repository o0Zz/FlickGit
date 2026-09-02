using FlickGit.Actions;
using FlickGit.App.CommandLine;
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

        await using ServiceProvider services = BuildServices(settings);

        if (settingsError is not null)
            services.GetRequiredService<ILog>().Warn(settingsError);

        VerbOutput output = VerbOutput.Direct(
            services.GetRequiredService<INotifier>(),
            services.GetRequiredService<IDialogs>());

        VerbResult result = await services
            .GetRequiredService<VerbRunner>()
            .RunAsync(verb, output)
            .ConfigureAwait(false);

        return result.Code;
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

        services.AddSingleton<UpstreamConsent>();
        services.AddSingleton<RecentRepositories>();

        //The three seams. A CLI has no notification area and no windows, and every verb that needs
        //one is refused by name rather than silently doing nothing.
        services.AddSingleton<INotifier, SilentNotifier>();
        services.AddSingleton<IDialogs, ConsoleDialogs>();
        services.AddSingleton<UnavailableVerbs>();
        services.AddSingleton<IEnvironmentVerbs>(provider => provider.GetRequiredService<UnavailableVerbs>());
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
