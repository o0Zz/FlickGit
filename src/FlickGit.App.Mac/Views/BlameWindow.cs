using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using FlickGit.App.Localization;
using FlickGit.App.Mac.Rendering;
using FlickGit.App.Settings;
using FlickGit.Blame;
using FlickGit.Diagnostics;
using FlickGit.Logging;
using FlickGit.Models;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// Who last touched each line of a file, and — the reason this window exists — <b>what was there
/// before</b>.
///
/// Reading one blame answers a question about the present. The commit it names is very often not the
/// one that introduced the line, only the last to reformat, rename or move it. Stepping back through
/// <see cref="BlameCommit.PreviousSha"/> is how you get past that to the change that actually did it,
/// and it is the half most blame viewers leave out.
///
/// <b>Git computes the step, not this window.</b> The porcelain stream names both the previous commit
/// and the path the file had there, so nothing here appends <c>^</c> or resolves a parent, and a
/// rename is followed by using the path Git reported.
///
/// <b>It performs nothing.</b> No checkout, revert, cherry-pick or edit — the same boundary the log
/// window holds, and the reason a read-only history surface belongs in a tool that is not a complete
/// Git client.
/// </summary>
public sealed class BlameWindow : Window
{
    private readonly RepositoryInfo _repository;
    private readonly BlameService _blame;
    private readonly OperationTimings _timings;
    private readonly ILog _log;

    private readonly BlameMargin _margin = new();
    private readonly BlameBackgroundRenderer _highlight = new();

    private readonly TextEditor _editor = new()
    {
        IsReadOnly = true,
        ShowLineNumbers = true,
        WordWrap = false,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
    };

    private readonly TextBlock _pathText = new() { Classes = { "title" }, TextTrimming = TextTrimming.CharacterEllipsis };

    private readonly TextBlock _revisionText = new()
    {
        Classes = { "muted", "small" },
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly TextBlock _depthText = new()
    {
        Classes = { "muted", "small" },
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12, 0),
    };

    private readonly TextBlock _placeholderText = new()
    {
        Classes = { "muted" },
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        MaxWidth = 520,
    };

    private readonly Border _placeholder;

    private readonly TextBlock _commitText = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _commitMeta = new() { Classes = { "muted", "small" }, Margin = new Thickness(0, 4, 0, 0) };

