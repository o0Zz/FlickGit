using System.Diagnostics;
using System.IO;
using System.Windows;
using FlickGit.App.Localization;
using FlickGit.App.Resident;
using FlickGit.App.Views;
using FlickGit.Branches;
using FlickGit.Cli;
using FlickGit.App.Settings;
using FlickGit.Clone;
using FlickGit.Diagnostics;
using FlickGit.Diff;
using FlickGit.History;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Pulls;
using FlickGit.Repositories;
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
/// </summary>
public sealed class WindowVerbs(
    CommitWindowHost commitWindow,
    PaletteWindowHost palette,
    StatusService status,
    SwitchService switches,
    PullService pulls,
    CloneService clones,
    TagService tags,
    HistoryService history,
    DiffService diffs,
    FlickSettings settings,
    OperationTimings timings,
    RepositoryService repositories,
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

        var window = new LogWindow(repository, history, diffs, settings, timings, log);

        window.Show();

        //The stub granted this process foreground rights before sending the request; without this
        //the window comes up behind Explorer.
        window.Activate();

        timings.Record("window.log.visible", clock.Elapsed);

        //Awaited rather than left running, so "visible" and "usable" are two budgets -- the same
        //split CommitWindowHost makes.
        await window.LoadFirstPageAsync().ConfigureAwait(true);

        timings.Record("window.log.populated", clock.Elapsed);

        return VerbResult.Stay();
    }

    /// <summary>`flick pull-rebase`, with the submodule update as a distinct step.</summary>
    public async Task<VerbResult> PullAsync(VerbOutput output, RepositoryInfo repository)
    {
        var window = new ProgressWindow(Strings.Get("pull.title", repository.Name));
        window.Show();
        window.Activate();

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

        var picker = new SwitchBranchWindow(repository, switches, state.Branch);

        picker.Show();

        //The stub granted this process foreground rights before sending the request; without this
        //the picker comes up behind Explorer.
        picker.Activate();

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

        window.Show();

        //The stub granted this process foreground rights before sending the request; without this the
        //window comes up behind Explorer.
        window.Activate();

        return VerbResult.Stay();
    }

    /// <summary>
    /// The clone dialog.
    ///
    /// Unlike every other verb this one wants a folder that is <i>not</i> a repository, so no
    /// repository is resolved. Cloning into a subdirectory of an existing one is legal and
    /// occasionally intended, so it is not refused either.
    /// </summary>
    public VerbResult Clone(VerbOutput output, string path, string? url)
    {
        string parent = Directory.Exists(path) ? path : Environment.CurrentDirectory;

        var window = new CloneWindow(parent, clones, log, url ?? ReadClipboard());

        window.Closed += async (_, _) =>
        {
            //Cloned successfully: offer the commit window on the new repository, which is almost
            //always what the user does next.
            if (window.ClonedInto is not { Length: > 0 } cloned || !Directory.Exists(cloned))
                return;

            RepositoryInfo? cloneRepository = await repositories
                .ResolveAsync(cloned, CancellationToken.None)
                .ConfigureAwait(true);

            if (cloneRepository is not null)
                await CommitAsync(output, cloneRepository).ConfigureAwait(true);
        };

        window.Show();
        window.Activate();

        return VerbResult.Stay();
    }

    /// <summary>
    /// A terminal at the folder. Not a window of ours, but the same shape: something appears and
    /// this process is done.
    /// </summary>
    public VerbResult Terminal(VerbOutput output, string? path)
    {
        string directory = path ?? Environment.CurrentDirectory;

        //Windows Terminal when present, the shell's own default otherwise. UseShellExecute is
        //required here and only here: it is what lets Windows resolve wt.exe through the
        //app-execution alias, which is not on PATH as a real file.
        foreach (string executable in new[] { "wt.exe", "powershell.exe" })
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    WorkingDirectory = directory,
                    UseShellExecute = true,
                });

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
