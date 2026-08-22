using System.Diagnostics;
using FlickGit.App.Localization;
using FlickGit.App.ViewModels;
using FlickGit.App.Views;
using FlickGit.Diagnostics;
using FlickGit.Logging;
using FlickGit.Models;

namespace FlickGit.App.Resident;

/// <summary>
/// Owns the one quick-commit popup, built once at logon and shown many times.
///
/// The same shape as <see cref="CommitWindowHost"/> and for the same reason, with two differences
/// that matter:
///
/// <list type="bullet">
/// <item><description><b>It is placed, not centred.</b> CLAUDE.md: "Anchored near the cursor, not
/// centred." That happens before <c>Show()</c>, in physical pixels — see
/// <see cref="PopupPlacement"/>.</description></item>
/// <item><description><b>It verifies that Windows actually gave it focus.</b> A <c>Topmost</c>
/// popup that is visible but unfocused over an Explorer window is worse than no popup at all: the
/// user's Enter reaches Explorer's file list and opens whatever was selected. If activation was
/// refused, the window is demoted to an ordinary one rather than left in that state.</description></item>
/// </list>
/// </summary>
public sealed class QuickCommitWindowHost(
    QuickCommitViewModelFactory viewModels,
    CommitWindowHost commitWindow,
    Notifier notifier,
    OperationTimings timings,
    ILog log)
{
    private QuickCommitWindow? _window;

    /// <summary>
    /// Builds the popup and lays it out, without ever showing it. See <see cref="ResidentWindow"/>,
    /// which handles this popup sizing to its content rather than to a declared height.
    /// </summary>
    public void Warm()
    {
        QuickCommitWindow window = Create(keepAlive: true);
        _window = ResidentWindow.TryWarm(window, "Quick-commit popup", log) ? window : null;
    }

    /// <summary>
    /// Shows the popup for <paramref name="repository"/>.
    /// </summary>
    /// <param name="isFallback">
    /// True when the repository is the most recently used one rather than the folder the user was
    /// looking at. The popup says so prominently; CLAUDE.md forbids acting silently on a repository
    /// the user did not choose.
    /// </param>
    public async Task ShowAsync(RepositoryInfo repository, bool isFallback)
    {
        var clock = Stopwatch.StartNew();

        _window ??= Create(keepAlive: false);

        //Cleared before showing, so the popup never appears holding the previous repository's
        //counts for a frame. No Git yet -- that is the next step, and the split is what makes two
        //separate budgets meaningful.
        _window.Reset(repository, isFallback);

        if (!ResidentWindow.Present(_window, PopupPlacement.NearCursor))
        {
            //Topmost without keyboard focus is the one state worse than no popup: over an Explorer
            //window, Enter reaches Explorer's file list and opens whatever was selected. Demoting
            //leaves an ordinary background window the user can click; withdrawing would throw away a
            //window that was legitimately asked for.
            log.Warn("Windows refused foreground activation; the popup is not on top and not focused.");
            _window.DemoteFromTopmost();
            notifier.Warn(Strings.Get("app.name"), Strings.Get("quick.foreground.refused"));
        }

        timings.Record("popup.quick.visible", clock.Elapsed);

        await _window.RefreshAsync().ConfigureAwait(true);

        timings.Record("popup.quick.populated", clock.Elapsed);

        //After the status, because the payload is built from the ticked files -- and after the
        //budget is recorded, because a network request is not part of "the popup is usable".
        _window.BeginGeneration();
    }

    private QuickCommitWindow Create(bool keepAlive)
    {
        QuickCommitViewModel viewModel = viewModels.Create();

        viewModel.Committed += result => notifier.Success(
            Strings.Get("app.name"),
            Strings.Get("commit.success", result.ShortHash, result.Subject));

        var window = new QuickCommitWindow
        {
            DataContext = viewModel,
            KeepAlive = keepAlive,
        };

        //Details... hands over the status the popup already fetched, so the commit window costs a
        //repaint rather than three more Git processes.
        window.DetailsRequested += source => _ = HandOffAsync(source);

        return window;
    }

    private async Task HandOffAsync(QuickCommitViewModel source)
    {
        var clock = Stopwatch.StartNew();

        try
        {
            if (source.CurrentStatus is { } status)
            {
                commitWindow.ShowFrom(source.Repository, status, source.Message, source.BranchInput);
                timings.Record("window.commit.handoff", clock.Elapsed);
                return;
            }

            //The popup was dismissed before its status landed. Rare, and the honest answer is the
            //ordinary path rather than a window with nothing in it.
            await commitWindow.ShowAsync(source.Repository).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            log.Error($"Handoff to the commit window failed: {ex}");
        }
    }
}
