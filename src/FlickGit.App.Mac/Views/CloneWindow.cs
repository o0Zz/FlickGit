using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using FlickGit.App.Localization;
using FlickGit.Clone;
using FlickGit.Logging;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// The clone dialog.
///
/// Three behaviours are the point of it, all from CLAUDE.md, "Clone":
///
/// <list type="bullet">
/// <item><description><b>Clipboard prefill.</b> The user copies a URL from GitHub or Azure DevOps,
/// right-clicks, and the field is already filled — but only if what is on the clipboard really looks
/// like a remote, and never as a trigger: the clone waits for the button.</description></item>
/// <item><description><b>A determinate bar.</b> <c>clone --progress</c> writes phase and percentage
/// to stderr, which the service parses.</description></item>
/// <item><description><b>Cancellation cleans up.</b> The partial directory is deleted, and only when
/// this operation created it.</description></item>
/// </list>
///
/// <b>The clipboard is read asynchronously here</b>, which is the one shape difference from the WPF
/// window: Avalonia's clipboard is a <c>Task</c> because macOS's pasteboard is. So the window opens
/// with an empty field and fills it a frame later, rather than the caller reading the clipboard and
/// handing a string in.
/// </summary>
public sealed class CloneWindow : Window
{
    private readonly string _parentDirectory;
    private readonly CloneService _clones;
    private readonly ILog _log;

    private readonly TextBox _url = new() { Classes = { "mono" } };
    private readonly TextBox _directory = new() { Classes = { "mono" } };
    private readonly CheckBox _submodules = new() { IsChecked = true };
    private readonly CheckBox _shallow = new();

    private readonly TextBlock _phase = new() { Classes = { "muted" }, FontSize = 11.5 };

