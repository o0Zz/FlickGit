using Avalonia.Threading;
using FlickGit.App.CommandLine;
using FlickGit.App.Mac.Views;
using FlickGit.App.ViewModels;
using FlickGit.Cli;
using Avalonia.Controls;
using FlickGit.App.Localization;
using FlickGit.App.Settings;
using FlickGit.Blame;
using FlickGit.Branches;
using FlickGit.Clone;
using FlickGit.App.Ai;
using FlickGit.Config;
using FlickGit.Forges;
using FlickGit.Status;
using FlickGit.Diagnostics;
using FlickGit.Diff;
using FlickGit.Pulls;
using FlickGit.Remotes;
using FlickGit.History;
using FlickGit.Logging;
using FlickGit.Merges;
using FlickGit.Models;
using FlickGit.Stashes;
using FlickGit.Submodules;
using FlickGit.Tags;
using FlickGit.Repositories;

namespace FlickGit.App.Mac;

/// <summary>
/// The verbs that open a window, on macOS.
///
/// <b>All fourteen are real.</b> Nothing here raises <see cref="HostCapabilityException"/> any more
/// — the refusal it produced was honest while the windows were being written and would now be a lie.
/// The type stays in the interface's vocabulary because a verb added on Windows shows up here as a
/// compile error, and answering the new one with a refusal is better than answering it with silence.
///
/// <b>Every method hops to the UI thread first.</b> A verb arrives on the socket listener's thread,
/// and constructing a <c>Window</c> from there throws.
///
/// <b>The windows are created per call rather than pre-warmed.</b> Pre-warming is the Windows host's
/// answer to a 120 ms budget it has measured; nothing here has been measured on real hardware yet,
/// and a pre-warmed window that has to be fully re-initialisable is a correctness cost to pay once
/// there is a number saying it is needed.
/// </summary>
public sealed class MacWindowVerbs(
    CommitViewModel commit,
    PaletteViewModel palette,
    RepositoryService repositories,
    HistoryService history,
    DiffService diffs,
    SwitchService switches,
    BranchService branches,
    StashService stashes,
    TagService tags,
    SubmoduleService submodules,
    PullService pulls,
    CloneService clones,
    BlameService blame,
    RepositoryConfigService repositoryConfig,
    RemoteService remoteService,
    PullRequestService pullRequests,
    PullRequestFlow pullRequestFlow,
    ForgeCredentials forgeCredentials,
    AiTextService aiText,
    StatusService status,
    UpstreamConsent consent,
    INotifier notifier,
    OperationTimings timings,
    ILog log,
    PrimaryBranchFlow primaryBranch,
    FlickSettings settings,
    IDialogs dialogs) : IWindowVerbs
{
    /// <summary>The one separator these reports join on. A char, so TrimEnd takes it too.</summary>
    private const char NewLine = '\n';

    public Task<VerbResult> CommitAsync(VerbOutput output, RepositoryInfo repository) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = new CommitWindow(commit, dialogs);

            //Reset before Show and refresh after, which is the whole reason the two are separate: the
            //user sees the right repository name and an empty list immediately, rather than nothing
            //at all until four Git processes have answered.
            window.Reset(repository);
            window.Show();

            await window.RefreshAsync().ConfigureAwait(true);

            //Stay: the window is the output, and shutting down here would close it.
            return VerbResult.Stay(ExitCodes.Success);
        });

    public Task<VerbResult> PaletteAsync() =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            palette.Reset();

            var window = new PaletteWindow(palette);
            window.Show();

            await palette.RefreshAsync().ConfigureAwait(true);

            return VerbResult.Stay(ExitCodes.Success);
        });

    /// <summary>
    /// `flick pull-rebase` — the first of the three root menu entries.
    ///
    /// <b>The window's own token, not None.</b> Its Cancel button and Esc have to reach the git
    /// process rather than being decoration over an operation nothing can stop, which is exactly what
    /// a pull against an unreachable remote turns into.
    /// </summary>
    public Task<VerbResult> PullAsync(RepositoryInfo repository) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = new ProgressWindow(Strings.Get("pull.title", repository.Name));
            window.Show();

            PullOutcome outcome;

            try
            {
                outcome = await pulls
                    .PullRebaseAsync(repository, new Progress<string>(window.AddStep), window.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                //Nothing is undone here. A cancelled `pull --rebase` can leave a rebase in progress,
                //and CLAUDE.md is explicit that FlickGit does not abort one on the user's behalf --
                //so the window says to look rather than tidying up behind Git's back.
                window.Cancelled(Strings.Get("pull.cancelled"));

                return VerbResult.Stay(ExitCodes.UserCancelled);
            }

            if (!outcome.Succeeded)
            {
                //Both arms are sentences about the pull, never a generic error title: CLAUDE.md wants
                //the operation, the Git error and a next action.
                window.Fail(
                    outcome.StoppedOnConflict ? Strings.Get("pull.conflict") : Strings.Get("pull.failed"),
                    outcome.GitError ?? string.Empty,
                    outcome.Suggestion);

                //The window stays open with the reason on it, so the exit code is remembered rather
                //than acted on.
                return VerbResult.Stay(ExitCodes.GitError);
            }

            if (outcome.SubmoduleError is not null)
            {
                //The pull succeeded. Reported as a warning on a successful operation, never as a
                //failure: a submodule failure does not roll back the pull.
                window.Warn(Strings.Get("pull.submodule.failed"), outcome.SubmoduleError);

                return VerbResult.Stay();
            }

            if (settings.ClosePullWindowAfterSuccess)
            {
                //A clean pull leaves nothing on this window worth reading, so it goes rather than
                //waiting for a keystroke that can only mean "yes". Only this branch: the two above
                //have something to report, and a window that closes cannot report it.
                window.Close();

                return VerbResult.Stay();
            }

            window.Succeed(Strings.Get("pull.success"));

            return VerbResult.Stay();
        });

    /// <summary>
    /// `flick back` — switch to the primary branch, then pull there. The third root entry.
    ///
    /// The same window as <see cref="PullAsync"/>, because it is the same kind of operation: several
    /// steps, a network step among them, and an outcome worth reading. What it adds is the one
    /// question — <see cref="PrimaryBranchFlow"/> refuses a switch that local changes block, and only
    /// then offers the branch picker's own stash path. It is never taken on the user's behalf.
    ///
    /// <b>Every arm of the switch below is a refusal or a report</b>, which is the shape of the flow
    /// rather than defensiveness: three of its five steps refuse.
    /// </summary>
    public Task<VerbResult> BackAsync(RepositoryInfo repository) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var window = new ProgressWindow(Strings.Get("back.title", repository.Name));
            window.Show();

            var request = new PrimaryBranchRequest
            {
                Repository = repository,
                ConfiguredPrimaryBranch = settings.PrimaryBranch,
                Progress = new Progress<string>(window.AddStep),

                //Enter does not accept. Not because stashing is destructive -- it discards nothing,
                //and Git refuses rather than overwriting on the way back -- but because the
                //affirmative answer moves the user's uncommitted work somewhere they did not put it.
                Confirm = (question, _) => Dispatcher.UIThread.InvokeAsync(() => MessageWindow.AskAsync(
                    Strings.Get("switch.stash"),
                    Strings.Get("back.stash.ask", question.Branch, string.Join(NewLine, question.BlockingFiles)),
                    Strings.Get("switch.stash"),
                    Strings.Get("common.cancel"),
                    destructive: true)),
            };

            PrimaryBranchResult result;

            try
            {
                result = await primaryBranch.RunAsync(request, window.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
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
                    //The heading, then Git's own file list as the detail -- the split the branch
                    //picker already makes, rather than one string with the list folded into it.
                    window.Fail(
                        Strings.Get("branch.blocked", string.Empty).TrimEnd(NewLine),
                        string.Join(NewLine, result.Files),
                        Strings.Get("branch.blocked.hint"));

                    return VerbResult.Stay(ExitCodes.RefusedForSafety);

                case PrimaryBranchOutcome.SwitchFailed:
                    window.Fail(
                        Strings.Get("back.switch.failed", result.Branch),
                        result.Detail ?? string.Empty,
                        suggestion: null);

                    return VerbResult.Stay(ExitCodes.GitError);

                case PrimaryBranchOutcome.StashSwitchFailed:
                    //The one outcome that must never be reported vaguely: the switch may have
                    //happened and the user's work may be sitting in a stash. The reference is the
                    //actionable part.
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
                    ? NewLine + NewLine + Strings.Get("back.leftdetached", head)
                    : string.Empty));

            return VerbResult.Stay();
        });

    public Task<VerbResult> LogAsync(RepositoryInfo repository) =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            new LogWindow(repository, history, diffs, aiText, blame, settings, timings, log).Show();

            //GetTask, because InvokeAsync over a *synchronous* lambda hands back a
            //DispatcherOperation rather than a Task. The async overloads above unwrap themselves.
            return VerbResult.Stay(ExitCodes.Success);
        }).GetTask();

    /// <summary>
    /// `flick blame &lt;file&gt;` — who last touched each line, and what came before.
    ///
    /// <b>A directory is refused in text rather than opened as an empty window.</b> The verb takes a
    /// file; saying so costs a line and a window that could say nothing costs a click.
    /// </summary>
    public Task<VerbResult> BlameAsync(VerbOutput output, RepositoryInfo repository, string path) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            string full = System.IO.Path.GetFullPath(path);

            if (System.IO.Directory.Exists(full))
            {
                output.Say(Strings.Get("app.name"), Strings.Get("blame.notafile", full));

                return VerbResult.Exit(ExitCodes.NotARepository);
            }

            //Git speaks repository-relative paths with forward slashes, whatever the Finder handed
            //over. Harmless on macOS, where the separator already is one, and kept because the path
            //may have come over the socket from anywhere.
            string relative = System.IO.Path
                .GetRelativePath(repository.Root, full)
                .Replace('\\', '/');

            var clock = System.Diagnostics.Stopwatch.StartNew();

            var window = new BlameWindow(repository, relative, revision: null, blame, settings, timings, log);
            window.Show();

            timings.Record("window.blame.visible", clock.Elapsed);

            await window.LoadAsync().ConfigureAwait(true);

            timings.Record("window.blame.populated", clock.Elapsed);

            return VerbResult.Stay();
        });

    /// <summary>
    /// The pull-request window.
    ///
    /// No bare-repository guard: a bare repository has branches and a remote, and proposing one
    /// branch into another is a question about refs rather than about files.
    /// </summary>
    public Task<VerbResult> PullRequestAsync(RepositoryInfo repository) =>
        Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var window = new PullRequestWindow(
                repository,
                pullRequests,
                pullRequestFlow,
                forgeCredentials,
                aiText,
                status,
                consent,
                notifier,
                settings,
                log);

            window.Show();

            timings.Record("window.pr.visible", clock.Elapsed);

            //Nothing touches the network before the window paints, per CLAUDE.md -- which is exactly
            //what this split is: shown first, resolved second.
            await window.LoadAsync().ConfigureAwait(true);

            timings.Record("window.pr.populated", clock.Elapsed);

            return VerbResult.Stay();
        });

    public Task<VerbResult> SwitchPickerAsync(RepositoryInfo repository) =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            new SwitchBranchWindow(repository, switches, branches, dialogs).Show();

            return VerbResult.Stay(ExitCodes.Success);
        }).GetTask();

    public VerbResult TagPicker(RepositoryInfo repository) => Open(() => new TagsWindow(repository, tags, dialogs));

    public VerbResult StashPicker(RepositoryInfo repository) => Open(() => new StashesWindow(repository, stashes, dialogs));

    public VerbResult Submodules(RepositoryInfo repository) =>
        Open(() => new SubmodulesWindow(repository, submodules, dialogs));

    public VerbResult Repo(RepositoryInfo repository) =>
        Open(() => new RepositoryWindow(repository, repositoryConfig, remoteService));

    /// <summary>
    /// The clone dialog, on the folder that is not inside a repository.
    ///
    /// <b>The url argument is deliberately unused.</b> `flick clone &lt;path&gt; [url]` accepts one,
    /// and the window reads the clipboard itself and prefills only what looks like a remote -- so a
    /// url handed in here would be a second source for the same field, silently overriding what the
    /// user copied. The window is the one place that decides.
    /// </summary>
    public VerbResult Clone(string path, string? url) =>
        Dispatcher.UIThread.Invoke(() =>
        {
            _ = url;

            new CloneWindow(path, clones, log).Show();

            return VerbResult.Stay(ExitCodes.Success);
        });

    /// <summary>
    /// Shows a window and stays.
    ///
    /// The hop is what matters: these three are called on the socket listener's thread, and
    /// constructing a Window from there throws. Blocking on the dispatcher rather than posting,
    /// because the verb has to answer the client after the window exists — otherwise a failure to
    /// open would be reported as success.
    /// </summary>
    private static VerbResult Open(Func<Window> build) =>
        Dispatcher.UIThread.Invoke(() =>
        {
            build().Show();

            return VerbResult.Stay(ExitCodes.Success);
        });

    /// <summary>
    /// Opens a terminal at the folder.
    ///
    /// <c>open -a Terminal</c> rather than launching a shell directly: on macOS the terminal is an
    /// application to be told about a folder, not a process to spawn with a working directory.
    /// </summary>
    public VerbResult Terminal(VerbOutput output, string? path)
    {
        _ = repositories;

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                UseShellExecute = false,
                ArgumentList = { "-a", "Terminal", path ?? Environment.CurrentDirectory },
            });

            return VerbResult.Exit(ExitCodes.Success);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            output.Fail("FlickGit", ex.Message);

            return VerbResult.Exit(ExitCodes.ConfigurationError);
        }
    }
}
