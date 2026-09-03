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
using FlickGit.Merges;
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
    PrimaryBranchFlow primaryBranch,
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
    ILog log) : IWindowVerbs
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

    /// <summary>
    /// `flick back` — switch to the primary branch, then pull there. The third root entry.
    ///
    /// The same window as <see cref="PullAsync"/>, because it is the same kind of operation: several
    /// steps, a network step among them, and an outcome worth reading. What it adds is the one
    /// question — <see cref="PrimaryBranchFlow"/> refuses a switch that local changes block, and only
    /// then offers the branch picker's own stash path, through the same
    /// <c>SwitchService.StashSwitchRestoreAsync</c>. It is never taken on the user's behalf.
    /// </summary>
    public async Task<VerbResult> BackAsync(RepositoryInfo repository)
    {
        var window = new ProgressWindow(Strings.Get("back.title", repository.Name));
        AppWindow.Present(window);

        var request = new PrimaryBranchRequest
        {
            Repository = repository,
            ConfiguredPrimaryBranch = settings.PrimaryBranch,
            Progress = new Progress<string>(window.AddStep),

            //Onto the dispatcher, and that is not a formality -- CommitWindow learned it the hard
            //way and says so. The flow raises this from wherever its own ConfigureAwait(false) left
            //it, which is a thread-pool thread from the first git.exe call onwards, and constructing
            //a Window there throws "the calling thread must be STA". ShowDialog pumps its own message
            //loop, so the flow waits on the dialog rather than the dialog blocking the pump.
            //
            //ConfirmWindow directly rather than IDialogs, because IDialogs deliberately takes no
            //owner: a question about this operation belongs over the window running it.
            //
            //Enter does not accept. Not because stashing is destructive -- it discards nothing, and
            //Git refuses rather than overwriting on the way back -- but because the affirmative
            //answer moves the user's uncommitted work somewhere they did not put it.
            Confirm = (question, _) => window.Dispatcher.InvokeAsync(() => ConfirmWindow.Ask(
                window,
                Strings.Get("switch.stash"),
                Strings.Get("back.stash.ask", question.Branch, string.Join('\n', question.BlockingFiles)),
                Strings.Get("switch.stash"),
                Strings.Get("common.cancel"))).Task,
        };

        PrimaryBranchResult result;

        try
        {
            result = await primaryBranch.RunAsync(request, window.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            //Nothing is undone, for the reason PullAsync gives: a cancelled `pull --rebase` can leave
            //a rebase in progress, and FlickGit does not abort one on the user's behalf.
            window.Cancelled(Strings.Get("pull.cancelled"));
            return VerbResult.Stay(ExitCodes.UserCancelled);
        }

        switch (result.Outcome)
        {
            case PrimaryBranchOutcome.OperationInProgress:
                window.Fail(
                    Strings.Get("back.inprogress", Strings.Get(result.InProgress switch
                    {
                        MergeOperation.Merge => "conflict.name.merge",
                        MergeOperation.Rebase => "conflict.name.rebase",
                        MergeOperation.CherryPick => "conflict.name.cherrypick",
                        _ => "conflict.name.revert",
                    })),
                    string.Empty,
                    suggestion: null);

                return VerbResult.Stay(ExitCodes.RefusedForSafety);

            case PrimaryBranchOutcome.SwitchRefused:
                //The heading, then Git's own file list as the detail -- the split SwitchBranchWindow
                //already makes, rather than one string with the list folded into it.
                window.Fail(
                    Strings.Get("branch.blocked", string.Empty).TrimEnd('\n'),
                    string.Join('\n', result.Files),
                    Strings.Get("branch.blocked.hint"));

                return VerbResult.Stay(ExitCodes.RefusedForSafety);

            case PrimaryBranchOutcome.SwitchFailed:
                window.Fail(
                    Strings.Get("back.switch.failed", result.Branch),
                    result.Detail ?? string.Empty,
                    suggestion: null);

                return VerbResult.Stay(ExitCodes.GitError);

            case PrimaryBranchOutcome.StashSwitchFailed:
                //The one outcome that must never be reported vaguely: the switch may have happened
                //and the user's work may be sitting in a stash. The reference is the actionable part.
                window.Fail(
                    Strings.Get("back.stash.failed"),
                    result.Detail ?? string.Empty,
                    result.StashRef is { Length: > 0 } reference
                        ? Strings.Get("switch.stashkept", reference)
                        : null);

                return VerbResult.Stay(ExitCodes.GitError);

            case PrimaryBranchOutcome.PullFailed:
                window.Fail(
                    result.StoppedOnConflict ? Strings.Get("pull.conflict") : Strings.Get("pull.failed"),
                    result.Detail ?? string.Empty,
                    result.Suggestion);

                return VerbResult.Stay(ExitCodes.GitError);
        }

        if (result.SubmoduleError is not null)
        {
            //The switch and the pull both worked. A stale submodule is a warning on top of that,
            //never a failure -- reporting it as one would invite undoing a pull that was fine.
            window.Warn(Strings.Get("pull.submodule.failed"), result.SubmoduleError);
            return VerbResult.Stay();
        }

        //A stash that round-tripped and a detached HEAD left behind are the two things the user
        //cannot find out anywhere else, so those two keep their window whatever the setting says.
        if (!result.Stashed && result.LeftDetachedAt is null && settings.ClosePullWindowAfterSuccess)
        {
            window.Close();
            return VerbResult.Stay();
        }

        window.Succeed(
            (result.Stashed
                ? Strings.Get("back.success.stashed", result.Branch)
                : Strings.Get("back.success", result.Branch))
            + (result.LeftDetachedAt is { Length: > 0 } head
                ? "\n\n" + Strings.Get("back.leftdetached", head)
                : string.Empty));

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
    /// The stash window. Per call, for the reason <see cref="TagPicker"/> gives -- and with more
    /// force now that it holds a diff cache and a selection of its own.
    /// </summary>
    public VerbResult StashPicker(RepositoryInfo repository)
    {
        var window = new StashesWindow(repository, stashes, diffs, settings, timings, log);

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
        string directory = (path ?? Environment.CurrentDirectory).Trim().Trim('"');

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