    private readonly TextBlock _percent = new()
    {
        Classes = { "muted" },
        FontSize = 11.5,
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    private readonly ProgressBar _progress = new()
    {
        Height = 6,
        Minimum = 0,
        Maximum = 100,
        ShowProgressText = false,
    };

    private readonly TextBlock _transcript = new() { Classes = { "mono" }, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly ScrollViewer _logScroller;
    private readonly StackPanel _progressPanel;

    private readonly TextBlock _status = new()
    {
        Classes = { "muted" },
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 360,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly Button _clone = new() { Classes = { "primary" }, IsDefault = true };
    private readonly Button _cancel = new();

    private CancellationTokenSource? _cancellation;
    private bool _directoryEditedByUser;
    private bool _running;

    public CloneWindow(string parentDirectory, CloneService clones, ILog log)
    {
        _parentDirectory = parentDirectory;
        _clones = clones;
        _log = log;

        Title = Strings.Get("clone.title", parentDirectory);
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _submodules.Content = Strings.Get("clone.submodules");
        _shallow.Content = Strings.Get("clone.shallow");
        _clone.Content = Strings.Get("clone.button");
        _cancel.Content = Strings.Get("common.cancel");

        _clone.Click += (_, _) => _ = CloneAsync();
        _cancel.Click += (_, _) => CancelOrClose();

        _url.TextChanged += (_, _) => OnUrlChanged();
        _directory.TextChanged += (_, _) => OnDirectoryChanged();

        _logScroller = new ScrollViewer { MaxHeight = 120, Content = _transcript };

        _progressPanel = new StackPanel
        {
            IsVisible = false,
            Margin = new Thickness(0, 16, 0, 0),
            Spacing = 6,
            Children =
            {
                new Grid { Children = { _phase, _percent } },
                _progress,
                new Border
                {
                    Margin = new Thickness(0, 4, 0, 0),
                    Padding = new Thickness(8, 6),
                    Background = Resource("SurfaceAlt"),
                    BorderBrush = Resource("Border"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Child = _logScroller,
                },
            },
        };

        Content = new Border
        {
            Padding = new Thickness(18, 16),
            Child = new StackPanel
            {
                Spacing = 0,
                Children =
                {
                    new TextBlock
                    {
                        Text = Title,
                        Classes = { "title" },
                        Margin = new Thickness(0, 0, 0, 14),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },

                    Fields(),

                    new StackPanel
                    {
                        Margin = new Thickness(0, 14, 0, 0),
                        Spacing = 6,

                        //Submodules on by default: --recurse-submodules clones them in parallel with
                        //the main history, so the default is both the useful one and the fast one.
                        Children = { _submodules, _shallow },
                    },

                    _progressPanel,

                    new Grid
                    {
                        Margin = new Thickness(0, 18, 0, 0),
                        ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                        Children =
                        {
                            _status,
                            Column(
                                new StackPanel
                                {
                                    Orientation = Orientation.Horizontal,
                                    Spacing = 8,
                                    Children = { _clone, _cancel },
                                },
                                1),
                        },
                    },
                },
            },
        };

        _ = PrefillAsync();
    }

    private Grid Fields()
    {
        var url = new TextBlock
        {
            Text = "URL",
            Classes = { "section" },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 8),
        };

        var into = new TextBlock
        {
            Text = Strings.Get("clone.into"),
            Classes = { "section" },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };

        _url.Margin = new Thickness(0, 0, 0, 8);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
        };

        grid.Children.Add(Cell(url, 0, 0));
        grid.Children.Add(Cell(_url, 0, 1));
        grid.Children.Add(Cell(into, 1, 0));
        grid.Children.Add(Cell(_directory, 1, 1));

        return grid;
    }

    /// <summary>
    /// Fills the URL field from the clipboard, but only when the content is shaped like a remote.
    ///
    /// Anything else leaves the field empty. A wrong prefill costs the user more than an empty one:
    /// they have to notice it and clear it, which is more work than typing the URL would have been.
    /// </summary>
    private async Task PrefillAsync()
    {
        string? text = null;

        try
        {
            if (Clipboard is { } clipboard)
                text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            //A clipboard that will not answer is not a reason to refuse to clone. Logged rather than
            //shown: the user asked to clone, not to paste.
            _log.Warn($"Clipboard unavailable for clone prefill: {ex.Message}");
        }

        if (CloneUrl.TryParse(text) is not { } target)
        {
            _url.Focus();

            return;
        }

        _url.Text = target.Url;
        _directory.Text = target.DirectoryName;

        //Prefilled and ready, so the focus goes to the button rather than the field: the common case
        //is that the guess is right and the user just wants to press Clone.
        _clone.Focus();
    }

    private void OnUrlChanged()
    {
        //The derived name follows the URL until the user types their own, and then stops. Silently
        //overwriting a name they chose would be worse than not deriving one at all.
        if (_directoryEditedByUser)
            return;

        //DirectoryNameFor rather than TryParse: the strict URL check guards the clipboard prefill,
        //but once the user has typed a source -- including a local path or a UNC share -- deriving a
        //folder name from it is just as useful and saves them inventing one.
        if (CloneUrl.DirectoryNameFor(_url.Text) is not { } derived)
            return;

        //Guarded, or the assignment below would re-enter through OnDirectoryChanged and mark the
        //field as user-edited.
        _directoryEditedByUser = true;
        _directory.Text = derived;
        _directoryEditedByUser = false;
    }

    private void OnDirectoryChanged()
    {
        if (!_directoryEditedByUser && _directory.IsFocused)
            _directoryEditedByUser = true;
    }

    private async Task CloneAsync()
    {
        if (_running)
            return;

        _running = true;
        _cancellation = new CancellationTokenSource();

        SetRunning(true);
        _transcript.Text = string.Empty;
        _status.Text = string.Empty;

        bool succeeded = false;

        try
        {
            CloneOutcome outcome = await _clones.CloneAsync(
                _parentDirectory,
                (_url.Text ?? string.Empty).Trim(),
                (_directory.Text ?? string.Empty).Trim(),
                new CloneOptions(
                    RecurseSubmodules: _submodules.IsChecked == true,
                    ShallowDepth: _shallow.IsChecked == true ? 1 : null),
                new Progress<CloneProgress>(Report),
                _cancellation.Token).ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                _progress.Value = 100;

                //Closed by the finally below rather than left open behind a button: a repository just
                //cloned has nothing to commit, so there is no next step to offer here.
                succeeded = true;

                return;
            }

            _status.Text = outcome.Suggestion is { Length: > 0 }
                ? $"{outcome.Error}\n\n{outcome.Suggestion}"
                : outcome.Error;
        }
        catch (OperationCanceledException)
        {
            //The service has already deleted the partial directory -- and only if it created it.
            _status.Text = Strings.Get("clone.cancelled");
        }
        catch (Exception ex)
        {
            _log.Error($"Clone failed: {ex}");
            _status.Text = ex.Message;
        }
        finally
        {
            //Cleared before the close, or OnClosing reads it as a clone still in flight and cancels
            //the operation that has just succeeded.
            _running = false;
            SetRunning(false);

            if (succeeded)
                Close();
        }
    }

    private void Report(CloneProgress progress)
    {
        if (progress.Percent is { } percent)
        {
            _phase.Text = progress.Phase ?? string.Empty;
            _percent.Text = $"{percent}%";
            _progress.Value = percent;

            //Progress redraws are not appended to the log: Git emits one per percent, and a hundred
            //near-identical lines would bury the remote's actual messages.
            return;
        }

        _transcript.Text = _transcript.Text?.Length is null or 0 ? progress.Text : $"{_transcript.Text}\n{progress.Text}";
        _logScroller.ScrollToEnd();
    }

    private void SetRunning(bool running)
    {
        _progressPanel.IsVisible = true;
        _clone.IsEnabled = !running;
        _url.IsEnabled = !running;
        _directory.IsEnabled = !running;
        _submodules.IsEnabled = !running;
        _shallow.IsEnabled = !running;

        //Cancel stays live throughout: a clone of a large repository is the operation most likely to
        //need interrupting.
        _cancel.Content = running ? Strings.Get("common.cancel") : Strings.Get("common.close");
    }

    private void CancelOrClose()
    {
        if (_running)
        {
            //Cancels the clone, which kills the process tree and removes the partial directory. The
            //window stays open to report what happened.
            _cancellation?.Cancel();

            return;
        }

        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelOrClose();

            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        //Closing the window mid-clone cancels it rather than orphaning a git process that would keep
        //writing into a directory nobody is watching.
        if (_running)
            _cancellation?.Cancel();

        base.OnClosing(e);
    }

    private static T Cell<T>(T control, int row, int column)
        where T : Control
    {
        control.SetValue(Grid.RowProperty, row);
        control.SetValue(Grid.ColumnProperty, column);

        return control;
    }

    private static T Column<T>(T control, int column)
        where T : Control
    {
        control.SetValue(Grid.ColumnProperty, column);

        return control;
    }

    private static IBrush? Resource(string key) => Application.Current?.FindResource(key) as IBrush;
}
