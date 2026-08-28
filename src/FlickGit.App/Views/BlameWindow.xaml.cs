using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using FlickGit.App.Localization;
using FlickGit.App.Rendering;
using FlickGit.App.Settings;
using FlickGit.Blame;
using FlickGit.Diagnostics;
using FlickGit.Logging;
using FlickGit.Models;
using ICSharpCode.AvalonEdit.Highlighting;

namespace FlickGit.App.Views;

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
public partial class BlameWindow : Window
{
    private readonly RepositoryInfo _repository;
    private readonly BlameService _blame;
    private readonly OperationTimings _timings;
    private readonly ILog _log;

    private readonly BlameMargin _margin = new();
    private readonly BlameBackgroundRenderer _highlight = new();

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
        InitializeComponent();

        _repository = repository;
        _path = relativePath;
        _revision = revision;
        _blame = blame;
        _timings = timings;
        _log = log;

        BackButton.Content = Strings.Get("blame.back");
        CloseButton.Content = Strings.Get("common.close");
        PreviousButton.Content = Strings.Get("blame.previous.none");

        Editor.FontFamily = new System.Windows.Media.FontFamily(settings.DiffFontFamily);
        Editor.FontSize = settings.DiffFontSize;
        Editor.Options.EnableHyperlinks = false;

        //A read-only editor still draws a caret, which here is the selection: the line the detail
        //band and the Blame-previous button are about.
        Editor.TextArea.Caret.CaretBrush = System.Windows.Media.Brushes.Transparent;

        _margin.SetTypography(Editor.FontFamily, Editor.FontSize);
        _margin.LineClicked += SelectLine;

        Editor.TextArea.LeftMargins.Insert(0, _margin);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_highlight);
        Editor.TextArea.Caret.PositionChanged += (_, _) => SelectLine(Editor.TextArea.Caret.Line);

        //Double-click walks back, which is the same thing the button does -- the button names the
        //target so the gesture is discoverable, and the double-click is there once it has been.
        Editor.TextArea.MouseDoubleClick += (_, e) =>
        {
            if (PreviousButton.IsEnabled)
            {
                OnBlamePrevious(this, new RoutedEventArgs());
                e.Handled = true;
            }
        };

        //Alt+Left is Back everywhere that has history. Esc is left to the Close button's IsCancel:
        //nothing here can refuse to close.
        InputBindings.Add(new KeyBinding
        {
            Key = Key.Left,
            Modifiers = ModifierKeys.Alt,
            Command = new Infrastructure.RelayCommand(() => OnBack(this, new RoutedEventArgs())),
        });
    }

    /// <summary>
    /// Reads the first blame.
    ///
    /// Separate from the constructor so the caller can time "visible" and "usable" apart, the split
    /// the log window and the commit window both make.
    /// </summary>
    public async Task LoadAsync() => await ReadAsync().ConfigureAwait(true);

    private async Task ReadAsync()
    {
        Title = Strings.Get("blame.title", Path.GetFileName(_path));
        PathText.Text = _path;
        RevisionText.Text = Strings.Get("blame.loading");
        Placeholder.Visibility = Visibility.Visible;
        PlaceholderText.Text = Strings.Get("blame.loading");
        PreviousButton.IsEnabled = false;
        BackButton.IsEnabled = _history.Count > 0;
        DepthText.Text = _history.Count > 0 ? Strings.Get("blame.depth", _history.Count) : string.Empty;

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

        Editor.SyntaxHighlighting = HighlightingManager.Instance
            .GetDefinitionByExtension(Path.GetExtension(_path));

        Editor.Text = outcome.Text();

        _margin.SetLines(_lines);
        _highlight.SetLines(_lines);

        Placeholder.Visibility = Visibility.Collapsed;
        RevisionText.Text = RevisionLabel();
        StatusText.Text = Strings.Get("blame.lines", _lines.Count, _lines.Select(l => l.Commit.Sha).Distinct().Count());

        _timings.Record("blame.read", clock.Elapsed);

        SelectLine(Math.Min(Editor.TextArea.Caret.Line, Math.Max(_lines.Count, 1)));

        //The editor takes the keyboard, because every gesture this window has is aimed at a line: the
        //arrow keys move the subject, and Alt+Left steps back from it. Without this the window opens
        //with a selected line that nothing can move until it is clicked. Focus the TextArea rather than
        //the TextEditor -- the editor forwards focus, but only once it is loaded, and a walk back
        //re-enters here on an already-loaded window.
        Editor.TextArea.Focus();
    }

    /// <summary>Shows a reason in place of the file, and leaves the walk where it was.</summary>
    private void Refuse(string message)
    {
        _lines = [];
        _margin.SetLines([]);
        _highlight.SetLines([]);
        Editor.Text = string.Empty;

        Placeholder.Visibility = Visibility.Visible;
        PlaceholderText.Text = message;
        RevisionText.Text = RevisionLabel();
        StatusText.Text = string.Empty;
        CommitText.Text = string.Empty;
        CommitMeta.Text = string.Empty;
        PreviousButton.IsEnabled = false;
        PreviousButton.Content = Strings.Get("blame.previous.none");
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
        Editor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);

        if (commit.IsUncommitted)
        {
            CommitText.Text = Strings.Get("blame.uncommitted");
            CommitMeta.Text = Strings.Get("blame.line", index + 1);
        }
        else
        {
            CommitText.Text = $"{commit.ShortSha}  {commit.Summary}";
            CommitMeta.Text = Strings.Get(
                "blame.meta",
                commit.Author,
                commit.When.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                index + 1);
        }

        //The button says where it goes, so pressing it is never a guess -- and when it cannot go
        //anywhere it says which of the two reasons applies.
        if (commit.HasPrevious)
        {
            PreviousButton.IsEnabled = true;
            PreviousButton.Content = Strings.Get("blame.previous", Short(commit.PreviousSha!));
        }
        else
        {
            PreviousButton.IsEnabled = false;
            PreviousButton.Content = Strings.Get(commit.IsBoundary ? "blame.previous.first" : "blame.previous.none");
        }
    }

    private async void OnBlamePrevious(object sender, RoutedEventArgs e)
    {
        if (_selected is not { HasPrevious: true } commit)
            return;

        _history.Push(new Step(_path, _revision, Editor.TextArea.Caret.Line));

        //Git's own answer for both: the commit to blame next, and the name the file had there. This
        //is what makes the walk cross a rename without anything here knowing one happened.
        _revision = commit.PreviousSha;
        _path = commit.PreviousPath ?? _path;

        await ReadAsync().ConfigureAwait(true);
    }

    private async void OnBack(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0)
            return;

        Step step = _history.Pop();

        _path = step.Path;
        _revision = step.Revision;

        await ReadAsync().ConfigureAwait(true);

        //Restored after the read, or the caret would land in a document that is about to be replaced.
        if (_lines.Count > 0)
        {
            int line = Math.Clamp(step.Line, 1, _lines.Count);
            Editor.TextArea.Caret.Line = line;
            Editor.ScrollToLine(line);
            SelectLine(line);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

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

    /// <param name="Line">The caret line, so Back returns to the line the walk was following.</param>
    private sealed record Step(string Path, string? Revision, int Line);
}
