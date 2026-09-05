using System.IO;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FlickGit.Ai;
using FlickGit.App.Ai;
using FlickGit.App.Localization;
using FlickGit.History;
using FlickGit.Logging;
using FlickGit.Models;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// A changelog for the commits selected in the log window, written for the people who use the
/// software rather than for the people who wrote it.
///
/// <b>It is the log window's other outward action, and it is the same shape as the first.</b> Save
/// as patch hands the range to somebody who is going to apply it; this hands the same range to
/// somebody who is never going to read it. Both write outside the repository, neither runs a Git
/// command that changes anything, and both describe <i>the range</i> — so the gap disclosure is
/// repeated here rather than left behind in the window that produced the selection.
///
/// <b>Nothing here is a Git operation.</b> The text is a draft in a box: editable, copyable, savable,
/// and gone when the window closes unless the user does one of those three things. That is what lets
/// it be regenerated on a whim, and what makes the Style box safe to press twice.
/// </summary>
public sealed class ChangelogWindow : Window
{
    private readonly RepositoryInfo _repository;
    private readonly CommitRange _range;
    private readonly IReadOnlyList<GitFileChange> _files;
    private readonly AiTextService _ai;
    private readonly ILog _log;

    private readonly ComboBox _style = new() { MinWidth = 180 };
    private readonly Button _write = new() { MinWidth = 130, Classes = { "strip" } };
    private readonly Button _copy = new() { MinWidth = 110, Classes = { "strip" } };
    private readonly Button _save = new() { MinWidth = 110, Classes = { "primary" } };
    private readonly Button _close = new() { MinWidth = 90 };

    private readonly TextBox _text = new()
    {
        Classes = { "mono" },
        AcceptsReturn = true,
        AcceptsTab = false,
        TextWrapping = TextWrapping.Wrap,
    };

    private readonly TextBlock _status = new()
    {
        Classes = { "muted" },
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private CancellationTokenSource? _generation;

    /// <summary>True while the code is writing the box, so its own writes do not read as the user's.</summary>
    private bool _applying;

    /// <summary>
    /// The user has typed. Their words win over whatever is still arriving — the pull-request
    /// window's rule, for its reason: a stream fighting a caret is unusable.
    /// </summary>
    private bool _edited;

    /// <summary>
    /// False until the constructor has finished filling the Style box, so populating it does not count
    /// as choosing something and start a generation before the window is on screen.
    /// </summary>
    private readonly bool _ready;

    public ChangelogWindow(
        RepositoryInfo repository,
        CommitRange range,
        IReadOnlyList<GitFileChange> files,
        AiTextService ai,
        ILog log)
    {
        _repository = repository;
        _range = range;
        _files = files;
        _ai = ai;
        _log = log;

        Title = Strings.Get("changelog.title", repository.Name);
        Width = 720;
        Height = 620;
        MinWidth = 520;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _write.Content = Strings.Get("changelog.write");
        _copy.Content = Strings.Get("changelog.copy");
        _save.Content = Strings.Get("changelog.save");
        _close.Content = Strings.Get("common.close");

        _style.ItemsSource = new[]
        {
            Strings.Get("changelog.style.brief"),
            Strings.Get("changelog.style.detailed"),
        };

        _style.SelectedIndex = 0;

        //Hidden rather than disabled, which is what the commit and pull-request surfaces do with their
        //own AI buttons: a dead button invites a click that can only ever refuse.
        _write.IsVisible = ai.IsUsable;

        _write.Click += (_, _) =>
        {
            //An explicit press overrides "their words win", because it *is* them asking.
            _edited = false;
            _ = GenerateAsync();
        };

        //Changing the style rewrites, rather than applying to the next one somebody remembers to ask
        //for. Those two words are the only control this window has, and a control that does nothing
        //until a second button is pressed is not one.
        _style.SelectionChanged += (_, _) =>
        {
            if (!_ready || !_ai.IsUsable)
                return;

            _edited = false;
            _ = GenerateAsync();
        };

        _copy.Click += (_, _) => _ = CopyAsync();
        _save.Click += (_, _) => _ = SaveAsync();
        _close.Click += (_, _) => Close();

        _text.TextChanged += (_, _) =>
        {
            if (!_applying)
                _edited = true;

            UpdateActions();
        };

        Content = Build();

        //Something in the box before the first token arrives, and the whole of it when there is no
        //provider at all. The commits already say what happened, one line each, in the words of the
        //person who wrote them.
        Apply(Fallback());

        //Set explicitly rather than left to the TextChanged that assignment usually raises: writing an
        //empty string into an already-empty box raises nothing, so a range whose commits all have
        //empty subjects would leave both buttons live over nothing at all.
        UpdateActions();

        //"Edit it here, then copy it or save it" is what this window is for, so the caret starts in
        //the box holding the text rather than on a button.
        Opened += (_, _) => _text.Focus();

        _ready = true;
    }

    private Control Build()
    {
        //The log window's own wording for the same range, rather than a second phrasing of it: two
        //windows describing one thing in two sentences is two chances to disagree about it.
        string rangeText = _range.SelectedCount == 1
            ? Strings.Get("log.range.one", _range.Newest.ShortSha)
            : _range.Oldest.IsRoot
                ? Strings.Get("log.range.root", _range.SelectedCount, _range.Newest.ShortSha)
                : Strings.Get("log.range.many", _range.SelectedCount, _range.Oldest.ShortSha, _range.Newest.ShortSha);

        var header = new Border
        {
            Background = Resource("SurfaceAlt"),
            BorderBrush = Resource("Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 10),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = _repository.Name, Classes = { "title" } },
                    new TextBlock { Text = rangeText, Classes = { "muted", "small" }, Margin = new Thickness(0, 2, 0, 0) },

                    //The gap disclosure, repeated here rather than left behind in the window that
                    //produced the selection: this text describes the range, so it has to say what the
                    //range actually covers.
                    new TextBlock
                    {
                        Text = Strings.Get("log.range.gap", _range.ImplicitCount),
                        Classes = { "muted", "small" },
                        Margin = new Thickness(0, 2, 0, 0),
                        IsVisible = _range.ImplicitCount > 0,
                    },
                },
            },
        };

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(14, 10, 14, 0),
            Children =
            {
                new TextBlock
                {
                    Text = Strings.Get("changelog.style"),
                    Classes = { "section" },
                    VerticalAlignment = VerticalAlignment.Center,
                },
                _style,
                _write,
            },
        };

