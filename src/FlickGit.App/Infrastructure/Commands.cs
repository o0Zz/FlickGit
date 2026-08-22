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
/// Exceptions are routed to <paramref name="onError"/> rather than escaping into WPF's
/// dispatcher, where an unhandled task exception from an <c>async void</c> handler takes
/// the whole resident process down and every pre-warmed window with it.
/// </summary>
public sealed class AsyncCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null,
    Action<Exception>? onError = null) : ICommand
{
    private bool _running;

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

    public void RaiseCanExecuteChanged() =>
        System.Windows.Application.Current?.Dispatcher.Invoke(
            () => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
}
