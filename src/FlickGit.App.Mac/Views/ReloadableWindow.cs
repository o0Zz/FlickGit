using Avalonia.Controls;
using Avalonia.Input;
using FlickGit.App.Localization;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// The lifecycle every window that reads repository state and can be re-read shares: a cancellation
/// source tied to closing, a busy flag, F5, and the one read that turns a closing window's
/// cancellation back into "stop".
///
/// The same base the Windows host keeps, and for the same reason: three of the four paragraphs below
/// describe a way this code can kill the resident process, and they are worth stating once.
///
/// <b>What is deliberately not here.</b> Anything touching a named control. <c>SetBusy</c> looks
/// shared and is not — each window disables a different set, and the sets genuinely differ.
/// </summary>
public abstract class ReloadableWindow : Window
{
    /// <summary>
    /// Cancelled when the window closes, and passed to this window's <i>reads</i> only.
    ///
    /// The writes keep <see cref="CancellationToken.None"/> on purpose: abandoning one part-way
    /// leaves the repository in a state nobody reported, which is worse than waiting for it.
    /// </summary>
    private readonly CancellationTokenSource _closing = new();

    /// <summary>The token for this window's reads. Never pass it to a write.</summary>
    protected CancellationToken ClosingToken => _closing.Token;

    /// <summary>Set by <see cref="RunBusyAsync"/>, and read here only to gate F5.</summary>
    protected bool IsBusy { get; set; }

    /// <summary>
    /// Disables what this window disables while something is running.
    ///
    /// <b>Abstract rather than shared</b>, because the body is a list of named controls and the
    /// bodies genuinely differ: the repository window toggles twelve, the branch picker four.
    /// </summary>
    protected abstract void SetBusy(bool busy);

    /// <summary>
    /// Runs one operation with the window disabled, and re-enables it whatever happens.
    ///
    /// The scaffold is the part that must not be got wrong: a write that throws with no
    /// <c>finally</c> leaves the window permanently dead, which is a bug the shape hides well because
    /// the happy path looks identical.
    ///
    /// <b>A <c>return</c> inside <paramref name="work"/> leaves the lambda, not the caller.</b>
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

    /// <summary>Reads whatever this window shows. The one thing that differs between them.</summary>
    protected abstract Task ReadStateAsync();

    /// <summary>
    /// The window's read, with the one exception a closing window can raise turned back into "stop".
    ///
    /// Without this the token would surface as an unhandled <see cref="OperationCanceledException"/>
    /// on a fire-and-forget task, which for the resident process means it dies — a worse outcome than
    /// the leak the token was added to fix.
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
        catch (Exception ex)
        {
            //Never swallowed silently: a window that failed to read and said nothing is a window
            //showing stale values it will not admit to.
            MessageWindow.Notice(Strings.Get("error.title"), ex.Message);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        //F5 re-reads. On the window rather than a button, so it works from a filter box and a list
        //alike -- the same shape as the commit window's.
        if (e.Key == Key.F5)
        {
            e.Handled = true;

            if (!IsBusy)
                _ = LoadAsync();

            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();

            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        //Cancel, and deliberately *not* Dispose. Every write in these windows runs to completion on
        //CancellationToken.None and then reloads, so a token read can still happen after the window
        //is gone -- and CancellationTokenSource.Token throws once disposed, which in an async
        //continuation means the resident process dies. Cancelling is what this needs; the source is
        //collected with the window.
        _closing.Cancel();

        base.OnClosed(e);
    }
}
