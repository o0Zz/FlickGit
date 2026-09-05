using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;
using FlickGit.App.Localization;
using FlickGit.App.Mac.Rendering;
using FlickGit.Diff;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// The side-by-side diff, on AvaloniaEdit.
///
/// The alignment is not this control's work. <c>DiffService.BuildRows</c> pairs the two sides in
/// FlickGit.Core and <c>DiffDocument.Build</c> turns those rows into the two padded documents, one
/// document line per row, filler rows included. That is what lets the panes be scrolled together by
/// offset rather than by line number, and it is why <see cref="DiffBackgroundRenderer"/> can index
/// its rows by document line.
///
/// <b>The chrome is not decoration.</b> The mode header is the user's only signal about whether an
/// edit in the right pane reaches the commit, the staged strip is CLAUDE.md's answer to the
/// staged-versus-worktree trap, and the two footer labels are what say which pane is theirs to type
/// in. Each of those is a rule the pane is required to state, so they are built here in code beside
/// the logic that drives them rather than left to the window that hosts it.
/// </summary>
internal sealed class DiffPane : UserControl
{
    private readonly TextEditor _left = Editor();
    private readonly TextEditor _right = Editor();

    private readonly DiffBackgroundRenderer _leftBackground = new(isLeftPane: true);
    private readonly DiffBackgroundRenderer _rightBackground = new(isLeftPane: false);
    private readonly DiffLineNumberMargin _leftNumbers = new(isLeftPane: true);
    private readonly DiffLineNumberMargin _rightNumbers = new(isLeftPane: false);
    private readonly SearchHighlightRenderer _leftSearch = new();
    private readonly SearchHighlightRenderer _rightSearch = new();
    private readonly DiffOverviewStrip _overview = new();

    /// <summary>The comparison being shown. Permanently visible, never a tooltip.</summary>
    private readonly TextBlock _modeText = new()
    {
        Classes = { "section" },
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly TextBlock _noticeText = new()
    {
        Classes = { "muted", "small" },
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(10, 0),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly TextBlock _dirtyText = new()
    {
        Classes = { "small", "danger" },
        FontWeight = FontWeight.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0),
        IsVisible = false,
    };

    private readonly TextBlock _savedText = new()
    {
        Classes = { "muted", "small" },
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 8, 0),
        IsVisible = false,
    };

    private readonly Button _stageButton = new() { Classes = { "strip" }, MinWidth = 86, IsEnabled = false };
    private readonly Button _unstageButton = new() { Classes = { "strip" }, MinWidth = 94, IsEnabled = false };
    private readonly Button _revertButton = new() { Classes = { "strip" }, MinWidth = 94, IsEnabled = false };
    private readonly Button _saveButton = new() { Classes = { "strip" }, MinWidth = 56, IsEnabled = false };

    private readonly StackPanel _editingBar;

    private readonly TextBlock _stagedStripText = new()
    {
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11.5,
        Margin = new Thickness(0, 0, 12, 0),
    };

    private readonly Button _restageButton = new() { Classes = { "strip" }, MinWidth = 70 };
    private readonly Border _stagedStrip;

    /// <summary>
    /// Covers the panes when there is nothing to show them. A cover rather than a replacement,
    /// because the two editors are the expensive part of this control and switching files must not
    /// rebuild them.
    /// </summary>
    private readonly TextBlock _placeholderText = new()
    {
        Classes = { "muted" },
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        MaxWidth = 360,
    };

    private readonly Border _placeholder;

    private readonly TextBlock _leftLabel = new() { Classes = { "muted", "small" } };

    private readonly TextBlock _rightLabel = new()
    {
        Classes = { "muted", "small" },
        HorizontalAlignment = HorizontalAlignment.Right,
    };

    private readonly TextBox _searchBox = new()
    {
        Classes = { "mono" },
        Width = 220,
        Margin = new Thickness(0, 0, 6, 0),
    };

    private readonly TextBlock _searchSide = new()
    {
        Classes = { "muted", "small" },
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(4, 0),
    };

    private readonly TextBlock _searchCount = new()
    {
        Classes = { "muted", "small" },
        VerticalAlignment = VerticalAlignment.Center,

        //So counting up from "1 of 12" to "12 of 12" does not walk the close button sideways under
        //the pointer.
        MinWidth = 64,
        TextAlignment = TextAlignment.Right,
    };

    private readonly Border _searchBar;

    /// <summary>Which pane the search is walking. Only that one is lit.</summary>
    private TextEditor? _searchPane;

    /// <summary>
    /// The pane a <c>Ctrl+F</c> would search: whichever one last had the keyboard.
    ///
    /// Tracked rather than asked for at the time, because by then the search box has the focus and
    /// the answer would always be neither.
    /// </summary>
    private TextEditor? _lastFocusedEditor;

    private IReadOnlyList<ISegment> _matches = [];
    private int _matchIndex = -1;

    /// <summary>The pane a sync is currently writing to, so its own scroll event is not treated as a gesture.</summary>
    private TextEditor? _syncTarget;

    /// <summary>
    /// The right pane's document, and the only thing that converts it back into the file.
    /// </summary>
    private readonly AlignedDocument _aligned;

    /// <summary>Set while this class is writing the document, so its own change is not "the user typing".</summary>
    private bool _loading;

    /// <summary>Whether the file being shown ends with a newline. Its own property, never inferred.</summary>
    private bool _endsWithNewline;

    /// <summary>An untracked file has no index entry, so there is nothing for a patch to apply to.</summary>
    private bool _untracked;

    /// <summary>The rows currently rendered, which the row-selection arithmetic works over.</summary>
    private IReadOnlyList<DiffRow> _diffRows = [];

    /// <summary>The diff currently shown, for the one question the pane asks of it: is it editable.</summary>
    private SideBySideDiff? _diff;

    /// <summary>
    /// Where a stage or unstage request goes. The window applies it through the view model, which
    /// builds the patch in FlickGit.Core and applies it with <c>git apply --cached</c> — so the index
    /// moves and the working tree never does. The pane decides <i>which rows</i>; it does not know
    /// what a patch is, and it does not know what to do when one is refused.
    /// </summary>
    public Func<IReadOnlySet<int>, bool, Task>? HunkStageRequested { get; set; }

    /// <summary>
    /// Where Ctrl+S and the Save button go. The text travels rather than a path: the encoding, the
    /// BOM and the line endings are the window's to put back, and this is the only value that may
    /// ever be written.
    /// </summary>
    public Func<string, Task>? SaveRequested { get; set; }

