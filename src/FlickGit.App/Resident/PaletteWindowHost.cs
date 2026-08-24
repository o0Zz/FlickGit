using System.Diagnostics;
using FlickGit.App.CommandLine;
using FlickGit.App.ViewModels;
using FlickGit.App.Views;
using FlickGit.Diagnostics;
using FlickGit.Logging;

namespace FlickGit.App.Resident;

/// <summary>
/// Owns the one palette window, built once at logon and shown many times.
///
/// The same shape as the commit host, and the same reason: CLAUDE.md budgets 80 ms from hotkey
/// to painted palette, which is less than a single <c>git status</c> — so the window is pre-warmed
/// and the list is rendered from cache before anything is asked of Git.
///
/// What it adds is the ordering that makes that budget honest: reset, place, show, activate, record,
/// <i>then</i> refresh. The refresh replaces the rows in place when Git answers.
/// </summary>
public sealed class PaletteWindowHost
{
    private readonly PaletteViewModel _viewModel;
    private readonly OperationTimings _timings;
    private readonly ILog _log;

    private PaletteWindow? _window;

    /// <param name="actions">
    /// Where the chosen action goes, behind a factory for the same reason <see cref="ActionRunner"/>
    /// takes the verb runner that way: this host is reachable <i>from</i> an action, so holding the
    /// runner outright would close a cycle. A settable property here was null on the one-shot
    /// <c>flick palette</c> path, where choosing anything did nothing at all.
    /// </param>
    public PaletteWindowHost(
        PaletteViewModel viewModel,
        Func<ActionRunner> actions,
        OperationTimings timings,
        ILog log)
    {
        _viewModel = viewModel;
        _timings = timings;
        _log = log;

        //Here rather than in Create, and that placement is the whole of it: the view model is a
        //singleton now, and Create runs twice whenever the pre-warm fails and ShowAsync has to build
        //a window of its own. Subscribing there would run the chosen action twice.
        //
        //Straight through to the one runner. The palette deliberately has no other way to reach Git
        //-- CLAUDE.md: it is "not a shortcut around these rules".
        viewModel.ActionRequested += (action, repository, argument) =>
            _ = actions().RunAsync(action, repository, VerbOutput.Direct(), argument);
    }

    /// <summary>Builds the palette and lays it out, without showing it. See <see cref="AppWindow"/>.</summary>
    public void Warm()
    {
        PaletteWindow window = Create(keepAlive: true);
        _window = AppWindow.TryWarm(window, "Palette", _log) ? window : null;
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

        bool focused = AppWindow.Present(_window, PopupPlacement.NearTopOfActiveScreen);

        //After Show, not before: the pre-warm laid the window out while it was hidden, and a TextBox
        //cannot take keyboard focus until its window is actually visible.
        _window.FocusQuery();

        if (!focused)
        {
            //Same failure and same answer as the popup. The hotkey path normally grants activation,
            //so this is the `flick palette` case.
            _log.Warn("Windows refused foreground activation; the palette is not on top and not focused.");
            _window.DemoteFromTopmost();
        }

        //The number CLAUDE.md budgets at 80 ms. Recorded before the refresh, because "painted" and
        //"up to date" are two different claims.
        _timings.Record("palette.visible", clock.Elapsed);

        await _window.RefreshAsync().ConfigureAwait(true);

        _timings.Record("palette.populated", clock.Elapsed);
    }

    private PaletteWindow Create(bool keepAlive) =>
        new()
        {
            DataContext = _viewModel,
            KeepAlive = keepAlive,
        };
}
