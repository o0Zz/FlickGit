using System.Windows;
using FlickGit.App.Localization;

namespace FlickGit.App.Views;

/// <summary>
/// The one place FlickGit tells the user something went wrong.
///
/// CLAUDE.md, "Error Handling": "Display the operation, the Git error, the repository path,
/// and a suggested next action" and "Never show generic errors such as 'Something went
/// wrong.'" The constructor takes the message as a required argument for that reason —
/// there is no parameterless path that could produce an empty dialog.
/// </summary>
public partial class NoticeWindow : Window
{
    /// <param name="title">The operation, in the user's words: "Commit", "Switch branch".</param>
    /// <param name="message">What happened. Shown in full, wrapped.</param>
    /// <param name="compact">
    /// True for a one-line notice such as "not a repository", where CLAUDE.md requires "a
    /// one-line message, never a full window".
    /// </param>
    /// <param name="detail">Git's raw stderr, shown monospaced in its own box.</param>
    public NoticeWindow(string title, string message, bool compact, string? detail = null)
    {
        InitializeComponent();

        CloseButton.Content = Strings.Get("common.close");

        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;

        if (detail is { Length: > 0 })
        {
            DetailText.Text = detail;
            DetailBox.Visibility = Visibility.Visible;
        }

        if (!compact)
            return;

        //Compact form: no heading, tighter width, so a "not a repository" message reads as
        //a passing notice rather than as a failure worth a dialog.
        TitleText.Visibility = Visibility.Collapsed;
        MessageText.MaxWidth = 420;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
