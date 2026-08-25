using System.Windows;
using System.Windows.Controls;

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

    private ConfirmWindow(string title, string question, string yes, string no, bool defaultIsAffirmative)
    {
        InitializeComponent();

        Title = title;
        TitleText.Text = title;
        QuestionText.Text = question;
        YesButton.Content = yes;
        NoButton.Content = no;

        //The default button, the accent styling and the focus ring travel together. A dialog where
        //Enter presses one button and the accent colour is on the other is a dialog lying about what
        //Enter does, which on a destructive question is the worst place to be unclear.
        Button preferred = defaultIsAffirmative ? YesButton : NoButton;

        preferred.IsDefault = true;
        preferred.Style = (Style)FindResource("PrimaryButton");

        //Loaded rather than here: focus cannot be given to an element that has not been arranged yet.
        Loaded += (_, _) => preferred.Focus();
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
    /// <param name="defaultIsAffirmative">
    /// True puts Enter on the affirmative. Reserved for the two questions whose answer the Recycle Bin
    /// makes undoable -- revert and delete, from the commit window's file list -- and defaulted to
    /// false so every guardrail keeps Enter meaning "no" without saying so.
    /// </param>
    public static bool Ask(
        Window? owner,
        string title,
        string question,
        string yes,
        string no,
        bool defaultIsAffirmative = false)
    {
        var window = new ConfirmWindow(title, question, yes, no, defaultIsAffirmative);

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
