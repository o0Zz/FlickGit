using System.Windows;
using System.Windows.Input;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;

namespace FlickGit.App.Views;

/// <summary>
/// The lifecycle every window that reads repository state and can be re-read shares: a cancellation
/// source tied to closing, a busy flag, F5, and the one read that turns a closing window's
/// cancellation back into "stop".
///
/// <b>Five windows carried this identically</b> — Branches, Tags, Stashes, Submodules and Repository
/// settings — 58 byte-for-byte identical lines each, comments included. Four copies of a rationale
/// comment are four copies that can drift, and three of the four paragraphs below describe a way this
/// code can kill the resident process. They are worth stating once.
///
/// <b>What is deliberately not here.</b> Anything touching a named control. <c>SetBusy</c>,
/// <c>SetStatus</c>, <c>Report</c> and the filter logic all look shared and are not: the controls they
/// touch are fields XAML generates on the <i>derived</i> partial class, invisible to a base, so
/// hoisting them would mean an abstract accessor per control and more code than it saves. Each window
/// keeps its own, and each one differs anyway — Repository toggles twelve controls, Branches four.
/// </summary>
public abstract class ReloadableWindow : Window
{
    /// <summary>
    /// Cancelled when the window closes, and passed to this window's <i>reads</i> only.
    ///
    /// The writes keep <see cref="CancellationToken.None"/> on purpose: abandoning one part-way leaves
    /// the repository in a state nobody reported, which is worse than waiting for it.
    /// </summary>
    private readonly CancellationTokenSource _closing = new();

    protected ReloadableWindow()
    {
        //F5 re-reads. A window binding rather than a button, so it works from the filter box and the
        //list alike -- the same shape as the commit window's, which was the only F5 in the product.
        //
        //AsyncCommand rather than RelayCommand over an async void: Commands.cs gives both reasons and
        //both apply here. Its re-entrancy guard stops two F5 presses interleaving two reads of the
        //same list, and its onError keeps an unhandled task exception out of WPF's dispatcher, where
        //it would take the resident process and every pre-warmed window with it.
        //
        //In the base constructor, which runs before the derived InitializeComponent(): InputBindings is
        //a Window property that exists already, none of these windows declares any in XAML, and nothing
        //here reads a generated field.
        InputBindings.Add(new KeyBinding
        {
            Key = Key.F5,
            Command = new AsyncCommand(
                LoadAsync,
                canExecute: () => !IsBusy,
                onError: exception => Notice.Show(this, Strings.Get("error.title"), exception.Message)),
        });
    }

    /// <summary>The token for this window's reads. Never pass it to a write.</summary>
    protected CancellationToken ClosingToken => _closing.Token;

    /// <summary>
    /// Set by <see cref="RunBusyAsync"/>, and read here only to gate F5.
    /// </summary>
    protected bool IsBusy { get; set; }

    /// <summary>
    /// Disables what this window disables while something is running.
    ///
    /// <b>Abstract rather than shared</b>, because the body is a list of named controls and those are
    /// fields XAML generates on the derived class. The five bodies also genuinely differ — Repository
    /// settings toggles twelve controls and re-derives two button labels, Branches toggles four.
    /// </summary>
    protected abstract void SetBusy(bool busy);

    /// <summary>
    /// Runs one operation with the window disabled, and re-enables it whatever happens.
    ///
    /// This existed twenty-one times across the five windows as nine lines of
    /// <c>SetBusy(true)</c>/<c>try</c>/<c>finally</c> scaffold around a body. The scaffold is the part
    /// that must not be got wrong: a write that throws with no <c>finally</c> leaves the window
    /// permanently dead, which is a bug the shape hides well because the happy path looks identical.
    ///
    /// <b>A <c>return</c> inside <paramref name="work"/> leaves the lambda, not the caller.</b> Every
    /// site converted to this had the <c>try</c>/<c>finally</c> as the last statement of its method, so
    /// the two are equivalent there — the one site that had code after the <c>finally</c>
    /// (<c>SubmodulesWindow.RemoveAsync</c>, whose recursive retry must run with the flag already down)
    /// keeps its scaffold on purpose.
    /// </summary>
    protected async Task RunBusyAsync(Func<Task> work)
    {
        SetBusy(true);

        try
        {
            await work().ConfigureAwait(true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Reads whatever this window shows. The one thing that differs between the five.</summary>
    protected abstract Task ReadStateAsync();

    /// <summary>
    /// The window's read, with the one exception a closing window can now raise turned back into
    /// "stop". Without this the token added for _closing would surface as an unhandled
    /// OperationCanceledException inside an async void handler, which ends the process -- a worse
    /// outcome than the leak it was added to fix.
    /// </summary>
    protected async Task LoadAsync()
    {
        //A write that finished after the window closed still asks for a reload. There is nothing left
        //to populate, and the read would only be cancelled a moment later anyway.
        if (_closing.IsCancellationRequested)
            return;

        try
        {
            await ReadStateAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            //Closed while the read was in flight. There is no longer anything to populate.
        }
    }

    /// <summary>The footer button that only dismisses the window. Wired from XAML in every derived window.</summary>
    protected void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        //Cancel, and deliberately *not* Dispose. Every write in these windows runs to completion on
        //CancellationToken.None and then reloads, so a token read can still happen after the window is
        //gone -- and CancellationTokenSource.Token throws ObjectDisposedException once disposed, which
        //in an async continuation means the resident process dies. Cancelling is what this needs; the
        //source is collected with the window.
        _closing.Cancel();

        base.OnClosed(e);
    }
}
