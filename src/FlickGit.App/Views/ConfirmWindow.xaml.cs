using System.Windows;

namespace FlickGit.App.Views;

/// <summary>
/// The two-choice question used for every guardrail consent in the product.
///
/// Modal on purpose, and only ever owned by a window the user is already looking at: the questions
/// it asks (create an upstream? pull before pushing? overwrite a file that changed on disk?) all
/// gate an operation that must not proceed until answered.
/// </summary>
public partial class ConfirmWindow : Window
{
    private bool _answer;

    private ConfirmWindow(string title, string question, string yes, string no)
    {
        InitializeComponent();

        Title = title;
        TitleText.Text = title;
        QuestionText.Text = question;
        YesButton.Content = yes;
        NoButton.Content = no;
    }

    /// <summary>
    /// Asks, and returns true only if the user chose the affirmative.
    ///
    /// Closing the window any other way — Esc, the title bar — is a "no". A guardrail that treated
    /// a dismissed dialog as consent would not be a guardrail.
    /// </summary>
    /// <param name="owner">
    /// Null when the question comes from a command-line invocation with no window open. The
    /// question still has to be asked: it is one-time consent to publish a branch, and answering it
    /// on the user's behalf is exactly what a guardrail must not do.
    /// </param>
    public static bool Ask(Window? owner, string title, string question, string yes, string no)
    {
        var window = new ConfirmWindow(title, question, yes, no);

        if (owner is not null)
            window.Owner = owner;
        else
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        window.ShowDialog();
        return window._answer;
    }

    private void OnYes(object sender, RoutedEventArgs e)
    {
        _answer = true;
        Close();
    }

    private void OnNo(object sender, RoutedEventArgs e)
    {
        _answer = false;
        Close();
    }
}
