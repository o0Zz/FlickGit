using System.Windows;
using FlickGit.App.Localization;
using FlickGit.Clone;
using FlickGit.Logging;

namespace FlickGit.App.Views;

/// <summary>
/// The clone dialog.
///
/// Three behaviours are the point of it, all from CLAUDE.md, "Clone":
///
/// <list type="bullet">
/// <item><description><b>Clipboard prefill.</b> The user copies a URL from GitHub or Azure DevOps,
/// right-clicks, and the field is already filled — but only if what is on the clipboard really
/// looks like a remote, and never as a trigger: the clone waits for the button.</description></item>
/// <item><description><b>A determinate bar.</b> `clone --progress` writes phase and percentage to
/// stderr, which the service parses.</description></item>
/// <item><description><b>Cancellation cleans up.</b> The partial directory is deleted, and only
/// when this operation created it.</description></item>
/// </list>
/// </summary>
public partial class CloneWindow : Window
{
    private readonly string _parentDirectory;
    private readonly CloneService _clones;
    private readonly ILog _log;

    private CancellationTokenSource? _cancellation;
    private bool _directoryEditedByUser;
    private bool _running;

    public CloneWindow(string parentDirectory, CloneService clones, ILog log, string? clipboardText)
    {
        InitializeComponent();

        _parentDirectory = parentDirectory;
        _clones = clones;
        _log = log;

        Title = Strings.Get("clone.title", parentDirectory);
        TitleText.Text = Title;

        //Named here rather than duplicated as literals in the XAML.
        IntoLabel.Text = Strings.Get("clone.into");
        SubmodulesBox.Content = Strings.Get("clone.submodules");
        ShallowBox.Content = Strings.Get("clone.shallow");
        CloneButton.Content = Strings.Get("clone.button");
        CancelButton.Content = Strings.Get("clone.cancel");

        Prefill(clipboardText);
    }

    /// <summary>Set when the clone succeeded, so the caller can offer to open the new repository.</summary>
    public string? ClonedInto { get; private set; }

    /// <summary>
    /// Fills the URL field from the clipboard, but only when the content is shaped like a remote.
    ///
    /// Anything else leaves the field empty. A wrong prefill costs the user more than an empty one:
    /// they have to notice it and clear it, which is more work than typing the URL would have been.
    /// </summary>
    private void Prefill(string? clipboardText)
    {
        CloneTarget? target = CloneUrl.TryParse(clipboardText);

        if (target is null)
        {
            UrlBox.Focus();
            return;
        }

        UrlBox.Text = target.Url;
        DirectoryBox.Text = target.DirectoryName;

        //Prefilled and ready, so the focus goes to the button rather than the field: the common
        //case is that the guess is right and the user just wants to press Clone.
        CloneButton.Focus();
    }

    private void OnUrlChanged(object sender, RoutedEventArgs e)
    {
        //The derived name follows the URL until the user types their own, and then stops. Silently
        //overwriting a name they chose would be worse than not deriving one at all.
        if (_directoryEditedByUser)
            return;

        //DirectoryNameFor rather than TryParse: the strict URL check guards the clipboard
        //prefill, but once the user has typed a source -- including a local path or a UNC share --
        //deriving a folder name from it is just as useful and saves them inventing one.
        string? derived = CloneUrl.DirectoryNameFor(UrlBox.Text);
        if (derived is null)
            return;

        //Guarded, or the assignment below would re-enter through OnDirectoryChanged and mark the
        //field as user-edited.
        _directoryEditedByUser = true;
        DirectoryBox.Text = derived;
        _directoryEditedByUser = false;
    }

    private void OnDirectoryChanged(object sender, RoutedEventArgs e)
    {
        if (!_directoryEditedByUser && DirectoryBox.IsKeyboardFocusWithin)
            _directoryEditedByUser = true;
    }

    private async void OnClone(object sender, RoutedEventArgs e)
    {
        if (_running)
            return;

        _running = true;
        _cancellation = new CancellationTokenSource();

        SetRunning(true);
        LogText.Text = string.Empty;
        StatusText.Text = string.Empty;

        var progress = new Progress<CloneProgress>(Report);

        try
        {
            CloneOutcome outcome = await _clones.CloneAsync(
                _parentDirectory,
                UrlBox.Text.Trim(),
                DirectoryBox.Text.Trim(),
                new CloneOptions(
                    RecurseSubmodules: SubmodulesBox.IsChecked == true,
                    ShallowDepth: ShallowBox.IsChecked == true ? 1 : null),
                progress,
                _cancellation.Token).ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                ClonedInto = outcome.TargetDirectory;
                StatusText.Text = Strings.Get("clone.success", outcome.TargetDirectory ?? string.Empty);
                Progress.Value = 100;

                //Left open with the result showing rather than closing out from under the user: the
                //caller offers to open the new repository from here.
                CloneButton.Content = Strings.Get("clone.open");
                CloneButton.IsEnabled = true;
                CloneButton.Click -= OnClone;
                CloneButton.Click += (_, _) => Close();
                CancelButton.Content = Strings.Get("clone.close");
                return;
            }

            StatusText.Text = outcome.Suggestion is { Length: > 0 }
                ? $"{outcome.Error}\n\n{outcome.Suggestion}"
                : outcome.Error;
        }
        catch (OperationCanceledException)
        {
            //The service has already deleted the partial directory -- and only if it created it.
            StatusText.Text = Strings.Get("clone.cancelled");
        }
        catch (Exception ex)
        {
            _log.Error($"Clone failed: {ex}");
            StatusText.Text = ex.Message;
        }
        finally
        {
            _running = false;
            SetRunning(false);
        }
    }

    private void Report(CloneProgress progress)
    {
        if (progress.Percent is { } percent)
        {
            PhaseText.Text = progress.Phase ?? string.Empty;
            PercentText.Text = $"{percent}%";
            Progress.Value = percent;

            //Progress redraws are not appended to the log: Git emits one per percent, and a
            //hundred near-identical lines would bury the remote's actual messages.
            return;
        }

        LogText.Text = LogText.Text.Length == 0 ? progress.Text : $"{LogText.Text}\n{progress.Text}";
        LogScroller.ScrollToEnd();
    }

    private void SetRunning(bool running)
    {
        ProgressPanel.Visibility = Visibility.Visible;
        CloneButton.IsEnabled = !running;
        UrlBox.IsEnabled = !running;
        DirectoryBox.IsEnabled = !running;
        SubmodulesBox.IsEnabled = !running;
        ShallowBox.IsEnabled = !running;

        //Cancel stays live throughout: a clone of a large repository is the operation most likely
        //to need interrupting.
        CancelButton.Content = running ? Strings.Get("clone.cancel") : Strings.Get("clone.close");
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            //Cancels the clone, which kills the process tree and removes the partial directory.
            //The window stays open to report what happened.
            _cancellation?.Cancel();
            return;
        }

        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        //Closing the window mid-clone cancels it rather than orphaning a git.exe that would keep
        //writing into a directory nobody is watching.
        if (_running)
            _cancellation?.Cancel();

        base.OnClosing(e);
    }
}
