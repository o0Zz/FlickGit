using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace FlickGit.App.Mac.Views;

/// <summary>What a three-answer question came back with. The macOS counterpart of ConfirmChoice.</summary>
public enum MessageChoice
{
    /// <summary>The first button: the affirmative the question named.</summary>
    Yes,

    /// <summary>The second button: the other thing the question named.</summary>
    No,

    /// <summary>The third button, Esc, or the close box. Nothing was asked for.</summary>
    Cancelled,
}

/// <summary>
/// One window for all three of the shapes this product asks in: a notice that states an outcome, a
/// question with two answers, and the three-answer form where doing nothing is its own answer.
///
/// <b>The third form is not a nicety.</b> Two of the questions in the commit window — the file
/// changed on disk under an unsaved edit, and closing with one outstanding — have three outcomes
/// where two of them destroy something: overwriting loses what is on disk and reloading loses what
/// is in the editor. Neither may be what Esc picks, so Esc has to be able to mean "neither".
///
/// <b>Built in code rather than in XAML, and only this one.</b> The commit window and the diff pane
/// are where the XAML investment belongs — they have layout worth expressing declaratively and a
/// view model to bind to. This has a title, a paragraph and at most three buttons.
///
/// <b>Ownerless on purpose.</b> Avalonia's <c>ShowDialog</c> requires an owner and these answer a
/// verb that may have no window behind it at all — a context menu, a terminal, the socket. So it is
/// <c>Show()</c> plus a <see cref="TaskCompletionSource{TResult}"/> completed when the window
/// closes, which is modal to nothing and still answers exactly once.
/// </summary>
public sealed class MessageWindow : Window
{
    private readonly TaskCompletionSource<MessageChoice> _answer = new();

    private MessageWindow(
        string title,
        string message,
        string? yes,
        string? no,
        string? third,
        bool destructive,
        string? detail)
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
            buttons.Children.Add(
                Button(Localization.Strings.Get("common.close"), MessageChoice.No, isDefault: true, danger: false));
        }
        else
        {
            //Read left to right in the order the WPF confirmations use: the way out first, then the
            //two things that act. The dangerous answer is never the default, whichever way round the
            //buttons read.
            if (third is not null)
                buttons.Children.Add(Button(third, MessageChoice.Cancelled, isDefault: true, danger: false));

            buttons.Children.Add(
                Button(no ?? Localization.Strings.Get("common.cancel"),
                    MessageChoice.No,
                    isDefault: third is null && !destructive,
                    danger: false));

            buttons.Children.Add(Button(yes, MessageChoice.Yes, isDefault: false, danger: destructive));
        }

        var content = new StackPanel
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
            },
        };

        //Git's own words, in their own box and monospaced -- never folded into the sentence above.
        //CLAUDE.md's error rule wants the operation, what happened and Git's output as three things,
        //and stderr set in the body font reads as prose the tool wrote.
        if (detail is { Length: > 0 })
        {
            content.Children.Add(new TextBox
            {
                Classes = { "mono" },
                Text = detail,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
                MaxHeight = 200,
                FontSize = 11.5,
            });
        }

        content.Children.Add(buttons);

        Content = content;
    }

    /// <summary>Opens a notice. Nothing waits on it: an outcome is not a question.</summary>
    /// <param name="detail">Git's raw stderr, in its own monospaced box. Null to omit it.</param>
    public static void Notice(string title, string message, string? detail = null) =>
        new MessageWindow(title, message, yes: null, no: null, third: null, destructive: false, detail).Show();

    /// <summary>
    /// A Git command failed, said in the four parts CLAUDE.md's "Error Handling" section requires:
    /// the operation, what happened, the repository path, and Git's own words.
    ///
    /// A named method rather than four <see cref="Notice"/> arguments assembled per call site,
    /// because the Windows host learned that lesson: five sites there passed raw stderr as the
    /// <i>message</i>, which drops the path and renders an empty dialog when Git said nothing.
    /// </summary>
    public static void GitFailure(string title, string message, string? gitError, string repositoryPath) =>
        Notice(
            title,
            message + Environment.NewLine + Environment.NewLine
                    + Localization.Strings.Get("error.repositorypath", repositoryPath),
            gitError is { Length: > 0 } words ? words.Trim() : null);

    /// <summary>Opens a two-answer question and completes when it is answered or closed.</summary>
    public static async Task<bool> AskAsync(string title, string body, string yes, string no, bool destructive) =>
        await AskAsync(title, body, yes, no, third: null, destructive).ConfigureAwait(true) == MessageChoice.Yes;

    /// <summary>
    /// Opens a question and completes when it is answered or closed.
    ///
    /// With a <paramref name="third"/> label the close box and Esc mean <see cref="MessageChoice.Cancelled"/>
    /// — nothing happened — rather than the second answer. Without one they mean
    /// <see cref="MessageChoice.No"/>, which for a two-answer question is the one that does nothing.
    /// </summary>
    public static Task<MessageChoice> AskAsync(
        string title,
        string body,
        string yes,
        string no,
        string? third,
        bool destructive)
    {
        var window = new MessageWindow(title, body, yes, no, third, destructive, detail: null);

        window.Show();

        return window._answer.Task;
    }

    private Button Button(string text, MessageChoice answer, bool isDefault, bool danger)
    {
        var button = new Button
        {
            Content = text,
            IsDefault = isDefault,
            MinWidth = 92,
        };

        if (danger)
            button.Classes.Add("danger");
        else if (isDefault)
            button.Classes.Add("primary");

        button.Click += (_, _) =>
        {
            //TrySetResult, not SetResult: OnClosed runs for this close too, and the second call would
            //throw on an already-completed source.
            _answer.TrySetResult(answer);
            Close();
        };

        return button;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
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
        //Closing without pressing anything is the answer that does nothing: Cancelled where the
        //question offered it, No where it did not. Never the affirmative.
        _answer.TrySetResult(MessageChoice.Cancelled);

        base.OnClosed(e);
    }
}
