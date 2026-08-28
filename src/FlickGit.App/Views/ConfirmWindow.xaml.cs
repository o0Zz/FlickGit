using System.Windows;
using System.Windows.Controls;

namespace FlickGit.App.Views;

/// <summary>
/// What the user chose when the question had a third answer that changes nothing.
///
/// A bool cannot carry this: "do not overwrite" and "do nothing" are different instructions, and
/// the whole point of the third button is that the caller can tell them apart.
/// </summary>
public enum ConfirmChoice
{
    /// <summary>The first button: the affirmative the question named.</summary>
    Yes,

    /// <summary>The second button: the other thing the question named.</summary>
    No,

    /// <summary>The third button, Esc, or the title bar. Nothing was asked for.</summary>
    Cancelled,
}

/// <summary>
/// The named-choice question used for every guardrail consent in the product.
///
/// Modal on purpose, and only ever owned by a window the user is already looking at: the questions
/// it asks (create an upstream? pull before pushing? overwrite a file that changed on disk?) all
/// gate an operation that must not proceed until answered.
/// </summary>
public partial class ConfirmWindow : Window
{
    private ConfirmChoice _choice = ConfirmChoice.Cancelled;

    private ConfirmWindow(
        string title,
        string question,
        string yes,
        string no,
        string? cancel,
        bool defaultIsAffirmative,
        bool destructive)
    {
        InitializeComponent();

        Title = title;
        TitleText.Text = title;
        QuestionText.Text = question;
        YesButton.Content = yes;
        NoButton.Content = no;

        //A question that destroys something says so in the colour of its affirmative, not only in the
        //verb on it. Applied before the default styling below, so if a question is ever both
        //destructive and defaulted to its affirmative, the accent wins -- which is the honest signal
        //for that combination, because it is only reached when the answer is undoable.
        if (destructive)
            YesButton.Style = (Style)FindResource("DangerButton");

        //The default button, the accent styling and the focus ring travel together. A dialog where
        //Enter presses one button and the accent colour is on the other is a dialog lying about what
        //Enter does, which on a destructive question is the worst place to be unclear.
        Button preferred;

        if (cancel is null)
        {
            preferred = defaultIsAffirmative ? YesButton : NoButton;
        }
        else
        {
            //Three choices, which is the shape used when *both* of the first two lose an edit. Esc has
            //to mean "nothing happened", so IsCancel moves off the refusal and onto the third button --
            //and Enter joins it there rather than sitting on one of the two irreversible answers. A
            //question with three verbs in it is one the user has to read.
            CancelButton.Content = cancel;
            CancelButton.Visibility = Visibility.Visible;

            NoButton.IsCancel = false;
            CancelButton.IsCancel = true;

            preferred = CancelButton;
        }

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
    /// <param name="destructive">
    /// True when the affirmative destroys something -- a branch, a stash, a remote, a file. Colours
    /// that button's label, so "Delete the local branch" and "Push and track" stop rendering
    /// identically. It changes nothing else: the guardrail is the question and the verb on the
    /// button, and a colour is not allowed to be the only thing carrying either.
    /// </param>
    public static bool Ask(
        Window? owner,
        string title,
        string question,
        string yes,
        string no,
        bool defaultIsAffirmative = false,
        bool destructive = false) =>
        Show(owner, title, question, yes, no, cancel: null, defaultIsAffirmative, destructive) == ConfirmChoice.Yes;

    /// <summary>
    /// Asks a question whose first two answers both destroy an edit, and whose third does nothing.
    ///
    /// <b>Separate from <see cref="Ask"/> rather than an optional argument on it</b>, because the
    /// return types differ for a reason: a guardrail consent genuinely is a bool -- twenty call sites
    /// read it as one -- while here "do not overwrite" and "do nothing" are two different
    /// instructions, and collapsing them is how a dialog ends up discarding an edit on Esc.
    ///
    /// The two callers are the working-tree editor's: closing with an unsaved edit, and saving over a
    /// file that changed on disk. In both, the first two buttons lose either the edit or the file, so
    /// there has to be a third that is neither -- CLAUDE.md's "never discard uncommitted work" is not
    /// satisfied by a dialog where the escape key picks one of two ways to do it.
    /// </summary>
    /// <param name="cancel">The third button: the answer that changes nothing. Carries Enter and Esc.</param>
    public static ConfirmChoice AskWithCancel(
        Window? owner,
        string title,
        string question,
        string yes,
        string no,
        string cancel) =>
        Show(owner, title, question, yes, no, cancel, defaultIsAffirmative: false, destructive: false);

    private static ConfirmChoice Show(
        Window? owner,
        string title,
        string question,
        string yes,
        string no,
        string? cancel,
        bool defaultIsAffirmative,
        bool destructive)
    {
        var window = new ConfirmWindow(title, question, yes, no, cancel, defaultIsAffirmative, destructive);

        if (owner is not null)
            window.Owner = owner;
        else
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        window.ShowDialog();
        return window._choice;
    }

    private void OnYes(object sender, RoutedEventArgs e)
    {
        _choice = ConfirmChoice.Yes;
        Close();
    }

    private void OnNo(object sender, RoutedEventArgs e)
    {
        _choice = ConfirmChoice.No;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _choice = ConfirmChoice.Cancelled;
        Close();
    }
}
