using System.Windows.Input;

namespace FlickGit.App.Infrastructure;

/// <summary>A synchronous <see cref="ICommand"/>. Used for the commands that only change view state.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// An <see cref="ICommand"/> over an async operation, with re-entrancy blocked while it
/// runs.
///
/// The re-entrancy guard is the reason this type exists rather than an
/// <c>async void</c> click handler. Every button in the commit window starts a Git
/// process, and double-clicking Commit must not run two `git commit` invocations against
/// one index — the second would either fail confusingly or commit an empty tree.
///
/// Exceptions are routed to <paramref name="onError"/> rather than escaping into the UI
/// framework's dispatcher, where an unhandled task exception from an <c>async void</c> handler
/// takes the whole resident process down and every pre-warmed window with it.
/// </summary>
public sealed class AsyncCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null,
    Action<Exception>? onError = null) : ICommand
{
    private bool _running;

    /// <summary>
    /// The UI thread, captured where the command is built.
    ///
    /// <b>A synchronization context rather than a dispatcher</b>, which is what this used to reach
    /// for: <c>System.Windows.Application.Current.Dispatcher</c> is WPF, and this type now serves an
    /// Avalonia front end as well. Both frameworks install a context on their UI thread, so
    /// capturing it here asks neither of them by name.
    ///
    /// Null when the command is built off the UI thread, in which case the event is raised inline —
    /// there is no thread to marshal to and nothing bound to it yet either.
    /// </summary>
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    public event EventHandler? CanExecuteChanged;

    public bool IsRunning
    {
        get => _running;
        private set
        {
            _running = value;
            RaiseCanExecuteChanged();
        }
    }

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;

        IsRunning = true;

        try
        {
            await execute().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            //Cancellation is a normal outcome here -- the window closed, or the user
            //pressed Esc. Not an error to report.
        }
        catch (Exception ex)
        {
            if (onError is null)
                throw;

            onError(ex);
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// Raises the event on the UI thread.
    ///
    /// <c>Post</c>, where the WPF version used <c>Dispatcher.Invoke</c> and blocked until the
    /// bindings had re-queried. Asynchronous is both sufficient and safer here — what this drives is
    /// a button's enabled state, which is allowed to settle on the next turn of the loop, and
    /// <c>Send</c> from a Git continuation back onto a UI thread that is awaiting it is a deadlock.
    /// </summary>
    public void RaiseCanExecuteChanged()
    {
        if (_ui is null)
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _ui.Post(_ => CanExecuteChanged?.Invoke(this, EventArgs.Empty), null);
    }
}