    private readonly TextBlock _status = new()
    {
        Classes = { "muted" },
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly Button _back = new() { MinWidth = 90, Classes = { "strip" }, IsEnabled = false };
    private readonly Button _previous = new() { Classes = { "primary" }, MinWidth = 230, IsEnabled = false };
    private readonly Button _close = new() { MinWidth = 80, Classes = { "strip" } };

    /// <summary>
    /// Where the walk came from, so Back can undo a step.
    ///
    /// The caret line is part of the state on purpose: stepping back and forward again while losing
    /// your place would defeat the point of walking, which is to follow one line through history.
    /// </summary>
    private readonly Stack<Step> _history = new();

    private IReadOnlyList<BlameLine> _lines = [];
    private string _path;
    private string? _revision;
    private BlameCommit? _selected;
    private CancellationTokenSource? _inFlight;

    public BlameWindow(
        RepositoryInfo repository,
        string relativePath,
        string? revision,
        BlameService blame,
        FlickSettings settings,
        OperationTimings timings,
        ILog log)
    {
        _repository = repository;
        _path = relativePath;
        _revision = revision;
        _blame = blame;
        _timings = timings;
        _log = log;

        Width = 1100;
        Height = 760;
        MinWidth = 720;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _back.Content = Strings.Get("blame.back");
        _close.Content = Strings.Get("common.close");
        _previous.Content = Strings.Get("blame.previous.none");

        _back.Click += (_, _) => _ = BackAsync();
        _previous.Click += (_, _) => _ = BlamePreviousAsync();
        _close.Click += (_, _) => Close();

        _editor.FontFamily = new FontFamily(settings.DiffFontFamily);
        _editor.FontSize = settings.DiffFontSize;
        _editor.Options.EnableHyperlinks = false;

        //A read-only editor still draws a caret, which here is the selection: the line the detail
        //band and the Blame-previous button are about.
        _editor.TextArea.Caret.CaretBrush = Brushes.Transparent;

        _margin.SetTypography(_editor.FontFamily, _editor.FontSize);
        _margin.LineClicked += SelectLine;

        _editor.TextArea.LeftMargins.Insert(0, _margin);
        _editor.TextArea.TextView.BackgroundRenderers.Add(_highlight);
        _editor.TextArea.Caret.PositionChanged += (_, _) => SelectLine(_editor.TextArea.Caret.Line);

        //Double-click walks back, which is the same thing the button does -- the button names the
        //target so the gesture is discoverable, and the double-click is there once it has been.
        _editor.TextArea.DoubleTapped += (_, e) =>
        {
            if (!_previous.IsEnabled)
                return;

            _ = BlamePreviousAsync();
            e.Handled = true;
        };

        _placeholder = new Border
        {
            Background = Resource("Surface"),
            IsVisible = false,
            Child = _placeholderText,
        };

        Content = Build();
    }

    private Control Build()
    {
        var header = new Border
        {
            Background = Resource("SurfaceAlt"),
            BorderBrush = Resource("Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(14, 10),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                Children =
                {
                    Column(
                        new StackPanel
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            Children = { _pathText, _revisionText },
                        },
                        0),
                    Column(_depthText, 1),
                    Column(_back, 2),
                },
            },
        };

        var body = new Grid
        {
            Background = Resource("SurfaceSunken"),
            Children = { _editor, _placeholder },
        };

        var detail = new Border
        {
            BorderBrush = Resource("Border"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 8),
            Child = new StackPanel { Children = { _commitText, _commitMeta } },
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
                            Spacing = 16,
                            Children = { _previous, _close },
                        },
                        1),
                },
            },
        };

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };

        grid.Children.Add(Row(header, 0));
        grid.Children.Add(Row(body, 1));
        grid.Children.Add(Row(detail, 2));
        grid.Children.Add(Row(footer, 3));

        return grid;
    }

    /// <summary>
    /// Reads the first blame.
    ///
    /// Separate from the constructor so the caller can time "visible" and "usable" apart, the split
    /// the log window and the commit window both make.
    /// </summary>
    public Task LoadAsync() => ReadAsync();

    private async Task ReadAsync()
    {
        Title = Strings.Get("blame.title", Path.GetFileName(_path));
        _pathText.Text = _path;
        _revisionText.Text = Strings.Get("blame.loading");
        _placeholderText.Text = Strings.Get("blame.loading");
        _placeholder.IsVisible = true;
        _previous.IsEnabled = false;
        _back.IsEnabled = _history.Count > 0;
        _depthText.Text = _history.Count > 0 ? Strings.Get("blame.depth", _history.Count) : string.Empty;

        _inFlight?.Cancel();

        var cancellation = new CancellationTokenSource();
        _inFlight = cancellation;

        var clock = Stopwatch.StartNew();

        BlameOutcome outcome;

        try
        {
            outcome = await _blame
                .BlameAsync(_repository, _path, _revision, cancellation.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log.Warn($"Blame failed for {_path}: {ex.Message}");
            Refuse(ex.Message);

            return;
        }

        if (cancellation.IsCancellationRequested)
            return;

        if (outcome.IsBinary)
        {
            Refuse(Strings.Get("blame.binary"));

            return;
        }

        if (!outcome.Succeeded)
        {
            //Git's own words, which for a file that is not tracked is "no such path ... in HEAD" --
            //the sentence the user needs rather than one of ours about it.
            Refuse(outcome.Error?.Trim() ?? Strings.Get("blame.failed"));

            return;
        }

        _lines = outcome.Lines;

        _editor.SyntaxHighlighting = HighlightingManager.Instance
            .GetDefinitionByExtension(Path.GetExtension(_path));

        _editor.Text = outcome.Text();

        _margin.SetLines(_lines);
        _highlight.SetLines(_lines);

        _placeholder.IsVisible = false;
        _revisionText.Text = RevisionLabel();
        _status.Text = Strings.Get(
            "blame.lines",
            _lines.Count,
            _lines.Select(l => l.Commit.Sha).Distinct().Count());

        _timings.Record("blame.read", clock.Elapsed);

        SelectLine(Math.Min(_editor.TextArea.Caret.Line, Math.Max(_lines.Count, 1)));

        //The editor takes the keyboard, because every gesture this window has is aimed at a line: the
        //arrow keys move the subject, and Alt+Left steps back from it. The TextArea rather than the
        //TextEditor -- the editor forwards focus, but only once it is loaded, and a walk back
        //re-enters here on an already-loaded window.
        _editor.TextArea.Focus();
    }

    /// <summary>Shows a reason in place of the file, and leaves the walk where it was.</summary>
    private void Refuse(string message)
    {
        _lines = [];
        _margin.SetLines([]);
        _highlight.SetLines([]);
        _editor.Text = string.Empty;

        _placeholderText.Text = message;
        _placeholder.IsVisible = true;
        _revisionText.Text = RevisionLabel();
        _status.Text = string.Empty;
        _commitText.Text = string.Empty;
        _commitMeta.Text = string.Empty;
        _previous.IsEnabled = false;
        _previous.Content = Strings.Get("blame.previous.none");
    }

    /// <summary>
    /// The clicked line becomes the subject: its commit fills the band, its siblings light up, and
    /// the button names where a step back would land.
    /// </summary>
    private void SelectLine(int line)
    {
        if (_lines.Count == 0)
            return;

        int index = Math.Clamp(line, 1, _lines.Count) - 1;
        BlameCommit commit = _lines[index].Commit;

        _selected = commit;

        _highlight.Select(commit.Sha);
        _editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);

        if (commit.IsUncommitted)
        {
            _commitText.Text = Strings.Get("blame.uncommitted");
            _commitMeta.Text = Strings.Get("blame.line", index + 1);
        }
        else
        {
            _commitText.Text = $"{commit.ShortSha}  {commit.Summary}";
            _commitMeta.Text = Strings.Get(
                "blame.meta",
                commit.Author,
                commit.When.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                index + 1);
        }

        //The button says where it goes, so pressing it is never a guess -- and when it cannot go
        //anywhere it says which of the two reasons applies.
        if (commit.HasPrevious)
        {
            _previous.IsEnabled = true;
            _previous.Content = Strings.Get("blame.previous", Short(commit.PreviousSha!));
        }
        else
        {
            _previous.IsEnabled = false;
            _previous.Content = Strings.Get(commit.IsBoundary ? "blame.previous.first" : "blame.previous.none");
        }
    }

    private async Task BlamePreviousAsync()
    {
        if (_selected is not { HasPrevious: true } commit)
            return;

        _history.Push(new Step(_path, _revision, _editor.TextArea.Caret.Line));

        //Git's own answer for both: the commit to blame next, and the name the file had there. This
        //is what makes the walk cross a rename without anything here knowing one happened.
        _revision = commit.PreviousSha;
        _path = commit.PreviousPath ?? _path;

        await ReadAsync().ConfigureAwait(true);
    }

    private async Task BackAsync()
    {
        if (_history.Count == 0)
            return;

        Step step = _history.Pop();

        _path = step.Path;
        _revision = step.Revision;

        await ReadAsync().ConfigureAwait(true);

        //Restored after the read, or the caret would land in a document that is about to be replaced.
        if (_lines.Count == 0)
            return;

        int line = Math.Clamp(step.Line, 1, _lines.Count);

        _editor.TextArea.Caret.Line = line;
        _editor.ScrollToLine(line);
        SelectLine(line);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        //Alt+Left is Back everywhere that has history.
        if (e.Key == Key.Left && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            e.Handled = true;
            _ = BackAsync();

            return;
        }

        //Nothing here can refuse to close: this window performs no operation.
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
        _inFlight?.Cancel();

        base.OnClosed(e);
    }

    /// <summary>
    /// What the header says the file is being read at.
    ///
    /// Named rather than left implicit: after two steps back the content on screen is not the file on
    /// disk, and a blame that does not say which revision it is of is a blame you cannot trust.
    /// </summary>
    private string RevisionLabel()
    {
        if (_revision is not { Length: > 0 })
            return Strings.Get("blame.workingtree");

        //After a step, the commit that was walked to is in the new blame's own history -- so its
        //subject is looked up there rather than carried along.
        BlameCommit? named = _lines.FirstOrDefault(l =>
            string.Equals(l.Commit.Sha, _revision, StringComparison.OrdinalIgnoreCase))?.Commit;

        return named is null
            ? Strings.Get("blame.revision", Short(_revision))
            : Strings.Get("blame.revision.named", Short(_revision), named.Summary);
    }

    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

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

    /// <param name="Line">The caret line, so Back returns to the line the walk was following.</param>
    private sealed record Step(string Path, string? Revision, int Line);
}