    /// <summary>Where the staged strip's one-click restage goes.</summary>
    public Func<Task>? RestageRequested { get; set; }

    /// <summary>
    /// The pane's own undo history, holding one <i>file text</i> per structural rebuild.
    ///
    /// <b>Two histories, and they are ordered by which one is empty.</b> AvaloniaEdit's own
    /// <c>UndoStack</c> takes back a keystroke; it cannot take back a rebuild, because assigning
    /// <c>Text</c> clears it. So a rebuild pushes the pre-rebuild file text here first, and Ctrl+Z
    /// consults the editor's stack while it has anything and this list only when it does not — which
    /// makes the newer history always the one that answers.
    ///
    /// CLAUDE.md is explicit that the alternative is worse: rebuilding through
    /// <c>Document.Replace</c> to keep the editor's undo would leave
    /// <see cref="AlignedDocument"/>'s anchors describing the layout that was just undone, and the
    /// next save would write alignment padding into the user's source.
    /// </summary>
    private readonly List<UndoStep> _undo = [];

    private const int MaxUndoSteps = 32;
    private const long MaxUndoChars = 8L * 1024 * 1024;

    /// <summary>Set while an undo is rebuilding, so a second Ctrl+Z does not race the first.</summary>
    private bool _undoing;

    /// <summary>
    /// The file text the document currently represents, as of the last rebuild.
    ///
    /// Not the same as what the document holds: typing within a line changes the document without a
    /// rebuild, and this deliberately lags behind until one happens. It is what
    /// <see cref="PushUndo"/> records, so an undo step lands <i>before</i> the typing burst that
    /// caused the rebuild rather than after it.
    /// </summary>
    private string _currentFileText = string.Empty;

    /// <summary>Restarted on every keystroke, so a burst of typing costs one re-diff.</summary>
    private readonly DispatcherTimer _rediffTimer =
        new() { Interval = TimeSpan.FromMilliseconds(200) };

    private CancellationTokenSource? _rediffCancellation;

    /// <param name="FileText">The file as it stood before the rebuild.</param>
    /// <param name="CaretFileOffset">
    /// The caret in the <i>file's</i> coordinates. A document offset would land somewhere else: the
    /// filler layout either side of a rebuild is different, which is the whole reason a rebuild loses
    /// the editor's history.
    /// </param>
    private readonly record struct UndoStep(string FileText, int CaretFileOffset);

    private bool _isDirty;

    /// <summary>True once the user has typed something that is not yet saved.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty == value)
                return;

            _isDirty = value;