        var footer = new Border
        {
            Background = Resource("SurfaceAlt"),
            BorderBrush = Resource("Border"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 10),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children =
                {
                    Column(_status, 0),
                    Column(
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Children = { _copy, _save, _close },
                        },
                        1),
                },
            },
        };

        _text.Margin = new Thickness(14, 10);

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };

        grid.Children.Add(Row(header, 0));
        grid.Children.Add(Row(controls, 1));
        grid.Children.Add(Row(_text, 2));
        grid.Children.Add(Row(footer, 3));

        return grid;
    }

    /// <summary>
    /// Starts the first generation. Separate from the constructor so the caller can show the window
    /// first — the fallback is already in the box, so there is something to read while it runs.
    /// </summary>
    public Task StartAsync() => GenerateAsync();

    /// <summary>
    /// The commit subjects, oldest first, as a bulleted list.
    ///
    /// Not a placeholder: with no AI configured this <i>is</i> the changelog, and it is a serviceable
    /// one — which is what "the AI is an accelerator, never a dependency" requires of a window whose
    /// only content the AI writes.
    /// </summary>
    private string Fallback() =>
        string.Join(
            Environment.NewLine,
            _range.Commits
                .Reverse()
                .Select(c => c.Subject)
                .Where(s => s.Length > 0)
                .Select(s => $"- {s}"));

    private ChangelogStyle SelectedStyle() =>
        _style.SelectedIndex == 1 ? ChangelogStyle.Detailed : ChangelogStyle.Brief;

    private async Task GenerateAsync()
    {
        if (!_ai.IsUsable)
            return;

        //One at a time. A second press, or a style change mid-stream, cancels the first -- otherwise
        //two streams write into one box.
        _generation?.Cancel();
        _generation?.Dispose();

        var generation = new CancellationTokenSource();
        _generation = generation;

        _write.IsEnabled = false;
        _style.IsEnabled = false;
        _status.Text = Strings.Get("changelog.writing");

        try
        {
            GenerationOutcome outcome = await _ai.StreamChangelogAsync(
                _repository,
                _range.BaseSpec,
                _range.TipSpec,
                _range.Commits,
                _files,
                SelectedStyle(),
                Apply,
                generation.Token).ConfigureAwait(true);

            //Superseded: a newer generation is already running and owns the footer and the buttons.
            //Reporting this one's outcome would overwrite its "writing…" with a stale answer.
            if (!ReferenceEquals(_generation, generation))
                return;

            if (outcome.Succeeded)
            {
                Apply(outcome.Message);
                _status.Text = Strings.Get("changelog.hint");
            }
            else if (outcome.FailureReason is { Length: > 0 } reason)
            {
                //Every AI failure gets the same treatment: an ordinary editable box, and one line
                //saying why. The commit subjects are still in it, so the window is still useful.
                _status.Text = reason;
            }
        }
        finally
        {
            if (ReferenceEquals(_generation, generation))
            {
                _write.IsEnabled = true;
                _style.IsEnabled = true;
                _generation = null;
            }

            generation.Dispose();
        }
    }

    private void Apply(string text)
    {
        //They started typing while it was arriving. The rest of the stream is thrown away rather than
        //fighting them for the caret.
        if (_edited)
        {
            _generation?.Cancel();

            return;
        }

        _applying = true;

        try
        {
            _text.Text = text;

            //Follows the tail as it arrives, so a changelog longer than the box does not stream past
            //unseen.
            _text.CaretIndex = text.Length;
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>Copy and Save mean nothing over an empty box.</summary>
    private void UpdateActions()
    {
        bool any = (_text.Text ?? string.Empty).Trim().Length > 0;

        _copy.IsEnabled = any;
        _save.IsEnabled = any;
    }

    private async Task CopyAsync()
    {
        try
        {
            if (Clipboard is { } clipboard)
                await clipboard.SetTextAsync(_text.Text ?? string.Empty).ConfigureAwait(true);

            _status.Text = Strings.Get("changelog.copied");
        }
        catch (Exception ex)
        {
            //Another process can hold the pasteboard. Worth a line in the footer and nothing more: the
            //text is still on screen and still selectable.
            _log.Debug($"Clipboard write failed: {ex.Message}");
            _status.Text = Strings.Get("changelog.copy.failed");
        }
    }

    private async Task SaveAsync()
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = FileName(),
            DefaultExtension = "md",
            ShowOverwritePrompt = true,

            //The repository's parent, not the repository -- Save as patch's rule, for its reason: a
            //file dropped inside the working tree comes straight back as an untracked row in the
            //commit window.
            SuggestedStartLocation = await StorageProvider
                .TryGetFolderFromPathAsync(ParentOfRepository())
                .ConfigureAwait(true),
        }).ConfigureAwait(true);

        if (file?.Path.LocalPath is not { Length: > 0 } path)
            return;

        try
        {
            //UTF-8 without a BOM, and the box's own line endings. Nothing here is round-tripping a
            //file that already existed, so there is no encoding to preserve: this is a new document,
            //and the one encoding every Markdown reader agrees about is the right one to write.
            File.WriteAllText(path, _text.Text ?? string.Empty, new UTF8Encoding(false));

            _status.Text = Strings.Get("changelog.saved", Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageWindow.Notice(
                Strings.Get("changelog.save.failed"),
                ex.Message + Environment.NewLine + Environment.NewLine + path);
        }
    }

    private string ParentOfRepository() =>
        Path.GetDirectoryName(_repository.Root.TrimEnd('/', '\\')) ?? _repository.Root;

    /// <summary>
    /// <c>changelog-4d5e6f7..a1b2c3d.md</c>, or <c>changelog-a1b2c3d.md</c> for one commit.
    ///
    /// No subject slug, unlike a patch: a patch of one commit <i>is</i> that commit, while a
    /// changelog of one commit is a rewriting of it, and naming the file after the subject would
    /// promise the two say the same thing.
    /// </summary>
    private string FileName() => _range.SelectedCount == 1
        ? $"changelog-{_range.Newest.ShortSha}.md"
        : $"changelog-{_range.Oldest.ShortSha}..{_range.Newest.ShortSha}.md";

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
        //Esc closes, always, and takes the generation with it -- the commit window's rule. Cancelling
        //instead and staying open would make one key mean two things depending on timing.
        _generation?.Cancel();

        base.OnClosed(e);
    }

    private static T Row<T>(T control, int row)
        where T : Control
    {
        control.SetValue(Grid.RowProperty, row);

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
