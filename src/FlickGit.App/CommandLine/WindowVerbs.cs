using System.Diagnostics;
using System.IO;
using System.Windows;
using FlickGit.App.Localization;
using FlickGit.App.Resident;
using FlickGit.App.Views;
using FlickGit.Branches;
using FlickGit.Cli;
using FlickGit.App.Settings;
using FlickGit.Blame;
using FlickGit.Clone;
using FlickGit.Config;
using FlickGit.Diagnostics;
using FlickGit.App.Ai;
using FlickGit.Diff;
using FlickGit.Forges;
using FlickGit.History;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Pulls;
using FlickGit.Remotes;
using FlickGit.Stashes;
using FlickGit.Status;
using FlickGit.Submodules;
using FlickGit.Tags;
using FlickGit.Worktrees;

namespace FlickGit.App.CommandLine;

/// <summary>
/// The verbs that put something on screen and leave it there.
///
/// They all return <see cref="VerbResult.Stay"/>: the process must outlive the call, because the
/// window is the answer. The CLI stub deliberately does not wait -- waiting would block a terminal
/// until the user closed the commit window, and leave Explorer holding a process per right-click.
///
/// Windows are constructed with <c>new</c> rather than resolved, which is not a lapse from Hard
/// Requirement 3: a window is not a collaborator, it is the output.
/// </summary>
public sealed class WindowVerbs(
    CommitWindowHost commitWindow,
    PaletteWindowHost palette,
    StatusService status,
    SwitchService switches,
    BranchService branches,
    WorktreeService worktrees,
    PullService pulls,
    CloneService clones,
    TagService tags,
    StashService stashes,
    SubmoduleService submodules,
    RepositoryConfigService repositoryConfig,
    RemoteService remotes,
    HistoryService history,
    BlameService blame,
    DiffService diffs,
    PullRequestService pullRequests,
    PullRequestFlow pullRequestFlow,
    ForgeCredentials forgeCredentials,
    AiTextService ai,
    UpstreamConsent upstreamConsent,
    Notifier notifier,
    FlickSettings settings,
    OperationTimings timings,
    ILog log)
{
    /// <summary>The commit window. Also where `flick status` lands when there is no console.</summary>
    public async Task<VerbResult> CommitAsync(VerbOutput output, RepositoryInfo repository)
    {
        if (repository.IsBare)
        {
            output.Say(Strings.Get("app.name"), Strings.Get("error.bare", repository.Root));
            return VerbResult.Exit(ExitCodes.NotARepository);
        }

        //Through the host, always: it owns the one window and knows whether this process is the resident
        //service (reuse it) or a one-shot launch (build it now).
        await commitWindow.ShowAsync(repository).ConfigureAwait(true);

        return VerbResult.Stay();
    }

    /// <summary>
    /// The repository palette. Takes no repository: its whole job is to find one, which is why it is
    /// the only window verb with nothing to guard against.
    /// </summary>
    public async Task<VerbResult> PaletteAsync()
    {
        await palette.ShowAsync().ConfigureAwait(true);
        return VerbResult.Stay();
    }

    /// <summary>
    /// The log window. Constructed per call rather than pre-warmed: a warm instance would be a window
    /// full of per-use state -- loaded pages, a selection, a range, a diff cache, an in-flight token --
    /// that has to be provably reset between two uses, for a surface with no latency budget.
    ///
    /// No bare-repository guard, unlike <see cref="CommitAsync"/>: a bare repository has no working
    /// tree but it does have history, and this is the one window that can show it.
    /// </summary>
    public async Task<VerbResult> LogAsync(RepositoryInfo repository)
    {
        var clock = Stopwatch.StartNew();

        var window = new LogWindow(repository, history, diffs, blame, ai, settings, timings, log);

        AppWindow.Present(window);

        timings.Record("window.log.visible", clock.Elapsed);

        //Awaited rather than left running, so "visible" and "usable" are two budgets.
        await window.LoadFirstPageAsync().ConfigureAwait(true);

        timings.Record("window.log.populated", clock.Elapsed);

        return VerbResult.Stay();
    }

    /// <summary>
    /// The blame window. The only verb whose path is a <b>file</b> rather than a directory, which is
    /// why it is the only one that has to say so when handed the wrong kind.
    /// </summary>
    public async Task<VerbResult> BlameAsync(VerbOutput output, RepositoryInfo repository, string path)
    {
        string full = Path.GetFullPath(path);

        if (Directory.Exists(full))
        {
            output.Say(Strings.Get("app.name"), Strings.Get("blame.notafile", full));
            return VerbResult.Exit(ExitCodes.NotARepository);
        }

        //Git speaks repository-relative paths with forward slashes, whatever Explorer handed over.
        string relative = Path.GetRelativePath(repository.Root, full).Replace('\\', '/');

        var clock = Stopwatch.StartNew();

        var window = new BlameWindow(repository, relative, revision: null, blame, settings, timings, log);

        AppWindow.Present(window);

        timings.Record("window.blame.visible", clock.Elapsed);

        await window.LoadAsync().ConfigureAwait(true);

        timings.Record("window.blame.populated", clock.Elapsed);

        return VerbResult.Stay();
    }

    /// <summary>
    /// The pull-request window. Per call rather than pre-warmed, for the reason the tag and repository
    /// windows are.
    ///
    /// No bare-repository guard: a bare repository has branches and a remote, and proposing one branch
    /// into another is a question about refs rather than about files.
    /// </summary>
    public async Task<VerbResult> PullRequestAsync(RepositoryInfo repository)
    {
        var clock = Stopwatch.StartNew();

        var window = new PullRequestWindow(
            repository,
            pullRequests,
            pullRequestFlow,
            forgeCredentials,
            ai,
            status,
            upstreamConsent,
            notifier,
            settings,
            log);

        AppWindow.Present(window);

        timings.Record("window.pr.visible", clock.Elapsed);

        await window.LoadAsync().ConfigureAwait(true);

        timings.Record("window.pr.populated", clock.Elapsed);

        return VerbResult.Stay();
    }

    /// <summary>`flick pull-rebase`, with the submodule update as a distinct step.</summary>
    public async Task<VerbResult> PullAsync(RepositoryInfo repository)
    {
        var window = new ProgressWindow(Strings.Get("pull.title", repository.Name));
        AppWindow.Present(window);

        PullOutcome outcome;

        try
        {
            //The window's own token, so its Cancel button and Esc reach the git process rather than
            //being decoration over an operation nothing can stop.
            outcome = await pulls
                .PullRebaseAsync(repository, new Progress<string>(window.AddStep), window.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            //Nothing is undone here. A cancelled `pull --rebase` can leave a rebase in progress, and
            //CLAUDE.md is explicit that FlickGit does not abort one on the user's behalf -- so the
            //window says to look rather than tidying up behind Git's back.
            window.Cancelled(Strings.Get("pull.cancelled"));
            return VerbResult.Stay(ExitCodes.UserCancelled);
        }

        if (!outcome.Succeeded)
        {
            window.Fail(
                //Both arms are sentences about the pull. This said error.title on the second one, which
                //put "FlickGit" on screen as the result of the operation.
                outcome.StoppedOnConflict ? Strings.Get("pull.conflict") : Strings.Get("pull.failed"),
                outcome.GitError ?? string.Empty,
                outcome.Suggestion);

            //The window stays open with the reason on it, so the exit code is remembered rather than acted on.
            return VerbResult.Stay(ExitCodes.GitError);
        }

        if (outcome.SubmoduleError is not null)
        {
            //The pull succeeded. Reported as a warning on a successful operation, never as a failure: a
            //submodule failure does not roll back the pull.
            window.Warn(Strings.Get("pull.submodule.failed"), outcome.SubmoduleError);
            return VerbResult.Stay();
        }

        if (settings.ClosePullWindowAfterSuccess)
        {
            //A clean pull leaves nothing on this window worth reading, so it goes rather than waiting for
            //a keystroke that can only mean "yes". Only this branch: the two above have something to
            //report, and a window that closes cannot report it.
            window.Close();
            return VerbResult.Stay();
        }

        window.Succeed(Strings.Get("pull.success"));
        return VerbResult.Stay();
    }

    public async Task<VerbResult> SwitchPickerAsync(RepositoryInfo repository)
    {
        RepositoryStatus state = await status
            .GetStatusAsync(repository, CancellationToken.None)
            .ConfigureAwait(true);

        var picker = new SwitchBranchWindow(repository, switches, branches, worktrees, state.Branch);

        AppWindow.Present(picker);

        return VerbResult.Stay();
    }

    /// <summary>
    /// The tag window. Per call rather than pre-warmed: not on any latency budget, and a window kept
    /// for the session is a window whose state has to be provably reset between two uses.
    /// </summary>
    public VerbResult TagPicker(RepositoryInfo repository)
    {
        var window = new TagsWindow(repository, tags, switches);

        AppWindow.Present(window);

        return VerbResult.Stay();
    }

    /// <summary>
    /// The stash window. Per call, for the reason <see cref="TagPicker"/> gives.
    /// </summary>
    public VerbResult StashPicker(RepositoryInfo repository)
    {
        var window = new StashesWindow(repository, stashes);

        AppWindow.Present(window);

        return VerbResult.Stay();
    }

    /// <summary>
    /// The submodules window. Per call, for the reason <see cref="TagPicker"/> gives.
    ///
    /// The commit hand-off is wired here rather than inside the window: the window has staged
    /// something and knows nothing about how a commit surface is reached, and the resident service's
    /// pre-warmed commit window is exactly the kind of collaborator a view is not given.
    /// </summary>
    public VerbResult Submodules(RepositoryInfo repository)
    {
        var window = new SubmodulesWindow(repository, submodules);

        //Discarded rather than awaited in an async lambda: the event is an Action, so `async () =>`
        //would be async void and a fault there takes the process down. The palette's own handler
        //does the same.
        window.CommitRequested += () => _ = commitWindow.ShowAsync(repository);

        AppWindow.Present(window);

        return VerbResult.Stay();
    }

    public VerbResult Repo(RepositoryInfo repository)
    {
        var window = new RepositoryWindow(repository, repositoryConfig, remotes);

        AppWindow.Present(window);

        return VerbResult.Stay();
    }

    /// <summary>
    /// The clone dialog. Unlike every other verb this one wants a folder that is <i>not</i> a
    /// repository, so none is resolved -- and cloning into a subdirectory of an existing one is legal
    /// and occasionally intended, so it is not refused either.
    /// </summary>
    public VerbResult Clone(string path, string? url)
    {
        string parent = Directory.Exists(path) ? path : Environment.CurrentDirectory;

        var window = new CloneWindow(parent, clones, log, url ?? ReadClipboard());

        AppWindow.Present(window);

        return VerbResult.Stay();
    }

    public VerbResult Terminal(VerbOutput output, string? path)
    {
        string directory = TerminalDirectory(path ?? Environment.CurrentDirectory);

        //Windows Terminal when present, the shell's own default otherwise. UseShellExecute is required
        //here and only here: it is what lets Windows resolve wt.exe through the app-execution alias,
        //which is not on PATH as a real file.
        //
        //`wt.exe` needs `-d`: a Windows Terminal profile carries its own `startingDirectory`, defaulting
        //to %USERPROFILE%, which wins over whatever directory the process was started in. Without the
        //argument every terminal opened in the home folder. WorkingDirectory is still set, because it is
        //all powershell.exe reads.
        (string Executable, string[] Arguments)[] terminals =
        [
            ("wt.exe", ["-d", directory]),
            ("powershell.exe", []),
        ];

        foreach ((string executable, string[] arguments) in terminals)
        {
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = directory,
                    UseShellExecute = true,
                };

                //ArgumentList rather than a command-line string: a folder containing a space, or ending in a
                //backslash, is quoted by the framework rather than by us.
                foreach (string argument in arguments)
                    start.ArgumentList.Add(argument);

                Process.Start(start);

                return VerbResult.Exit(ExitCodes.Success);
            }
            catch (Exception ex)
            {
                log.Debug($"Could not start {executable}: {ex.Message}");
            }
        }

        output.Fail(Strings.Get("app.name"), $"No terminal could be started in:\n\n{directory}");
        return VerbResult.Exit(ExitCodes.ConfigurationError);
    }

    /// <summary>
    /// The folder a terminal should start in. <c>C:</c> is not the root of the drive: it is the
    /// drive-<i>relative</i> path, meaning whichever directory happens to be current on C:, which is
    /// how a right-click on a drive root opened a terminal somewhere else entirely.
    /// </summary>
    private static string TerminalDirectory(string path)
    {
        string directory = path.Trim().Trim('"');

        return directory.Length == 2 && directory[1] == ':'
            ? directory + Path.DirectorySeparatorChar
            : directory;
    }

    /// <summary>
    /// The clipboard, for the clone prefill. Read on the UI thread because the clipboard is STA-bound,
    /// and a failure only means no prefill.
    /// </summary>
    private string? ReadClipboard()
    {
        try
        {
            return Clipboard.ContainsText() ? Clipboard.GetText() : null;
        }
        catch (Exception ex)
        {
            log.Debug($"Clipboard read failed: {ex.Message}");
            return null;
        }
    }
}