            _dirtyText.IsVisible = value;
            _saveButton.IsEnabled = value;
        }
    }

    public DiffPane()
    {
        _aligned = new AlignedDocument(_right);

        _right.TextChanged += (_, _) => OnRightTextChanged();

        _rediffTimer.Tick += (_, _) =>
        {
            _rediffTimer.Stop();
            _ = RediffAsync();
        };

        _left.TextArea.TextView.BackgroundRenderers.Add(_leftBackground);
        _right.TextArea.TextView.BackgroundRenderers.Add(_rightBackground);

        //Added after the diff renderers: a match sits inside a row the diff has already tinted, so it
        //has to be painted over it.
        _left.TextArea.TextView.BackgroundRenderers.Add(_leftSearch);
        _right.TextArea.TextView.BackgroundRenderers.Add(_rightSearch);

        _left.TextArea.LeftMargins.Add(_leftNumbers);
        _right.TextArea.LeftMargins.Add(_rightNumbers);

        _left.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_left, _right);
        _right.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_right, _left);

        _left.TextArea.GotFocus += (_, _) => _lastFocusedEditor = _left;
        _right.TextArea.GotFocus += (_, _) => _lastFocusedEditor = _right;

        //The left pane is HEAD and has no caret to place. Hidden rather than disabled, because a
        //blinking bar in a document nobody can type into reads as a bug.
        _left.TextArea.Caret.CaretBrush = Brushes.Transparent;

        _left.Options.EnableHyperlinks = false;
        _right.Options.EnableHyperlinks = false;

        //Two editor conveniences that must not be on: both would insert characters the user did not
        //type into a file this tool is about to write.
        _right.Options.ConvertTabsToSpaces = false;
        _right.Options.CutCopyWholeLine = false;

        _right.TextArea.Caret.PositionChanged += (_, _) => UpdateHunkButtons();
        _right.TextArea.SelectionChanged += (_, _) => UpdateHunkButtons();

        BuildContextMenu();

        //Tunnelling, so this sees Ctrl+Z before the editor does and can decide which history answers.
        //A window-level binding would be tidier and wrong: it would fire wherever focus is, so Ctrl+Z
        //in the commit message box would reach down and undo an edit in a pane nobody is looking at.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        _editingBar = BuildEditingBar();
        _searchBar = BuildSearchBar();
        _stagedStrip = BuildStagedStrip();
        _placeholder = BuildPlaceholder();

        var header = new Border
        {
            Background = Brush("SurfaceAlt"),
            BorderBrush = Brush("Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 6),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
                Children =
                {
                    Place(_modeText, column: 0),
                    Place(_noticeText, column: 1),
                    Place(_editingBar, column: 2),
                },
            },
        };

        var panes = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,4,*,11"),
            Children =
            {
                Place(_left, column: 0),

                //The join, and the whole reason the split is not a fixed 50/50: dragging it gives the
                //right pane room by taking it from the left, which is the only way to see a long line
                //without scrolling.
                Place(
                    new GridSplitter
                    {
                        ResizeDirection = GridResizeDirection.Columns,
                        Background = Brush("Border"),
                    },
                    column: 1),

                Place(_right, column: 2),

                //Down the right edge, mapping the whole file rather than the visible window.
                Place(_overview, column: 3),

                //Last, and spanning everything, so it covers the panes rather than sitting beside them.
                Place(_placeholder, column: 0),
            },
        };

        _placeholder.SetValue(Grid.ColumnSpanProperty, 4);

        var footer = new Border
        {
            Background = Brush("SurfaceAlt"),
            BorderBrush = Brush("Border"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(10, 4),
            Child = new Grid { Children = { _leftLabel, _rightLabel } },
        };

        Content = new Grid
        {
            Background = Brush("SurfaceSunken"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"),
            Children =
            {
                PlaceRow(header, row: 0),
                PlaceRow(_searchBar, row: 1),
                PlaceRow(_stagedStrip, row: 2),
                PlaceRow(panes, row: 3),
                PlaceRow(footer, row: 4),
            },
        };

        //Every string the pane shows comes from the one key = value file per language. Set here
        //rather than inline for the reason the WPF pane sets its own the same way: a literal in a
        //layout expression is a string no translator can reach.
        _stageButton.Content = Strings.Get("hunk.stage");
        _unstageButton.Content = Strings.Get("hunk.unstage");
        _revertButton.Content = Strings.Get("hunk.revert");
        _saveButton.Content = Strings.Get("edit.save.button");
        ToolTip.SetTip(_saveButton, Strings.Get("edit.save"));
        _restageButton.Content = Strings.Get("edit.restage");
        _dirtyText.Text = Strings.Get("edit.dirty");
        _savedText.Text = Strings.Get("edit.saved");
        _stagedStripText.Text = Strings.Get("edit.staged.notice");
        _leftLabel.Text = Strings.Get("diff.left.readonly");
        _rightLabel.Text = Strings.Get("diff.right.readonly");
        _placeholderText.Text = Strings.Get("diff.select.prompt");
    }

    /// <summary>
    /// The editing bar. Hidden entirely when the pane is read-only, so a file that cannot be edited
    /// does not offer a Save button that would do nothing.
    /// </summary>
    private StackPanel BuildEditingBar()
    {
        _stageButton.Click += (_, _) => _ = RaiseHunkAsync(SelectedRows(), unstage: false);
        _unstageButton.Click += (_, _) => _ = RaiseHunkAsync(SelectedRows(), unstage: true);
        _revertButton.Click += (_, _) => _ = RevertRowsAsync(SelectedRows());
        _saveButton.Click += (_, _) => RequestSave();

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            IsVisible = false,
            Children = { _dirtyText, _savedText, _stageButton, _unstageButton, _revertButton, _saveButton },
        };
    }

    /// <summary>
    /// The staged-file strip, with the one-click restage CLAUDE.md asks for.
    ///
    /// A file already staged and then edited is edited in the <i>working tree</i>, so the change will
    /// not be in the commit even though the diff looks complete. This is the only thing that says so.
    /// </summary>
    private Border BuildStagedStrip()
    {
        _restageButton.Click += (_, _) => _ = RaiseRestageAsync();

        return new Border
        {
            IsVisible = false,
            Background = Brush("WarnBackground"),
            BorderBrush = Brush("WarnBorder"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 5),
            Child = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children = { Place(_stagedStripText, column: 0), Place(_restageButton, column: 1) },
            },
        };
    }

    private Border BuildPlaceholder() =>
        new()
        {
            Background = Brush("SurfaceSunken"),
            Child = _placeholderText,
        };

    /// <summary>
    /// The find bar, hidden until Ctrl+F.
    ///
    /// In the pane rather than in the window, and it is the pane's own key: a window binding would
    /// fire wherever focus is, so Ctrl+F in the commit message box would open a search over a diff
    /// the user is not looking at.
    /// </summary>
    private Border BuildSearchBar()
    {
        var next = new Button { Content = "▼", Classes = { "compact" } };
        var previous = new Button { Content = "▲", Classes = { "compact" } };
        var close = new Button { Content = "✕", Classes = { "compact" } };

        //Glyphs on the buttons, words in the tooltips: the bar has to fit beside a 220px box, and
        //the keystrokes are what the tooltips are actually there to teach.
        ToolTip.SetTip(next, Strings.Get("diff.search.next"));
        ToolTip.SetTip(previous, Strings.Get("diff.search.previous"));
        ToolTip.SetTip(close, Strings.Get("diff.search.close"));

        _searchBox.PlaceholderText = Strings.Get("diff.search.label");

        next.Click += (_, _) => MoveMatch(1);
        previous.Click += (_, _) => MoveMatch(-1);
        close.Click += (_, _) => CloseSearch();

        _searchBox.TextChanged += (_, _) => UpdateMatches(keepPosition: false);

        return new Border
        {
            IsVisible = false,
            Background = Brush("SurfaceAlt"),
            BorderBrush = Brush("Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 5),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,

                //The side text is between the box and the arrows: the two documents are aligned row
                //for row, so a match in one pane and the same word in the other are indistinguishable
                //without it.
                Children = { _searchBox, _searchSide, previous, next, _searchCount, close },
            },
        };
    }

    /// <summary>
    /// Renders a diff, or covers the panes when there is nothing to show.
    /// </summary>
    /// <param name="isLoading">
    /// A cold diff is in flight. The panes are covered rather than cleared, so the previous file does
    /// not flash away and back.
    /// </param>
    /// <param name="fileIsStaged">
    /// Whether the index already holds a version of this path — what the staged strip is about.
    /// </param>
    /// <param name="isUntracked">
    /// Whether Git has ever seen the file. An untracked file is in neither HEAD nor the index, so
    /// there is no old side for a hunk patch to apply against.
    /// </param>
    public void Show(SideBySideDiff? diff, bool isLoading, bool fileIsStaged = false, bool isUntracked = false)
    {
        _untracked = isUntracked;

        _rediffTimer.Stop();
        _rediffCancellation?.Cancel();

        //The history belongs to the file that was shown before. A snapshot of another file's text
        //must never be applied to this one.
        _undo.Clear();
        _currentFileText = string.Empty;

        //The term survives a file change — chasing one word through several files is the reason not
        //to close the bar here — but the position in the old file does not.
        _matchIndex = -1;

        _diff = diff;
        IsDirty = false;
        _savedText.IsVisible = false;

        if (isLoading)
        {
            _placeholderText.Text = Strings.Get("diff.loading");
            _placeholder.IsVisible = true;

            return;
        }

        if (diff is null)
        {
            _placeholderText.Text = Strings.Get("diff.select.prompt");
            _placeholder.IsVisible = true;
            _modeText.Text = string.Empty;
            _noticeText.Text = string.Empty;
            _stagedStrip.IsVisible = false;

            SetEditable(editable: false);
            SetRows([]);

            //No file, nothing to search. The bar would otherwise sit over two editors the placeholder
            //has covered, counting matches in a document nobody can see.
            CloseSearch();

            return;
        }

        //A historical diff labels its range; everything else is the working tree against HEAD, which
        //is the only comparison the product computes. A "Working tree" label over two blobs out of
        //the object store would not merely be unhelpful, it would be false — which is the whole
        //reason this reads the range first.
        _modeText.Text = diff.Range is { } range ? range.Label : Strings.Get("diff.mode.head");
        _noticeText.Text = diff.Notice ?? string.Empty;

        IHighlightingDefinition? highlighting = HighlightingManager.Instance
            .GetDefinitionByExtension(System.IO.Path.GetExtension(diff.Path));

        _left.SyntaxHighlighting = highlighting;
        _right.SyntaxHighlighting = highlighting;

        _loading = true;

        try
        {
            switch (diff.RenderMode)
            {
                case DiffRenderMode.Binary:
                    ShowBinary(diff);

                    break;

                case DiffRenderMode.UnifiedReadOnly:
                    ShowUnified(diff);

                    break;

                default:
                    ShowSideBySide(diff, fileIsStaged);

                    break;
            }
        }
        finally
        {
            _loading = false;
        }

        UpdateHunkButtons();
    }

    /// <summary>
    /// The file's text as the editor now holds it — filler lines removed.
    ///
    /// <b>The only value that may ever be written to disk</b>, and it comes out of
    /// <see cref="AlignedDocument"/> rather than from <c>Text</c>, because the document contains
    /// alignment padding the file must never see.
    /// </summary>
    public string FileText() => _aligned.ToFileText(_endsWithNewline);

    /// <summary>
    /// Asks the host to write the edit. The pane never writes: the encoding, the BOM and the line
    /// endings are the view model's to put back.
    /// </summary>
    public void RequestSave()
    {
        if (!IsDirty || SaveRequested is null)
            return;

        _ = SaveRequested(FileText());
    }

    /// <summary>Called after a successful save, so the pane stops claiming to be dirty.</summary>
    public void MarkSaved(SideBySideDiff refreshed)
    {
        _diff = refreshed;
        IsDirty = false;
        _savedText.IsVisible = true;

        UpdateHunkButtons();
    }

    /// <summary>
    /// Updates the staged strip and the hunk buttons without rebuilding anything.
    ///
    /// <c>git apply --cached</c> touches the index and not the working tree, so the document, caret
    /// and scroll position are still correct — and a full <see cref="Show"/> would reset the caret,
    /// disabling the buttons the user is in the middle of using to stage the next hunk.
    /// </summary>
    public void MarkIndexChanged(bool fileIsStaged)
    {
        if (_diff is null)
            return;

        _stagedStrip.IsVisible = fileIsStaged && _diff.IsEditable;

        UpdateHunkButtons();
    }

    private void ShowBinary(SideBySideDiff diff)
    {
        _placeholderText.Text = diff.Notice ?? Strings.Get("files.tooltip.binary");
        _placeholder.IsVisible = true;
        _stagedStrip.IsVisible = false;

        SetEditable(editable: false);
        SetRows([]);
        CloseSearch();
    }

    /// <summary>
    /// Unified fallback for a file too large to diff line by line. Both panes show the same text
    /// rather than pretending to a side-by-side that was never computed.
    /// </summary>
    private void ShowUnified(SideBySideDiff diff)
    {
        string unified = diff.UnifiedText ?? string.Empty;

        SetRows([]);

        _left.Text = unified;
        _aligned.Clear();
        _right.Text = unified;

        //A patch is what this text is, whatever the file's own extension says.
        IHighlightingDefinition? patch = HighlightingManager.Instance.GetDefinition("Patch");

        _left.SyntaxHighlighting = patch;
        _right.SyntaxHighlighting = patch;

        SetEditable(editable: false);

        _placeholder.IsVisible = false;
        _stagedStrip.IsVisible = false;

        //Both documents were just replaced, so the recorded offsets belong to the file before this one.
        UpdateMatches(keepPosition: true);
    }

    private void ShowSideBySide(SideBySideDiff diff, bool fileIsStaged)
    {
        //IsEditable is the diff's own answer and consults three things this pane should not
        //second-guess: whether both sides came from the object store, whether the render mode is a
        //real side-by-side, and whether the file is binary.
        _endsWithNewline = diff.Right.EndsWithNewline;
        _currentFileText = diff.Right.Text;

        BuildDocuments(diff.Rows, preserveCaret: false);

        SetEditable(diff.IsEditable);

        //Not the top. A change three hundred lines down would open on a screenful of unchanged text,
        //and the user would have to hunt for the thing they clicked the file to see.
        ScrollToFirstChange(diff.Rows);

        //CLAUDE.md, "The staged-versus-worktree trap": the right pane is the working tree, so an edit
        //here is not in the commit until the file is restaged.
        _stagedStrip.IsVisible = fileIsStaged && diff.IsEditable;

        _placeholder.IsVisible = false;
    }

    /// <summary>
    /// Puts the pane into or out of editing, which is one decision with four consequences — so it is
    /// one method rather than four lines repeated at each call site.
    /// </summary>
    private void SetEditable(bool editable)
    {
        _right.IsReadOnly = !editable;
        _right.TextArea.Caret.CaretBrush = editable ? Brush("Text") : Brushes.Transparent;

        _editingBar.IsVisible = editable;
        _saveButton.IsEnabled = false;

        _rightLabel.Text = editable
            ? Strings.Get("edit.right.editable")
            : Strings.Get("diff.right.readonly");
    }

    /// <summary>
    /// Replaces both documents from a row list, re-anchoring the fillers.
    ///
    /// The one place the documents are written, so the ordering rules live in one place with them:
    /// rows before text, because the renderers read them during the paint the text change triggers
    /// and a paint against the previous file's rows is a pane briefly coloured by the wrong diff;
    /// and the right side through <see cref="AlignedDocument"/> rather than <c>Text</c>, because
    /// assigning <c>Text</c> would leave the anchors describing the previous layout.
    /// </summary>
    private void BuildDocuments(IReadOnlyList<DiffRow> rows, bool preserveCaret)
    {
        //Captured before the rebuild, in the file's coordinates.
        int caretFileOffset = preserveCaret ? _aligned.CaretFileOffset : 0;
        Vector scroll = ((ILogicalScrollable)_right.TextArea.TextView).Offset;

        (string left, string right, IReadOnlyList<int> fillerLines) = DiffDocument.Build(rows);

        SetRows(rows);

        _left.Text = left;
        _aligned.Load(right, fillerLines);

        //Explicit, rather than left to the Text setter happening to do the same. The two undo
        //histories are ordered only because a rebuild ends the editor's, so whichever is non-empty is
        //the newer one — which is the whole rule in OnPreviewKeyDown.
        _right.Document.UndoStack.ClearAll();

        if (!preserveCaret)
            return;

        _aligned.RestoreCaret(caretFileOffset);
        ((ILogicalScrollable)_right.TextArea.TextView).Offset = scroll;
    }

    /// <summary>Kept in step with the settings, so the numbers sit on the code's baseline.</summary>
    public void SetTypography(FontFamily family, double fontSize)
    {
        foreach (TextEditor editor in new[] { _left, _right })
        {
            editor.FontFamily = family;
            editor.FontSize = fontSize;
        }

        _leftNumbers.SetTypography(family, fontSize);
        _rightNumbers.SetTypography(family, fontSize);
    }

    private void SetRows(IReadOnlyList<DiffRow> rows)
    {
        _diffRows = rows;
        _overview.SetRows(rows);
        _leftBackground.SetRows(rows);
        _rightBackground.SetRows(rows);
        _leftNumbers.SetRows(rows);
        _rightNumbers.SetRows(rows);
    }

    /// <summary>
    /// Opens on the first change with three lines of context above it, per CLAUDE.md — a diff that
    /// opens at line 1 of a thousand-line file has told the reader nothing.
    /// </summary>
    private void ScrollToFirstChange(IReadOnlyList<DiffRow> rows)
    {
        for (int index = 0; index < rows.Count; index++)
        {
            if (rows[index].Kind == DiffLineKind.Unchanged)
                continue;

            //+1 for the 1-based document line, then back up for the context.
            int line = Math.Max(1, index + 1 - 3);

            _right.ScrollToLine(line);
            _right.TextArea.Caret.Line = index + 1;

            return;
        }
    }

    /// <summary>
    /// Copies one pane's scroll offset onto the other, in the same breath as the gesture.
    ///
    /// <b>Through <see cref="ILogicalScrollable"/> on the target's text view</b>, which is Avalonia's
    /// answer to the <c>IScrollInfo</c> the WPF pane uses and a tidier one: <c>Offset</c>,
    /// <c>Extent</c> and <c>Viewport</c> all sit on the same interface, where WPF spreads them over
    /// six properties.
    ///
    /// <b>What actually breaks the feedback loop is the 0.5 comparison, not the flag.</b> Once the
    /// two panes agree, the echo's reverse sync finds nothing to do and stops. That matters because
    /// the flag only catches an echo raised synchronously inside the assignment, and whether
    /// Avalonia raises it there or on the next layout pass is not something to depend on. The flag
    /// stays as the cheap first guard.
    ///
    /// The clamp is the subtle part, and it is the same on both platforms: the two documents have the
    /// same line count but not the same longest line, so the wider pane can scroll somewhere the
    /// narrower one cannot. Asked anyway, the narrower one takes what it can and clamps the rest —
    /// arriving back as an ordinary scroll event indistinguishable from a gesture. Copied on, it
    /// drags the wider pane back, which the user sees as a scrollbar that refuses to move.
    /// </summary>
    private void Sync(TextEditor source, TextEditor target)
    {
        if (ReferenceEquals(source, _syncTarget))
            return;

        var from = (ILogicalScrollable)source.TextArea.TextView;
        var to = (ILogicalScrollable)target.TextArea.TextView;

        Vector desired = from.Offset;
        Vector current = to.Offset;

        double x = current.X;
        double y = current.Y;

        //Vertical offsets are copied outright, which is only correct because both documents have the
        //same number of lines. Horizontal too: reading a long changed line means scrolling both halves.
        if (Math.Abs(current.Y - desired.Y) > 0.5
            && !IsPinnedAtEnd(desired.Y, from.Extent.Height, from.Viewport.Height, current.Y))
        {
            y = desired.Y;
        }

        if (Math.Abs(current.X - desired.X) > 0.5
            && !IsPinnedAtEnd(desired.X, from.Extent.Width, from.Viewport.Width, current.X))
        {
            x = desired.X;
        }

        if (Math.Abs(x - current.X) < 0.5 && Math.Abs(y - current.Y) < 0.5)
            return;

        _syncTarget = target;

        try
        {
            to.Offset = new Vector(x, y);
        }
        finally
        {
            _syncTarget = null;
        }
    }

    /// <summary>
    /// Whether the source pane sits at its own end while the target is already past it.
    ///
    /// A pane at its end has nothing left to say about where the other belongs, so the other is left
    /// where the gesture put it and only the pane that can still move follows. The maximum is
    /// computed the way the text view clamps — extent minus viewport, floored at zero — so this
    /// recognises exactly the offsets it produces.
    /// </summary>
    private static bool IsPinnedAtEnd(double offset, double extent, double viewport, double targetOffset)
    {
        double maximum = Math.Max(0, extent - viewport);

        return offset >= maximum - 0.5 && targetOffset > offset + 0.5;
    }

    /// <summary>
    /// The right-click menu, on <b>both</b> panes and one menu rather than two.
    ///
    /// Staging a hunk is the same operation whichever pane it was asked from: the patch is built from
    /// the rows, and a row is a pair. Two menus would be two places for the enabled state to be
    /// computed and one of them to be wrong.
    /// </summary>
    private void BuildContextMenu()
    {
        var revert = new MenuItem { Header = Strings.Get("hunk.revert") };
        var stage = new MenuItem { Header = Strings.Get("hunk.stage") };
        var unstage = new MenuItem { Header = Strings.Get("hunk.unstage") };

        revert.Click += (_, _) => _ = RevertRowsAsync(_pendingRows);
        stage.Click += (_, _) => _ = RaiseHunkAsync(_pendingRows, unstage: false);
        unstage.Click += (_, _) => _ = RaiseHunkAsync(_pendingRows, unstage: true);

        //Revert first and separated, the way the WPF menu orders them: it edits the document, where
        //the pair below it describe the document to Git.
        var menu = new ContextMenu
        {
            ItemsSource = new Control[] { revert, new Separator(), stage, unstage },
        };

        _left.ContextMenu = menu;
        _right.ContextMenu = menu;

        //The rows are captured when the menu opens rather than when an item is clicked: opening the
        //menu can move focus, and by the time the click arrives the caret may no longer be where the
        //user pointed.
        _left.ContextRequested += (_, e) => OnContextRequested(_left, e, stage, unstage, revert);
        _right.ContextRequested += (_, e) => OnContextRequested(_right, e, stage, unstage, revert);
    }

    private IReadOnlySet<int> _pendingRows = new HashSet<int>();

    private void OnContextRequested(
        TextEditor editor,
        ContextRequestedEventArgs e,
        MenuItem stage,
        MenuItem unstage,
        MenuItem revert)
    {
        if (_diff?.IsEditable != true)
        {
            //Nothing here applies to a historical diff. Suppressed rather than shown with three dead
            //items.
            e.Handled = true;

            return;
        }

        _pendingRows = RowsUnder(editor, e);

        //Disabled rather than hidden, and the refusal is the tooltip: a menu whose items move around
        //is harder to use than one whose items grey out and say why.
        string? cannotStage = WhyCannotStage(_pendingRows);
        string? cannotRevert = WhyCannotRevert(_pendingRows);

        stage.IsEnabled = cannotStage is null;
        unstage.IsEnabled = cannotStage is null;
        ToolTip.SetTip(stage, cannotStage);
        ToolTip.SetTip(unstage, cannotStage);

        //Revert additionally needs a writable pane: it is an edit to the document, where staging only
        //touches the index.
        revert.IsEnabled = cannotRevert is null && !_right.IsReadOnly;
        ToolTip.SetTip(revert, cannotRevert);
    }

    /// <summary>
    /// Why the selected rows cannot be staged, or null when they can.
    ///
    /// A sentence rather than a bool, because every one of these is a state the user can get out of
    /// and a greyed-out button that does not say how is a dead end.
    /// </summary>
    private string? WhyCannotStage(IReadOnlySet<int> rows)
    {
        if (_diff is null || !_diff.IsEditable)
            return null;

        //The row-to-line equivalence holds only for an unmodified document. Staging from a dirty one
        //would build a patch describing lines that are not what is on disk.
        if (IsDirty)
            return Strings.Get("hunk.savefirst");

        //An untracked file is in neither HEAD nor the index, so there is no old side to patch against.
        if (_untracked)
            return Strings.Get("hunk.untracked");

        //Not merely "is anything selected": a caret parked on a context line outside every hunk
        //selects a row that stages nothing.
        return rows.Any(row => Hunks.IsChange(_diffRows[row]))
            ? null
            : Strings.Get("hunk.nothing");
    }

    /// <summary>
    /// Reverting needs only the third of staging's three conditions. It edits the document rather
    /// than describing it to Git, and an untracked file's empty left side makes "revert to nothing" a
    /// legitimate thing to ask for.
    /// </summary>
    private string? WhyCannotRevert(IReadOnlySet<int> rows)
    {
        if (_diff is null || !_diff.IsEditable)
            return null;

        return rows.Any(row => Hunks.IsChange(_diffRows[row]))
            ? null
            : Strings.Get("hunk.nothing");
    }

    private void UpdateHunkButtons()
    {
        bool editable = _diff?.IsEditable == true;
        IReadOnlySet<int> rows = SelectedRows();

        string? refusal = editable ? WhyCannotStage(rows) : null;
        bool can = editable && refusal is null;

        _stageButton.IsEnabled = can;
        _unstageButton.IsEnabled = can;
        ToolTip.SetTip(_stageButton, refusal);
        ToolTip.SetTip(_unstageButton, refusal);

        string? revertRefusal = editable ? WhyCannotRevert(rows) : null;

        _revertButton.IsEnabled = editable && revertRefusal is null;
        ToolTip.SetTip(_revertButton, revertRefusal);
    }

    private IReadOnlySet<int> SelectedRows() => RowsIn(_right);

    /// <summary>
    /// Puts the selected rows back the way the left side has them.
    ///
    /// <b>An edit, not a Git operation.</b> Nothing is staged, nothing is written and no process
    /// runs, so Ctrl+Z takes it back and Ctrl+S is still the only thing that reaches the disk. That
    /// is what makes it safe on one click for something that reads as "discard my work": until the
    /// user saves, none has been.
    ///
    /// The rows are rebuilt from the new text rather than patched in place — a revert changes which
    /// lines are filler on both sides, and the filler layout <i>is</i> the alignment.
    /// </summary>
    private async Task RevertRowsAsync(IReadOnlySet<int> rows)
    {
        if (_diff is not { } diff || _right.IsReadOnly || rows.Count == 0)
            return;

        if (Hunks.RevertRows(_diffRows, rows) is not { } reverted)
            return;

        //Before the await, because this is the state the revert was computed against. The document's
        //own text rather than a remembered field: typing since the last rebuild is in the document,
        //and undo has to give it back.
        PushUndo(_aligned.ToFileText(_endsWithNewline));

        IReadOnlyList<DiffRow> rebuilt = await Rediff(diff, reverted).ConfigureAwait(true);

        //A different file was clicked while this was computing; these rows are not this document's.
        if (!ReferenceEquals(_diff, diff))
            return;

        _currentFileText = reverted;
        Rebuild(rebuilt);
        IsDirty = true;

        UpdateHunkButtons();
    }

    private void OnRightTextChanged()
    {
        if (_loading || _right.IsReadOnly)
            return;

        IsDirty = true;

        //A save is what clears this, not the next keystroke.
        _savedText.IsVisible = false;

        //Cancelled and restarted on each keystroke, so a burst of typing costs one re-diff.
        _rediffCancellation?.Cancel();
        _rediffTimer.Stop();
        _rediffTimer.Start();

        UpdateHunkButtons();
    }

    /// <summary>
    /// Recomputes the diff from the base text against the edited file text.
    ///
    /// <b>No Git call.</b> The moment the user types a character, any hunk list produced by
    /// <c>git diff</c> is stale — which is the whole reason the viewer diffs two in-memory buffers
    /// rather than parsing Git's output.
    ///
    /// <b>The two branches are the interesting part.</b> Typing inside a line leaves the row
    /// structure identical, and rebuilding the documents for that would flicker and — because a
    /// rebuild clears the editor's undo stack — fill the pane's own history with entries the user
    /// never made. So when the filler layout is unchanged, only the rows and the background layer
    /// are refreshed and the editor's history is left intact. A rebuild happens only when the
    /// alignment actually moved, and then it records exactly one undo step for the burst that led
    /// there.
    /// </summary>
    private async Task RediffAsync()
    {
        if (_diff is not { } diff || _right.IsReadOnly)
            return;

        var cancellation = new CancellationTokenSource();
        _rediffCancellation = cancellation;

        string editedText = _aligned.ToFileText(_endsWithNewline);

        try
        {
            IReadOnlyList<DiffRow> rows = await Rediff(diff, editedText).ConfigureAwait(true);

            //Superseded by a later keystroke, or a different file was clicked while this computed.
            if (cancellation.IsCancellationRequested
                || _rediffCancellation != cancellation
                || !ReferenceEquals(_diff, diff))
            {
                return;
            }

            if (FillerLayoutMatches(rows))
            {
                SetRows(rows);
                InvalidateDiffLayers();

                //No rebuild, so the editor's own history is intact and there is nothing to record —
                //but a stale value here would make the next undo step land a typing burst too far
                //back.
                _currentFileText = editedText;

                return;
            }

            //The layout moved, so this is the keystroke that ends the editor's undo history. One step
            //for the burst that led here, holding the text as it was *before* it.
            PushUndo(_currentFileText);

            _currentFileText = editedText;
            Rebuild(rows);
        }
        catch (OperationCanceledException)
        {
            //A later keystroke won. Nothing to undo and nothing to report.
        }
    }

    /// <summary>
    /// Whether a new row list pads the two panes in exactly the same places as the current one.
    ///
    /// The test is the filler layout rather than the row contents, because the filler layout is what
    /// the documents encode: same padding, same documents, and the colours are the only thing that
    /// needs to change.
    /// </summary>
    private bool FillerLayoutMatches(IReadOnlyList<DiffRow> rows)
    {
        if (rows.Count != _diffRows.Count)
            return false;

        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Right.IsFiller != _diffRows[i].Right.IsFiller
                || rows[i].Left.IsFiller != _diffRows[i].Left.IsFiller)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Repaints the diff colours without touching the documents.
    ///
    /// The background layer specifically: the renderers draw there, and invalidating the whole
    /// control would re-measure text that has not moved.
    /// </summary>
    private void InvalidateDiffLayers()
    {
        _left.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        _right.TextArea.TextView.InvalidateLayer(KnownLayer.Background);

        _leftNumbers.InvalidateVisual();
        _rightNumbers.InvalidateVisual();
    }

    /// <summary>Takes back the most recent structural rebuild.</summary>
    private async Task UndoAsync()
    {
        if (_diff is not { } diff || _undo.Count == 0)
            return;

        _undoing = true;

        try
        {
            UndoStep step = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);

            IReadOnlyList<DiffRow> rebuilt = await Rediff(diff, step.FileText).ConfigureAwait(true);

            if (!ReferenceEquals(_diff, diff))
                return;

            _currentFileText = step.FileText;
            Rebuild(rebuilt);
            _aligned.RestoreCaret(step.CaretFileOffset);

            //Still dirty unless everything has been taken back: the file on disk has not moved.
            IsDirty = _undo.Count > 0 || !string.Equals(step.FileText, diff.Right.Text, StringComparison.Ordinal);
        }
        finally
        {
            _undoing = false;
        }
    }

    /// <summary>Re-diffs a new right-hand text against the unchanged base, off the UI thread.</summary>
    private static Task<IReadOnlyList<DiffRow>> Rediff(SideBySideDiff diff, string fileText) =>
        Task.Run(() => DiffService.Rediff(
            diff.Left.Text,
            fileText,
            diff.RenderMode == DiffRenderMode.SideBySideWithWordDiff));

    private void Rebuild(IReadOnlyList<DiffRow> rows)
    {
        _loading = true;

        try
        {
            BuildDocuments(rows, preserveCaret: true);
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>
    /// Called immediately before a rebuild, because a rebuild is the only thing that loses history.
    /// </summary>
    private void PushUndo(string fileText)
    {
        _undo.Add(new UndoStep(fileText, _aligned.CaretFileOffset));

        long chars = 0;

        foreach (UndoStep step in _undo)
            chars += step.FileText.Length;

        //One step is always kept whatever it costs: a file large enough to blow the budget on its own
        //is precisely one where losing an edit hurts.
        while (_undo.Count > MaxUndoSteps || (_undo.Count > 1 && chars > MaxUndoChars))
        {
            chars -= _undo[0].FileText.Length;
            _undo.RemoveAt(0);
        }
    }

    /// <summary>
    /// Opens the find bar on the pane that has focus, or re-focuses it if already open.
    /// </summary>
    public void OpenSearch(TextEditor? pane = null)
    {
        //Nothing to search behind the placeholder.
        if (_placeholder.IsVisible)
            return;

        _searchPane = pane ?? _lastFocusedEditor ?? _right;

        _searchSide.Text = Strings.Get(
            ReferenceEquals(_searchPane, _left) ? "diff.search.left" : "diff.search.right");

        _searchBar.IsVisible = true;
        _searchBox.Focus();
        _searchBox.SelectAll();

        UpdateMatches(keepPosition: false);
    }

    /// <summary>
    /// Closes the find bar, and reports whether it was open.
    ///
    /// The return value is what lets the window give Esc to the search first and to closing the
    /// window second, per CLAUDE.md: "esc close the search bar if open, otherwise the window".
    /// </summary>
    public bool CloseSearch()
    {
        if (!_searchBar.IsVisible)
            return false;

        _searchBar.IsVisible = false;
        _matchIndex = -1;

        //Cleared before the pane is forgotten: SetMatches decides which renderer to clear by
        //comparing against _searchPane, so nulling it first would leave the highlights lit.
        SetMatches([]);
        _searchPane = null;

        return true;
    }

    private void UpdateMatches(bool keepPosition)
    {
        if (_searchPane is not { } pane)
            return;

        string term = _searchBox.Text ?? string.Empty;
        var matches = new List<ISegment>();

        if (term.Length > 0)
        {
            string text = pane.Text;

            for (int at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                 at >= 0;
                 at = text.IndexOf(term, at + term.Length, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new TextSegment { StartOffset = at, Length = term.Length });
            }
        }

        SetMatches(matches);

        if (keepPosition)
        {
            //Clamped rather than reset: a rebuild that removed the last match must not leave the
            //index pointing past the end of the list.
            _matchIndex = Math.Min(_matchIndex, matches.Count - 1);
            UpdateSearchCount();

            return;
        }

        if (matches.Count == 0)
        {
            _matchIndex = -1;
            UpdateSearchCount();

            return;
        }

        //The first match at or after where the user already is, so typing a term goes forward rather
        //than back to the top of a file they have scrolled down through.
        int from = pane.SelectionLength > 0 ? pane.SelectionStart : pane.CaretOffset;
        int index = 0;

        for (int i = 0; i < matches.Count; i++)
        {
            if (matches[i].Offset >= from)
            {
                index = i;
                break;
            }
        }

        ShowMatch(index);
    }

    /// <summary>Only the searched pane is lit: the same word in the other one is not on the walk.</summary>
    private void SetMatches(IReadOnlyList<ISegment> matches)
    {
        _matches = matches;

        _leftSearch.SetMatches(ReferenceEquals(_searchPane, _left) ? matches : []);
        _rightSearch.SetMatches(ReferenceEquals(_searchPane, _right) ? matches : []);

        _left.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        _right.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);

        UpdateSearchCount();
    }

    private void MoveMatch(int delta)
    {
        if (_matches.Count == 0)
            return;

        //Wraps, which is what F3 does everywhere else.
        int index = (_matchIndex + delta + _matches.Count) % _matches.Count;

        ShowMatch(index);
    }

    private void ShowMatch(int index)
    {
        if (_searchPane is not { } pane || index < 0 || index >= _matches.Count)
            return;

        _matchIndex = index;
        ISegment match = _matches[index];

        pane.Select(match.Offset, match.Length);
        pane.ScrollToLine(pane.Document.GetLineByOffset(match.Offset).LineNumber);

        UpdateSearchCount();
    }

    private void UpdateSearchCount() =>
        //The same two strings the WPF pane uses, so the count reads identically on both platforms.
        _searchCount.Text = _matches.Count == 0
            ? (_searchBox.Text?.Length > 0 ? Strings.Get("diff.search.nomatches") : string.Empty)
            : Strings.Get("diff.search.count", _matchIndex + 1, _matches.Count);

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled)
            return;

        if (HandleSearchKey(e))
            return;

        if (e.Key != Key.Z)
            return;

        bool undoGesture = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                           || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        if (!undoGesture || _diff?.IsEditable != true || _right.IsReadOnly)
            return;

        //Unhandled on purpose: the key carries on to the editor, which is what takes back a keystroke.
        if (_right.Document.UndoStack.CanUndo || _undo.Count == 0)
            return;

        e.Handled = true;

        if (!_undoing)
            _ = UndoAsync();
    }

    /// <summary>The keys the find bar owns, and true when the key was one of them.</summary>
    private bool HandleSearchKey(KeyEventArgs e)
    {
        bool command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        switch (e.Key)
        {
            case Key.F when command:
                OpenSearch();
                e.Handled = true;

                return true;

            case Key.F3:
                //Shift+F3 goes backwards, which is the convention everywhere this key appears.
                MoveMatch(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                e.Handled = true;

                return true;

            //Esc is not handled here. The commit window intercepts it first -- deliberately, so a
            //commit in flight can refuse to close -- and calls CloseSearch itself.
            default:
                return false;
        }
    }

    private async Task RaiseHunkAsync(IReadOnlySet<int> rows, bool unstage)
    {
        if (HunkStageRequested is null || rows.Count == 0)
            return;

        await HunkStageRequested(rows, unstage).ConfigureAwait(true);
    }

    private async Task RaiseRestageAsync()
    {
        if (RestageRequested is null)
            return;

        await RestageRequested().ConfigureAwait(true);
    }

    /// <summary>
    /// The rows the pointer is over, or the caret's if there is no pointer.
    ///
    /// A right-click inside an existing selection means the selection; anywhere else means the one
    /// row pointed at, which is what makes "right-click the line you mean" work without first
    /// selecting it. Shift+F10 has no pointer, and then the caret is what it means.
    /// </summary>
    private IReadOnlySet<int> RowsUnder(TextEditor editor, ContextRequestedEventArgs e)
    {
        if (!e.TryGetPosition(editor, out Point point))
            return RowsIn(editor);

        if (editor.GetPositionFromPoint(point) is not { } position)
            return RowsIn(editor);

        if (editor.SelectionLength > 0)
        {
            int first = editor.Document.GetLineByOffset(editor.SelectionStart).LineNumber;
            int last = editor.Document.GetLineByOffset(editor.SelectionStart + editor.SelectionLength).LineNumber;

            if (position.Line >= first && position.Line <= last)
                return RowsBetween(first, last);
        }

        return RowsBetween(position.Line, position.Line);
    }

    private IReadOnlySet<int> RowsIn(TextEditor editor)
    {
        int first = editor.TextArea.Caret.Line;
        int last = first;

        if (editor.SelectionLength > 0)
        {
            first = editor.Document.GetLineByOffset(editor.SelectionStart).LineNumber;
            last = editor.Document.GetLineByOffset(editor.SelectionStart + editor.SelectionLength).LineNumber;
        }

        return RowsBetween(first, last);
    }

    /// <summary>
    /// The row indices covered by a line range, expanded to the whole hunk when it is a single line.
    ///
    /// <b>A caret on a context line means the hunk it belongs to.</b> Asking the user to land exactly
    /// on a changed line would make it a game. The expansion is <c>Hunks.Find</c> and
    /// <c>Hunks.RowsOf</c> in FlickGit.Core — the same functions the WPF pane calls and the same ones
    /// the patch generator works from, so what is highlighted and what is staged cannot disagree.
    /// </summary>
    private IReadOnlySet<int> RowsBetween(int firstLine, int lastLine)
    {
        var rows = new HashSet<int>();

        for (int line = firstLine; line <= lastLine; line++)
        {
            int row = line - 1;

            if (row >= 0 && row < _diffRows.Count)
                rows.Add(row);
        }

        if (rows.Count == 1 && Hunks.Find(_diffRows).FirstOrDefault(h => h.Covers(rows.First())) is { } hunk)
            return Hunks.RowsOf(_diffRows, hunk);

        return rows;
    }

    private static TextEditor Editor() =>
        new()
        {
            //The right pane is re-enabled per diff. The left never is: it is HEAD, and there is
            //nothing on disk it corresponds to.
            IsReadOnly = true,
            ShowLineNumbers = false,
            WordWrap = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),

            //Both panes always show both scrollbars, so the two viewports are the same height
            //whatever each document's longest line is. Letting them come and go is what makes one
            //pane able to scroll a scrollbar's width further than the other.
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
        };

    /// <summary>
    /// One of the palette's brushes, by key.
    ///
    /// A lookup rather than a literal, so this control cannot become a second answer to what the
    /// product's grey is — the same reason the WPF pane writes <c>{StaticResource SurfaceAlt}</c>
    /// rather than a hex value. Application.Current is the only scope that has them: this control is
    /// built in its constructor, before it is attached to any window whose resources could be asked.
    /// </summary>
    private static IBrush Brush(string key) =>
        Application.Current?.FindResource(key) as IBrush ?? Brushes.Transparent;

    private static T Place<T>(T control, int column)
        where T : Control
    {
        control.SetValue(Grid.ColumnProperty, column);

        return control;
    }

    private static T PlaceRow<T>(T control, int row)
        where T : Control
    {
        control.SetValue(Grid.RowProperty, row);

        return control;
    }
}
