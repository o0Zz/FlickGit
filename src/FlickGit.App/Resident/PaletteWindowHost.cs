using System.Diagnostics;
using FlickGit.Actions;
using FlickGit.App.ViewModels;
using FlickGit.App.Views;
using FlickGit.Diagnostics;
using FlickGit.Logging;
using FlickGit.Models;

namespace FlickGit.App.Resident;

/// <summary>
/// Owns the one palette window, built once at logon and shown many times.
///
/// The same shape as the other two hosts, and the same reason: CLAUDE.md budgets 80 ms from hotkey
/// to painted palette, which is less than a single <c>git status</c> — so the window is pre-warmed
/// and the list is rendered from cache before anything is asked of Git.
///
/// What it adds is the ordering that makes that budget honest: reset, place, show, activate, record,
/// <i>then</i> refresh. The refresh replaces the rows in place when Git answers.
/// </summary>
public sealed class PaletteWindowHost(
    PaletteViewModelFactory viewModels,
    OperationTimings timings,
    ILog log)
{
    private PaletteWindow? _window;

    /// <summary>
    /// What to do with the action the user chose.
    ///
    /// Assigned by the composition root, which is the one place allowed to reach the
    /// <c>ActionRunner</c>. Injecting the runner instead would close a cycle — it opens windows
    /// through the verb runner, and this is one of the windows that can be opened.
    /// </summary>
    public Action<GitAction, RepositoryInfo, string?>? OnAction { get; set; }

    /// <summary>Builds the palette and lays it out, without showing it. See <see cref="ResidentWindow"/>.</summary>
    public void Warm()
    {
        PaletteWindow window = Create(keepAlive: true);
        _window = ResidentWindow.TryWarm(window, "Palette", log) ? window : null;
    }

    /// <summary>
    /// Shows the palette.
    ///
    /// Called from the hotkey and from <c>flick palette</c>. Both go through here, so the reuse path
    /// is the only path and is exercised every time.
    /// </summary>
    public async Task ShowAsync()
    {
        var clock = Stopwatch.StartNew();

        _window ??= Create(keepAlive: false);

        //Cleared before showing, so the palette never appears holding the previous session's query.
        //No Git yet: the rows come from the cache, which is the whole point of the cache.
        _window.Reset();

        bool focused = ResidentWindow.Present(_window, PopupPlacement.NearTopOfActiveScreen);

        //After Show, not before: the pre-warm laid the window out while it was hidden, and a TextBox
        //cannot take keyboard focus until its window is actually visible.
        _window.FocusQuery();

        if (!focused)
        {
            //Same failure and same answer as the popup. The hotkey path normally grants activation,
            //so this is the `flick palette` case.
            log.Warn("Windows refused foreground activation; the palette is not on top and not focused.");
            _window.DemoteFromTopmost();
        }

        //The number CLAUDE.md budgets at 80 ms. Recorded before the refresh, because "painted" and
        //"up to date" are two different claims.
        timings.Record("palette.visible", clock.Elapsed);

        await _window.RefreshAsync().ConfigureAwait(true);

        timings.Record("palette.populated", clock.Elapsed);
    }

    private PaletteWindow Create(bool keepAlive)
    {
        PaletteViewModel viewModel = viewModels.Create();

        //Straight through to the composition root. The palette deliberately has no other way to
        //reach Git -- CLAUDE.md: it is "not a shortcut around these rules".
        viewModel.ActionRequested += (action, repository, argument) => OnAction?.Invoke(action, repository, argument);

        return new PaletteWindow
        {
            DataContext = viewModel,
            KeepAlive = keepAlive,
        };
    }
}
