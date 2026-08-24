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
using FlickGit.Status;
using FlickGit.Tags;

namespace FlickGit.App.CommandLine;

/// <summary>
/// The verbs that put something on screen and leave it there.
///
/// They all return <see cref="VerbResult.Stay"/>: the process must outlive the call, because the
/// window is the answer. The CLI stub deliberately does not wait for these — waiting would block a
/// terminal until the user closed the commit window, and would leave Explorer holding a process per
/// right-click.
///
/// Windows are constructed here with <c>new</c> rather than resolved. That is not a lapse from
/// <b>Hard Requirement 3</b>: a window is not a collaborator, it is the output. What comes through
/// the constructor is everything that touches Git or the disk.
///
/// Every one of them goes on screen through <see cref="AppWindow.Present"/>. Each used to do its own
/// <c>Show()</c> then <c>Activate()</c> with the same comment above it — six copies of a sequence
/// that already had a seventh implementation in the class the two pre-warmed hosts share.
/// </summary>
public sealed class WindowVerbs(
    CommitWindowHost commitWindow,
    PaletteWindowHost palette,
    StatusService status,
    SwitchService switches,
    BranchService branches,
    PullService pulls,
    CloneService clones,
    TagService tags,
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
            //No working tree, so nothing to commit and nothing to show.
            output.Say(Strings.Get("app.name"), Strings.Get("error.bare", repository.Root));
            return VerbResult.Exit(ExitCodes.NotARepository);
        }

        //Through the host, always: it owns the one window and knows whether this process is the
        //resident service (reuse it) or a one-shot launch (build it now).
        await commitWindow.ShowAsync(repository).ConfigureAwait(true);

        return VerbResult.Stay();
    }

    /// <summary>
    /// The repository palette, for `flick palette` and the hotkey.
    ///
    /// Takes no repository: the palette's whole job is to find one. That is why it is the only window
    /// verb with nothing to guard against — there is no path to be not-a-repository yet.
    /// </summary>
    public async Task<VerbResult> PaletteAsync()
    {
        await palette.ShowAsync().ConfigureAwait(true);
        return VerbResult.Stay();
    }

    /// <summary>
    /// The log window, for `flick log`.
    ///
    /// Constructed per call rather than pre-warmed, and the reason is not the tag window's: the
    /// resident process has already paid WPF's cold start, the theme dictionary and AvalonEdit's
    /// first JIT for the commit window's diff pane, so a second pane here costs an HWND and a
    /// layout pass. What a warm instance <i>would</i> cost is a window full of per-use state --
    /// loaded pages, a selection, a range, a diff cache, an in-flight token -- that has to be
    /// provably reset between two uses, for a surface with no row in CLAUDE.md's latency table.
    ///
    /// No bare-repository guard, unlike <see cref="CommitAsync"/>: a bare repository has no working
    /// tree but it does have history, and this is the one window that can show it.
    /// </summary>
    public async Task<VerbResult> LogAsync(RepositoryInfo repository)
    {
        var clock = Stopwatch.StartNew();

        var window = new LogWindow(repository, history, diffs, blame, settings, timings, log);

        AppWindow.Present(window);

        timings.Record("window.log.visible", clock.Elapsed);

        //Awaited rather than left running, so "visible" and "usable" are two budgets -- the same
        //split CommitWindowHost makes.
        await window.LoadFirstPageAsync().ConfigureAwait(true);

        timings.Record("window.log.populated", clock.Elapsed);

        return VerbResult.Stay();
    }

    /// <summary>
    /// The blame window, for `flick blame` and for the file entry in the Explorer menu.
    ///
    /// The only verb whose path is a <b>file</b> rather than a directory, which is why it is the only
    /// one that has to say so when it is handed the wrong kind: every other verb treats a folder as
    /// the thing to act on, and blaming one is not a smaller version of blaming a file, it is a
    /// question with no answer.
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
    /// The pull-request window, for `flick pr` and the menu entry.
    ///
    /// Per call rather than pre-warmed, for the reason the tag and repository windows are: no row in
    /// CLAUDE.md's latency table, and a window kept for the session is a window whose per-use state
    /// -- a resolved forge, a summary, an in-flight generation -- has to be provably reset between
    /// two uses.
    ///
    /// No bare-repository guard: a bare repository has no working tree, but it does have branches and
    /// a remote, and proposing one branch into another is a question about refs rather than about
    /// files.
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

        //Awaited rather than left running, so "visible" and "usable" are two budgets -- the same
        //split the log window makes.
        await window.LoadAsync().ConfigureAwait(true);

        timings.Record("window.pr.populated", clock.Elapsed);

        return VerbResult.Stay();
    }

    /// <summary>`flick pull-rebase`, with the submodule update as a distinct step.</summary>
    public async Task<VerbResult> PullAsync(RepositoryInfo repository)
    {
        var window = new ProgressWindow(Strings.Get("pull.title", repository.Name));
        AppWindow.Present(window);

        PullOutcome outcome = await pulls
            .PullRebaseAsync(repository, new Progress<string>(window.AddStep), CancellationToken.None)
            .ConfigureAwait(true);

        if (!outcome.Succeeded)
        {
            window.Fail(
                outcome.StoppedOnConflict ? Strings.Get("pull.conflict") : Strings.Get("error.title"),
                outcome.GitError ?? string.Empty,
                outcome.Suggestion);

            //The window stays open with the reason on it, so the exit code is remembered rather
            //than acted on.
            return VerbResult.Stay(ExitCodes.GitError);
        }

        if (outcome.SubmoduleError is not null)
        {
            //The pull succeeded. Reported as a warning on a successful operation, never as a
            //failure -- CLAUDE.md, "Submodules": a submodule failure does not roll back the pull.
            window.Warn(Strings.Get("pull.submodule.failed"), outcome.SubmoduleError);
            return VerbResult.Stay();
        }

        window.Succeed(Strings.Get("pull.success"));
        return VerbResult.Stay();
    }

    /// <summary>The Switch branch picker, for when no branch was named.</summary>
    public async Task<VerbResult> SwitchPickerAsync(RepositoryInfo repository)
    {
        RepositoryStatus state = await status
            .GetStatusAsync(repository, CancellationToken.None)
            .ConfigureAwait(true);

        var picker = new SwitchBranchWindow(repository, switches, branches, state.Branch);

        AppWindow.Present(picker);

        return VerbResult.Stay();
    }

    /// <summary>
    /// The tag window, for `flick tag` with no name given.
    ///
    /// Constructed per call rather than pre-warmed, for the same reason the settings window is: it is
    /// not on any latency budget in CLAUDE.md's table, and a window kept alive for the session is a
    /// window whose state has to be provably reset between two uses.
    /// </summary>
    public VerbResult TagPicker(RepositoryInfo repository)
    {
        var window = new TagsWindow(repository, tags);

        AppWindow.Present(window);

        return VerbResult.Stay();
    }

    /// <summary>
    /// The repository window: `flick repo`.
    ///
    /// Per call rather than pre-warmed, for the reason <see cref="TagPicker"/> gives -- it is on no
    /// latency budget in CLAUDE.md's table, and a window kept for the session is a window whose state
    /// has to be provably reset between two uses.
    /// </summary>
    public VerbResult Repo(RepositoryInfo repository)
    {
        var window = new RepositoryWindow(repository, repositoryConfig, remotes);

        AppWindow.Present(window);

        return VerbResult.Stay();
    }

    /// <summary>
    /// The clone dialog.
    ///
    /// Unlike every other verb this one wants a folder that is <i>not</i> a repository, so no
    /// repository is resolved. Cloning into a subdirectory of an existing one is legal and
    /// occasionally intended, so it is not refused either.
    /// </summary>
    public VerbResult Clone(string path, string? url)
    {
        string parent = Directory.Exists(path) ? path : Environment.CurrentDirectory;

        var window = new CloneWindow(parent, clones, log, url ?? ReadClipboard());

        //Nothing happens after it closes. It used to open the commit window on the new repository,
        //which was the whole of what its "Open commit window" button did -- on a repository cloned
        //seconds earlier, so with nothing to commit. The window now closes itself on success and
        //that is the end of the operation.
        AppWindow.Present(window);

        return VerbResult.Stay();
    }

    /// <summary>
    /// A terminal at the folder. Not a window of ours, but the same shape: something appears and
    /// this process is done.
    /// </summary>
    public VerbResult Terminal(VerbOutput output, string? path)
    {
        string directory = TerminalDirectory(path ?? Environment.CurrentDirectory);

        //Windows Terminal when present, the shell's own default otherwise. UseShellExecute is
        //required here and only here: it is what lets Windows resolve wt.exe through the
        //app-execution alias, which is not on PATH as a real file.
        //
        //`wt.exe` needs `-d`, and the working directory is not enough on its own: a Windows Terminal
        //profile carries its own `startingDirectory`, which defaults to `%USERPROFILE%` and wins over
        //whatever directory the process was started in. Without the argument every terminal opened in
        //the home folder, which is the one thing this entry exists to avoid. WorkingDirectory is still
        //set, because it is all powershell.exe reads.
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

                //ArgumentList rather than a command-line string, per CLAUDE.md: a folder containing a
                //space, or ending in a backslash, is quoted by the framework rather than by us.
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
    /// The folder a terminal should start in.
    ///
    /// `C:` is not the root of the drive: it is the drive-*relative* path, meaning whichever directory
    /// happens to be current on C:, which is how a right-click on a drive root opened a terminal
    /// somewhere else entirely. Explorer hands over `C:\` and the trailing separator is trimmed on
    /// the way here, so it is put back.
    /// </summary>
    private static string TerminalDirectory(string path)
    {
        string directory = path.Trim().Trim('"');

        return directory.Length == 2 && directory[1] == ':'
            ? directory + Path.DirectorySeparatorChar
            : directory;
    }

    /// <summary>
    /// The clipboard, for the clone prefill.
    ///
    /// Read here, on the UI thread, because the clipboard is STA-bound — and a failure is not worth
    /// reporting: it only means no prefill.
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
