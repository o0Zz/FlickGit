using System.Diagnostics;
using FlickGit.App.ViewModels;
using FlickGit.App.Views;
using FlickGit.Diagnostics;
using FlickGit.Logging;
using FlickGit.Models;

namespace FlickGit.App.Resident;

/// <summary>
/// Owns the one commit window, built once and shown many times.
///
/// This is the whole of the resident service's speed advantage. CLAUDE.md, "Resident Service":
/// a cold WPF start pays CLR startup, JIT, PresentationFramework/PresentationCore/WindowsBase load,
/// theme dictionary resolution, HWND creation and first render — 400–800 ms. <see cref="Warm"/> pays
/// it once at logon; <see cref="ShowAsync"/> then costs a repaint and a status call.
///
/// The same class serves a one-shot launch, where there is nothing to reuse. That is deliberate:
/// the reuse path is the only path, so it is exercised every time rather than only in the
/// configuration nobody tests.
///
/// <b>Reuse is the correctness risk.</b> CLAUDE.md: the window "must be fully re-initialisable from
/// a new <c>CommitContext</c>; no state may leak between two uses." Every mutable field is assigned
/// in <see cref="CommitViewModel.Reset"/>, which is where to look when adding a field.
/// </summary>
public sealed class CommitWindowHost(CommitViewModelFactory viewModels, OperationTimings timings, ILog log)
{
    private CommitWindow? _window;

    /// <summary>
    /// Builds the window and lays it out, without ever showing it. Called only by the resident
    /// service; see <see cref="ResidentWindow"/> for what that buys and why.
    /// </summary>
    public void Warm()
    {
        CommitWindow window = Create(keepAlive: true);
        _window = ResidentWindow.TryWarm(window, "Commit window", log) ? window : null;
    }

    /// <summary>
    /// Shows the commit window for <paramref name="repository"/>, reusing the pre-warmed instance.
    /// </summary>
    public async Task ShowAsync(RepositoryInfo repository)
    {
        var clock = Stopwatch.StartNew();

        //A one-shot launch has nothing warm to reuse, so the window is built now and really closes
        //when the user closes it -- which is what lets the process exit.
        _window ??= Create(keepAlive: false);

        //Cleared before showing, so the window never appears holding the previous repository's file
        //list for a frame. No Git yet: that is the next step, and it is what the split is for.
        _window.Reset(repository);

        //Not Topmost, so a refused activation leaves an ordinary background window the user can click
        //-- there is nothing to demote and nothing to warn about.
        _ = ResidentWindow.Present(_window);

        //The number CLAUDE.md budgets at 120 ms. Recorded here rather than inferred from how long
        //the stub took, because the stub also waits for the status below -- and "visible" and
        //"populated" are two separate budgets.
        timings.Record("window.commit.visible", clock.Elapsed);

        //Only now the four Git processes. Awaited rather than left running, so the caller -- and
        //through it the stub -- knows when the window is not just visible but usable.
        await _window.RefreshAsync().ConfigureAwait(true);

        timings.Record("window.commit.populated", clock.Elapsed);
    }

    private CommitWindow Create(bool keepAlive) =>
        new()
        {
            DataContext = viewModels.Create(),

            //Resident: closing hides it, so the next right-click reuses it. One-shot: closing really
            //closes, so the process can exit.
            KeepAlive = keepAlive,
        };
}
