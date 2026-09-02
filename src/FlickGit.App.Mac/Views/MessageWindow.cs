using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// One window for both of <c>IDialogs</c>' shapes: a notice that states an outcome, and a question
/// with two answers.
///
/// <b>Built in code rather than in XAML, and only this one.</b> The commit window and the diff pane
/// are where the XAML investment belongs — they have layout worth expressing declaratively and a
/// view model to bind to. This has a title, a paragraph and at most two buttons, and while the port
/// has no Mac to render on, thirty lines of layout that the compiler fully checks is worth more than
/// an .axaml file whose mistakes are invisible until someone runs it.
///
/// <b>Ownerless on purpose.</b> Avalonia's <c>ShowDialog</c> requires an owner and these answer a
/// verb that may have no window behind it at all — a context menu, a terminal, the socket. So it is
/// <c>Show()</c> plus a <see cref="TaskCompletionSource{TResult}"/> completed when the window
/// closes, which is modal to nothing and still answers exactly once.
/// </summary>
public sealed class MessageWindow : Window
{
    private readonly TaskCompletionSource<bool> _answer = new();

    private MessageWindow(string title, string message, string? yes, string? no, bool destructive)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        MinWidth = 380;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        if (yes is null)
        {
            //A notice. One button, and closing the window is the same answer, so nothing is recorded.
            buttons.Children.Add(Button(Localization.Strings.Get("common.close"), answer: false, isDefault: true));
        }
        else
        {
            //The dangerous answer is never the default, whichever way round the buttons read.
            buttons.Children.Add(Button(no ?? Localization.Strings.Get("common.cancel"), answer: false, isDefault: !destructive));
            buttons.Children.Add(Button(yes, answer: true, isDefault: false));
        }

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = title,
                    FontWeight = FontWeight.SemiBold,
                },
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 460,
                },
                buttons,
            },
        };
    }

    /// <summary>Opens a notice. Nothing waits on it: an outcome is not a question.</summary>
    public static void Notice(string title, string message) =>
        new MessageWindow(title, message, yes: null, no: null, destructive: false).Show();

    /// <summary>Opens a question and completes when it is answered or closed.</summary>
    public static Task<bool> AskAsync(string title, string body, string yes, string no, bool destructive)
    {
        var window = new MessageWindow(title, body, yes, no, destructive);

        window.Show();

        return window._answer.Task;
    }

    private Button Button(string text, bool answer, bool isDefault) =>
        new()
        {
            Content = text,
            IsDefault = isDefault,
            MinWidth = 92,
            Command = new RelayCommand(() =>
            {
                //TrySetResult, not SetResult: OnClosed runs for this close too, and the second call
                //would throw on an already-completed source.
                _answer.TrySetResult(answer);
                Close();
            }),
        };

    protected override void OnClosed(EventArgs e)
    {
        //Closing the window without pressing anything is "no". For a notice nobody is listening, and
        //for a question the safe answer is the one that does nothing.
        _answer.TrySetResult(false);

        base.OnClosed(e);
    }
}

/// <summary>
/// The smallest possible <see cref="System.Windows.Input.ICommand"/>.
///
/// Avalonia consumes the same <c>ICommand</c> WPF does, so the Windows
/// <c>Infrastructure/Commands.cs</c> is a namespace change away from being shared — but it lives in
/// the WPF project today, and moving it is part of the view-model work rather than this file's.
/// </summary>
internal sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();

    /// <summary>Never raised: nothing here becomes unavailable. Present because the interface is.</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
