using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FlickGit.App.Localization;
using FlickGit.App.Rendering;
using FlickGit.Diff;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;

namespace FlickGit.App.Views;

/// <summary>
/// Two AvalonEdit instances edge to edge; the right one is editable.
///
/// <b>Both documents are built from one <see cref="DiffRow"/> list, one document line per row,
/// with a blank filler line where a side has no content.</b> So document line N is row N in both
/// panes, and synchronised scrolling is an offset copy rather than a mapping that could drift.
///
/// The consequence: the right document is <i>not</i> the file. Saving it verbatim would write
/// alignment padding into the user's source. <see cref="RealText"/> is the only value that may
/// ever reach the disk, and <see cref="AlignedDocument"/> is the only thing that converts.
/// </summary>
public partial class DiffPane : UserControl
{
    private static readonly TimeSpan RediffDelay = TimeSpan.FromMilliseconds(200);

    private readonly DiffBackgroundRenderer _leftRenderer = new(isLeftPane: true);
    private readonly DiffBackgroundRenderer _rightRenderer = new(isLeftPane: false);
    private readonly DiffLineNumberMargin _leftMargin = new(isLeftPane: true);
    private readonly DiffLineNumberMargin _rightMargin = new(isLeftPane: false);
    private readonly DiffOverviewStrip _overview = new();
    private readonly SearchHighlightRenderer _leftSearch = new();
    private readonly SearchHighlightRenderer _rightSearch = new();
    private readonly DispatcherTimer _rediffTimer;

    private const int ContextLinesAboveFirstChange = 3;

    /// <summary>
    /// A step is a whole file text rather than a delta, so the character budget is the limit that
    /// matters: side-by-side goes up to 2 MB, and a few snapshots of that would dominate the
    /// resident service's 80 MB idle working set on their own.
    /// </summary>
    private const int MaxUndoSteps = 100;

    private const int MaxUndoChars = 4_000_000;

    /// <summary>The pane a sync is moving. Its own scroll event is an echo, not a gesture.</summary>
    private TextEditor? _syncTarget;

    private bool _updatingDocument;

    private readonly AlignedDocument _document;

    private IReadOnlyList<DiffRow> _rows = [];
    private SideBySideDiff? _diff;

    private readonly record struct UndoStep(string FileText, int CaretFileOffset);

    /// <summary>
    /// The pane's own undo history, newest last: snapshots of the <i>file text</i>, not the document.
    ///
    /// A rebuild ends AvalonEdit's undo history, so <c>Ctrl+Z</c> after a revert reached an empty
    /// stack. <b>Keeping the editor's history across a rebuild is not the fix and must not be
    /// attempted:</b> the document's filler lines are recorded only in the anchor list
    /// <see cref="AlignedDocument"/> rebuilds for the <i>new</i> layout, so undoing the text alone
    /// would have <see cref="RealText"/> strip the wrong blank lines -- writing padding into the
    /// user's file. A file text plus the base determines the rows outright, so a step is restored by
    /// re-diffing it.
    /// </summary>
    private readonly List<UndoStep> _undo = [];

    /// <summary>
    /// The file text the document currently represents. By the time <see cref="RediffAsync"/> knows
    /// the layout moved, the edit that moved it has happened and the text before it is gone.
    /// </summary>
    private string _currentFileText = string.Empty;

    /// <summary>
    /// True while a step is being restored. <c>Ctrl+Z</c> auto-repeats and the restore has an await
    /// in the middle, so without this the older step's rebuild lands after the newer one. A repeat
    /// arriving mid-flight is dropped rather than queued.
    /// </summary>
    private bool _undoing;

    private readonly MenuItem _revertItem = new();
    private readonly MenuItem _stageItem = new();
    private readonly MenuItem _unstageItem = new();

    /// <summary>
    /// The rows the right-click menu was opened over -- resolved while the user is still aiming, not
    /// at click time, by which point the menu has focus and the pointer has moved.
    /// </summary>
    private IReadOnlySet<int> _menuRows = new HashSet<int>();

    /// <summary>
    /// The pane a <c>Ctrl+F</c> would search: whichever one last had the keyboard.
    ///
    /// Tracked rather than asked for at the time, because by then the search box has the focus and
    /// the answer would always be neither.
    /// </summary>
    private TextEditor? _lastFocusedEditor;

    /// <summary>The pane the open search is running against, and null when the bar is closed.</summary>
    private TextEditor? _searchPane;

    /// <summary>Every occurrence of the term, in document order.</summary>
    private IReadOnlyList<ISegment> _matches = [];

    /// <summary>The match the user is standing on, or -1 for none yet.</summary>
    private int _matchIndex = -1;

    /// <summary>An untracked file has no index entry, so there is nothing for a patch to apply to.</summary>
    private bool _untracked;
    private CancellationTokenSource? _rediffCancellation;
    private bool _isDirty;

    public DiffPane()
    {
        InitializeComponent();

        RightEditor.TextArea.Caret.PositionChanged += (_, _) => UpdateHunkButtons();
        RightEditor.TextArea.SelectionChanged += (_, _) => UpdateHunkButtons();

        StageHunkButton.Content = Strings.Get("hunk.stage");
        UnstageHunkButton.Content = Strings.Get("hunk.unstage");
        RevertHunkButton.Content = Strings.Get("hunk.revert");

        _document = new AlignedDocument(RightEditor);

        LeftEditor.TextArea.TextView.BackgroundRenderers.Add(_leftRenderer);
        RightEditor.TextArea.TextView.BackgroundRenderers.Add(_rightRenderer);

        LeftEditor.TextArea.LeftMargins.Add(_leftMargin);
        RightEditor.TextArea.LeftMargins.Add(_rightMargin);

        //Added after the diff renderers, and at KnownLayer.Selection rather than Background: a match
        //sits inside a row the diff has already tinted, so it has to be painted over it.
        LeftEditor.TextArea.TextView.BackgroundRenderers.Add(_leftSearch);
        RightEditor.TextArea.TextView.BackgroundRenderers.Add(_rightSearch);

        LeftEditor.TextArea.GotKeyboardFocus += (_, _) => _lastFocusedEditor = LeftEditor;
        RightEditor.TextArea.GotKeyboardFocus += (_, _) => _lastFocusedEditor = RightEditor;

        OverviewHost.Content = _overview;

        //Both directions, each guarded. One-way sync breaks the moment the user scrolls the pane
        //that is not the master, which is whichever one the pointer happens to be over.
        LeftEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(LeftEditor, RightEditor);
        RightEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(RightEditor, LeftEditor);

        LeftEditor.TextArea.Caret.CaretBrush = Brushes.Transparent;
        LeftEditor.Options.EnableHyperlinks = false;
        RightEditor.Options.EnableHyperlinks = false;

        //Editor conveniences that must not be on: both would insert characters the user did not type
        //into a file this tool is about to write.
        RightEditor.Options.ConvertTabsToSpaces = false;
        RightEditor.Options.CutCopyWholeLine = false;

        RightEditor.TextChanged += OnRightTextChanged;

        BuildContextMenu();

        _rediffTimer = new DispatcherTimer { Interval = RediffDelay };
        _rediffTimer.Tick += (_, _) =>
        {
            _rediffTimer.Stop();
            _ = RediffAsync();
        };

        LeftLabel.Text = Strings.Get("diff.left.readonly");
        PlaceholderText.Text = Strings.Get("diff.select.prompt");

        SearchLabel.Text = Strings.Get("diff.search.label");
        SearchPreviousButton.ToolTip = Strings.Get("diff.search.previous");
        SearchNextButton.ToolTip = Strings.Get("diff.search.next");
        SearchCloseButton.ToolTip = Strings.Get("diff.search.close");
    }

    public event Func<string, Task>? SaveRequested;

    public event Func<Task>? RestageRequested;

    /// <remarks>
    /// The rows travel rather than a patch: building one needs the <see cref="FileText"/> of both
    /// sides for their line endings, which belongs with the code that owns the repository.
    /// </remarks>
    public event Func<IReadOnlySet<int>, bool, Task>? HunkStageRequested;

    /// <summary>
    /// The diff rows a range of editor lines covers. One document line per row, so an editor line is
    /// a row index plus one -- true only while the document is unmodified, which is why
    /// <see cref="WhyCannotStage"/> refuses once it is dirty.
    /// </summary>
    private IReadOnlySet<int> RowsBetween(int firstLine, int lastLine)
    {
        var rows = new HashSet<int>();

        if (_diff is null)
            return rows;

        //_rows, not _diff.Rows: the live alignment. Staging refuses on a dirty document anyway, but
        //reverting does not, and reverting against a stale row list would rewrite lines the user has
        //since changed.
        for (int line = firstLine; line <= lastLine; line++)
        {
            int row = line - 1;

            if (row >= 0 && row < _rows.Count)
                rows.Add(row);
        }

        //A caret on a context line means the hunk it belongs to. Asking the user to land exactly on a
        //changed line would make it a game.
        if (rows.Count == 1 && Hunks.Find(_rows).FirstOrDefault(h => h.Covers(rows.First())) is { } hunk)
            return Hunks.RowsOf(_rows, hunk);

        return rows;
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

    private IReadOnlySet<int> SelectedRows() => RowsIn(RightEditor);

    /// <summary>
    /// The right-click menu, on <b>both</b> panes, and one <see cref="ContextMenu"/> rather than one
    /// each -- the same items acting on the same rows.
    ///
    /// The left pane's read-only document is no obstacle: reverting writes to the right pane and
    /// staging writes to the index, so neither touches the side the click came from.
    /// </summary>
    private void BuildContextMenu()
    {
        _revertItem.Header = Strings.Get("hunk.revert");
        _revertItem.Click += (_, _) => _ = RevertRowsAsync(_menuRows);

        _stageItem.Header = Strings.Get("hunk.stage");
        _stageItem.Click += (_, _) => RaiseHunk(_menuRows, unstage: false);

        _unstageItem.Header = Strings.Get("hunk.unstage");
        _unstageItem.Click += (_, _) => RaiseHunk(_menuRows, unstage: true);

        //A disabled item shows no tooltip unless asked, and the tooltip is the refusal.
        foreach (MenuItem item in new[] { _revertItem, _stageItem, _unstageItem })
            ToolTipService.SetShowOnDisabled(item, true);

        var menu = new ContextMenu();
        menu.Items.Add(_revertItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_stageItem);
        menu.Items.Add(_unstageItem);

        LeftEditor.ContextMenu = menu;
        RightEditor.ContextMenu = menu;

        LeftEditor.ContextMenuOpening += (_, e) => OnContextMenuOpening(LeftEditor, e);
        RightEditor.ContextMenuOpening += (_, e) => OnContextMenuOpening(RightEditor, e);
    }

    private void OnContextMenuOpening(TextEditor editor, ContextMenuEventArgs e)
    {
        if (_diff?.IsEditable != true)
        {
            e.Handled = true;
            return;
        }

        _menuRows = RowsUnder(editor, e);

        string? revert = WhyCannotRevert(_menuRows);
        string? stage = WhyCannotStage(_menuRows);

        _revertItem.IsEnabled = revert is null;
        _revertItem.ToolTip = revert;

        _stageItem.IsEnabled = stage is null;
        _stageItem.ToolTip = stage;

        _unstageItem.IsEnabled = stage is null;
        _unstageItem.ToolTip = stage;
    }

    /// <summary>
    /// The rows a right-click aimed at. Inside the selection means the selection, anywhere else
    /// means the line under the pointer.
    /// </summary>
    private IReadOnlySet<int> RowsUnder(TextEditor editor, ContextMenuEventArgs e)
    {
        //Shift+F10 has no pointer to ask, and reports -1 for both coordinates. The caret is what it
        //means.
        if (e.CursorLeft < 0 || e.CursorTop < 0)
            return RowsIn(editor);

        if (editor.GetPositionFromPoint(Mouse.GetPosition(editor)) is not { } position)
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
        return rows.Any(row => Hunks.IsChange(_rows[row]))
            ? null
            : Strings.Get("hunk.nothing");
    }

    private void UpdateHunkButtons()
    {
        bool editable = _diff?.IsEditable == true;
        IReadOnlySet<int> rows = SelectedRows();

        string? refusal = editable ? WhyCannotStage(rows) : null;
        bool can = editable && refusal is null;

        StageHunkButton.IsEnabled = can;
        UnstageHunkButton.IsEnabled = can;
        StageHunkButton.ToolTip = refusal;
        UnstageHunkButton.ToolTip = refusal;

        //Reverting needs only the third of staging's three conditions. It edits the document rather
        //than describing it to Git, and an untracked file's empty left side makes "revert to nothing"
        //a legitimate thing to ask for.
        string? revertRefusal = editable ? WhyCannotRevert(rows) : null;

        RevertHunkButton.IsEnabled = editable && revertRefusal is null;
        RevertHunkButton.ToolTip = revertRefusal;
    }

    private string? WhyCannotRevert(IReadOnlySet<int> rows)
    {
        if (_diff is null || !_diff.IsEditable)
            return null;

        return rows.Any(row => Hunks.IsChange(_rows[row]))
            ? null
            : Strings.Get("hunk.nothing");
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty == value)
                return;

            _isDirty = value;
            DirtyText.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            SaveButton.IsEnabled = value;
        }
    }

    /// <summary>
    /// The file's text with the filler lines removed. <b>The only value that may ever be written to
    /// disk</b> -- saving the document verbatim would insert alignment padding into the user's source.
    ///
    /// A filler line the user has typed into is no longer a filler, and is kept.
    /// </summary>
    private string RealText() => _document.ToFileText(_diff?.Right.EndsWithNewline ?? true);

    public void SetTypography(string fontFamily, double fontSize)
    {
        var family = new FontFamily(fontFamily);

        foreach (TextEditor editor in new[] { LeftEditor, RightEditor })
        {
            editor.FontFamily = family;
            editor.FontSize = fontSize;
        }

        _leftMargin.SetTypography(family, fontSize);
        _rightMargin.SetTypography(family, fontSize);
    }

    public void Show(SideBySideDiff? diff, bool isLoading, bool fileIsStaged = false, bool isUntracked = false)
    {
        _untracked = isUntracked;

        _rediffTimer.Stop();
        _rediffCancellation?.Cancel();

        //The history belongs to the file that was shown before. A snapshot of another file's text must
        //never be applied to this one.
        _undo.Clear();
        _currentFileText = string.Empty;

        //The term survives a file change -- chasing one word through several files is the reason not
        //to close the bar here -- but the position in the old file does not.
        _matchIndex = -1;

        _diff = diff;
        IsDirty = false;

        if (isLoading)
        {
            PlaceholderText.Text = Strings.Get("diff.loading");
            Placeholder.Visibility = Visibility.Visible;
            return;
        }

        if (diff is null)
        {
            PlaceholderText.Text = Strings.Get("diff.select.prompt");
            Placeholder.Visibility = Visibility.Visible;
            ModeText.Text = string.Empty;
            NoticeText.Text = string.Empty;
            StagedStrip.Visibility = Visibility.Collapsed;

            //No file, nothing to search. The bar would otherwise sit over two editors the placeholder
            //has covered, counting matches in a document nobody can see.
            CloseSearch();
            return;
        }

        //A historical diff labels its range; everything else is the working tree against HEAD, which
        //is the only comparison the product computes. A "Working tree" label over two blobs out of
        //the object store would not merely be unhelpful, it would be false -- which is the whole
        //reason this reads the range first.
        ModeText.Text = diff.Range is { } range ? range.Label : Strings.Get("diff.mode.head");

        NoticeText.Text = diff.Notice ?? string.Empty;

        IHighlightingDefinition? highlighting = HighlightingManager.Instance
            .GetDefinitionByExtension(System.IO.Path.GetExtension(diff.Path));

        LeftEditor.SyntaxHighlighting = highlighting;
        RightEditor.SyntaxHighlighting = highlighting;

        switch (diff.RenderMode)
        {
            case DiffRenderMode.Binary:
                PlaceholderText.Text = diff.Notice ?? Strings.Get("files.tooltip.binary");
                Placeholder.Visibility = Visibility.Visible;
                StagedStrip.Visibility = Visibility.Collapsed;
                CloseSearch();
                return;

            case DiffRenderMode.UnifiedReadOnly:
                ShowUnified(diff);
                return;

            default:
                ShowSideBySide(diff, fileIsStaged);
                return;
        }
    }

    /// <summary>
    /// Updates the staged strip and the hunk buttons without rebuilding anything.
    ///
    /// <c>git apply --cached</c> touches the index and not the working tree, so the document, caret
    /// and scroll position are still correct -- and a full <see cref="Show"/> would reset the caret,
    /// disabling the buttons the user is in the middle of using to stage the next hunk.
    /// </summary>
    public void MarkIndexChanged(bool fileIsStaged)
    {
        if (_diff is null)
            return;

        StagedStripText.Text = Strings.Get("edit.staged.notice");
        StagedStrip.Visibility = fileIsStaged && _diff.IsEditable ? Visibility.Visible : Visibility.Collapsed;

        UpdateHunkButtons();
    }

    public void MarkSaved(SideBySideDiff refreshed)
    {
        _diff = refreshed;
        IsDirty = false;
        SavedText.Visibility = Visibility.Visible;
    }

    private void ShowUnified(SideBySideDiff diff)
    {
        SetRows([]);

        _updatingDocument = true;
        try
        {
            LeftEditor.Text = diff.UnifiedText ?? string.Empty;

            _document.Load(string.Empty, []);
        }
        finally
        {
            _updatingDocument = false;
        }

        LeftEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("Patch");

        SetEditable(editable: false);
        Placeholder.Visibility = Visibility.Collapsed;
        StagedStrip.Visibility = Visibility.Collapsed;

        //Both documents were just replaced, so the recorded offsets belong to the file before this one.
        UpdateMatches(keepPosition: true);
    }

    private void ShowSideBySide(SideBySideDiff diff, bool fileIsStaged)
    {
        BuildDocuments(diff.Rows, diff.Right.Text, preserveCaret: false);

        SetEditable(diff.IsEditable);

        //Not ScrollToHome. A change three hundred lines down would open on a screenful of unchanged
        //text, and the user would have to hunt for the thing they clicked the file to see.
        ScrollToFirstChange(diff.Rows);

        //CLAUDE.md, "The staged-versus-worktree trap": the right pane is the working tree, so an edit
        //here is not in the commit until the file is restaged.
        StagedStripText.Text = Strings.Get("edit.staged.notice");
        StagedStrip.Visibility = fileIsStaged && diff.IsEditable ? Visibility.Visible : Visibility.Collapsed;
        SavedText.Visibility = Visibility.Collapsed;

        Placeholder.Visibility = Visibility.Collapsed;
    }

    private void SetEditable(bool editable)
    {
        RightEditor.IsReadOnly = !editable;
        RightEditor.TextArea.Caret.CaretBrush = editable ? Brushes.Black : Brushes.Transparent;

        EditingBar.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = false;

        RightLabel.Text = editable
            ? Strings.Get("edit.right.editable")
            : Strings.Get("diff.right.readonly");
    }

    /// <param name="fileText">
    /// What the rebuilt document represents, in the file's own terms. Recorded rather than derived:
    /// a value reconstructed from the document would be exactly as trustworthy as the filler layout
    /// it was reconstructed through.
    /// </param>
    private void BuildDocuments(IReadOnlyList<DiffRow> rows, string fileText, bool preserveCaret)
    {
        //Captured before the rebuild, in the file's coordinates: the filler layout either side of it
        //is different, so a document offset would land somewhere else.
        int caretFileOffset = preserveCaret ? _document.CaretFileOffset : 0;
        double verticalOffset = RightEditor.VerticalOffset;

        (string left, string right, IReadOnlyList<int> fillerLines) = DiffDocument.Build(rows);

        _updatingDocument = true;

        try
        {
            SetRows(rows);

            LeftEditor.Text = left;
            _document.Load(right, fillerLines);

            _currentFileText = fileText;

            //Explicit, rather than left to AvalonEdit's Text setter happening to do the same. The two undo
            //histories are ordered only because a rebuild ends the editor's, so whichever is non-empty is
            //the newer one -- which is the whole rule in OnPreviewKeyDown.
            RightEditor.Document.UndoStack.ClearAll();

            if (preserveCaret)
            {
                _document.RestoreCaret(caretFileOffset);
                RightEditor.ScrollToVerticalOffset(verticalOffset);
            }
        }
        finally
        {
            _updatingDocument = false;
        }

        //Every offset the search recorded was into the document this call has just replaced. Recomputed
        //rather than dropped, so a highlight survives the 200 ms re-diff that follows every keystroke
        //in the right pane -- and with keepPosition, so it does not move the caret while they type.
        UpdateMatches(keepPosition: true);
    }

    private void SetRows(IReadOnlyList<DiffRow> rows)
    {
        _rows = rows;

        if (rows.Count == 0)
            _document.Clear();

        _leftRenderer.SetRows(rows);
        _rightRenderer.SetRows(rows);
        _leftMargin.SetRows(rows);
        _rightMargin.SetRows(rows);
        _overview.SetRows(rows);
    }

    private void OnRightTextChanged(object? sender, EventArgs e)
    {
        if (_updatingDocument || RightEditor.IsReadOnly)
            return;

        IsDirty = true;
        SavedText.Visibility = Visibility.Collapsed;

        //Cancelled and restarted on each keystroke, so a burst of typing costs one re-diff.
        _rediffCancellation?.Cancel();
        _rediffTimer.Stop();
        _rediffTimer.Start();
    }

    /// <summary>
    /// Recomputes the diff from the base text against the edited file text. No Git call: the moment
    /// the user types a character, any hunk list produced by `git diff` is stale.
    /// </summary>
    private async Task RediffAsync()
    {
        if (_diff is null || RightEditor.IsReadOnly)
            return;

        var cancellation = new CancellationTokenSource();
        _rediffCancellation = cancellation;

        string baseText = _diff.Left.Text;
        string editedText = RealText();
        bool wordLevel = _diff.RenderMode == DiffRenderMode.SideBySideWithWordDiff;

        try
        {
            IReadOnlyList<DiffRow> rows = await Task.Run(
                () => DiffService.Rediff(baseText, editedText, wordLevel),
                cancellation.Token).ConfigureAwait(true);

            if (cancellation.IsCancellationRequested || _rediffCancellation != cancellation)
                return;

            //Only rebuild when the alignment actually moved. Typing inside a line leaves the row structure
            //identical, and a rebuild per keystroke would flicker and fill the undo stack with entries the
            //user never made.
            if (FillerLayoutMatches(rows))
            {
                SetRows(rows);
                LeftEditor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
                RightEditor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
                _overview.InvalidateVisual();

                //No rebuild, so the editor's own history is intact and there is nothing to record here -- but
                //a stale value would make the next undo step land a typing burst too far back.
                _currentFileText = editedText;
            }
            else
            {
                //The layout moved, so this is the keystroke that ends the editor's undo history. One step for
                //the burst that led here.
                PushUndo(_currentFileText);

                BuildDocuments(rows, editedText, preserveCaret: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool FillerLayoutMatches(IReadOnlyList<DiffRow> rows)
    {
        if (rows.Count != _rows.Count)
            return false;

        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Right.IsFiller != _rows[i].Right.IsFiller
                || rows[i].Left.IsFiller != _rows[i].Left.IsFiller)
                return false;
        }

        return true;
    }

    private void OnSave(object sender, RoutedEventArgs e) => RequestSave();

    public void RequestSave()
    {
        if (!IsDirty || SaveRequested is null)
            return;

        //RealText, never the document. See the class remarks.
        _ = SaveRequested(RealText());
    }

    private void OnRevertHunk(object sender, RoutedEventArgs e) => _ = RevertRowsAsync(SelectedRows());

    /// <summary>
    /// Puts the selected lines back the way the left pane has them.
    ///
    /// <b>An edit, not a Git operation.</b> Nothing is staged, nothing is written and no process
    /// runs, so <c>Ctrl+Z</c> takes it back and <c>Ctrl+S</c> is still the only thing that reaches
    /// the disk. That is what makes it safe on one click for something that reads as "discard my
    /// work": until the user saves, none has been.
    ///
    /// The rows are rebuilt from the new text rather than patched in place -- a revert changes which
    /// lines are filler on both sides, and the filler layout is the alignment.
    /// </summary>
    private async Task RevertRowsAsync(IReadOnlySet<int> rows)
    {
        if (_diff is not { } diff || RightEditor.IsReadOnly)
            return;

        if (Hunks.RevertRows(_rows, rows) is not { } reverted)
            return;

        //A re-diff already queued from earlier typing would otherwise land after this one and
        //recompute from text this is about to replace.
        _rediffCancellation?.Cancel();
        _rediffTimer.Stop();

        //Before the await, because this is the state the revert was computed against. RealText rather
        //than _currentFileText: within-line typing since the last rebuild is in the document, not the
        //field, and undo has to give it back.
        PushUndo(RealText());

        bool wordLevel = diff.RenderMode == DiffRenderMode.SideBySideWithWordDiff;
        string baseText = diff.Left.Text;

        IReadOnlyList<DiffRow> rebuilt = await Task.Run(
            () => DiffService.Rediff(baseText, reverted, wordLevel)).ConfigureAwait(true);

        //A different file was clicked while this was computing; these rows are not this document's.
        if (!ReferenceEquals(_diff, diff))
            return;

        BuildDocuments(rebuilt, reverted, preserveCaret: true);

        IsDirty = true;
        SavedText.Visibility = Visibility.Collapsed;
        UpdateHunkButtons();
    }

    //Called immediately before a rebuild, because a rebuild is the only thing that loses history.
    private void PushUndo(string fileText)
    {
        _undo.Add(new UndoStep(fileText, _document.CaretFileOffset));

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

    private async Task UndoAsync()
    {
        if (_diff is not { } diff || RightEditor.IsReadOnly || _undo.Count == 0)
            return;

        UndoStep step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        _rediffCancellation?.Cancel();
        _rediffTimer.Stop();

        bool wordLevel = diff.RenderMode == DiffRenderMode.SideBySideWithWordDiff;
        string baseText = diff.Left.Text;

        _undoing = true;

        try
        {
            IReadOnlyList<DiffRow> rows = await Task.Run(
                () => DiffService.Rediff(baseText, step.FileText, wordLevel)).ConfigureAwait(true);

            //A different file was clicked while this was computing. Applying its rows would leave the pane
            //holding one file's text under another's alignment -- a wrong file on disk as soon as it saves.
            if (!ReferenceEquals(_diff, diff))
                return;

            BuildDocuments(rows, step.FileText, preserveCaret: true);

            //The caret the step was taken at, not wherever the undone change left it, and brought into view
            //so undoing something off screen is visible rather than silent.
            _document.RestoreCaret(step.CaretFileOffset);
            RightEditor.TextArea.Caret.BringCaretToView();

            //Back to what is on disk is clean, and saying so re-disables Save and stops the close prompt
            //asking about an edit that no longer exists. Both sides are newline-normalised.
            IsDirty = step.FileText != diff.Right.Text;
            SavedText.Visibility = Visibility.Collapsed;
            UpdateHunkButtons();
        }
        finally
        {
            _undoing = false;
        }
    }

    /// <summary>
    /// <c>Ctrl+F</c>, <c>F3</c>, <c>Esc</c> and <c>Ctrl+Z</c> — everything the pane claims from its
    /// own keyboard.
    ///
    /// <b>Here rather than as window <c>KeyBinding</c>s</b>, for the reason spelled out below for
    /// <c>Ctrl+Z</c>: a window binding fires on the bubble wherever focus is, so <c>Ctrl+F</c> in the
    /// commit message box would open a search bar over a pane the user is not looking at. Tunnelling
    /// into a <see cref="UserControl"/> only happens when the focus is already inside it, which is
    /// exactly "put the caret in a pane, then press Ctrl+F".
    ///
    /// <b>AvalonEdit's own history goes first.</b> Only when its stack is empty does the pane's
    /// history answer. The two cannot come out of order because every <see cref="PushUndo"/> is
    /// followed by a rebuild that clears the editor's stack.
    ///
    /// A window <c>KeyBinding</c> beside Ctrl+S would be tidier and is wrong: those fire on the
    /// bubble wherever focus is, so Ctrl+Z in the commit message box would reach down here and undo
    /// an edit in a pane the user is not looking at.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Handled)
            return;

        if (HandleSearchKey(e))
            return;

        if (e.Key != Key.Z || Keyboard.Modifiers != ModifierKeys.Control)
            return;

        if (_diff?.IsEditable != true || RightEditor.IsReadOnly)
            return;

        //Unhandled on purpose: the key carries on to AvalonEdit, which is what takes back a keystroke.
        if (RightEditor.Document.UndoStack.CanUndo || _undo.Count == 0)
            return;

        if (_undoing)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;
        _ = UndoAsync();
    }

    /// <summary>
    /// The keys the search bar owns, and true when the key was one of them.
    ///
    /// <c>Esc</c> is only ever reached here from the log window. The commit window intercepts it
    /// before the pane sees it -- deliberately, so a commit in flight can refuse to close -- and calls
    /// <see cref="CloseSearch"/> itself.
    /// </summary>
    private bool HandleSearchKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                OpenSearch();
                break;

            //F3 as well as Enter, so the walk carries on with the caret back in the pane.
            case Key.F3 when _searchPane is not null
                             && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift:
                Step(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
                break;

            case Key.Escape when _searchPane is not null:
                CloseSearch();
                break;

            default:
                return false;
        }

        e.Handled = true;
        return true;
    }

    /// <summary>
    /// Opens the bar on the pane that last had the keyboard, or on the right one before either has.
    /// </summary>
    private void OpenSearch()
    {
        //Nothing to search: the placeholder is covering both editors, and their documents still hold
        //whatever was shown last.
        if (Placeholder.Visibility == Visibility.Visible)
            return;

        TextEditor pane = _lastFocusedEditor ?? RightEditor;

        //An empty pane is a dead end rather than a choice: a unified read-only diff puts the whole
        //file in the left editor and leaves the right one empty, and answering "no matches" for a word
        //plainly on screen is worse than searching the side that has the text.
        if (pane.Document.TextLength == 0)
            pane = ReferenceEquals(pane, RightEditor) ? LeftEditor : RightEditor;

        SearchBar.Visibility = Visibility.Visible;

        if (!ReferenceEquals(pane, _searchPane))
        {
            _searchPane = pane;
            _matchIndex = -1;

            SearchSideText.Text = Strings.Get(
                ReferenceEquals(pane, LeftEditor) ? "diff.search.left" : "diff.search.right");

            UpdateMatches(keepPosition: false);
        }

        //SelectAll rather than clear: reopening on a term the user still wants costs one Enter, and
        //replacing it costs one keystroke either way.
        SearchBox.SelectAll();
        SearchBox.Focus();
    }

    /// <summary>
    /// Hides the bar and drops the highlights. <b>Returns false when it was already closed</b>, which
    /// is what lets the commit window ask whether Esc belongs here before spending it on the window.
    /// </summary>
    public bool CloseSearch()
    {
        if (_searchPane is null)
            return false;

        TextEditor pane = _searchPane;

        _searchPane = null;
        _matchIndex = -1;

        bool hadKeyboard = SearchBox.IsKeyboardFocusWithin;

        SetMatches([]);
        SearchBar.Visibility = Visibility.Collapsed;

        //Back to the pane the search was running on -- but only when the box is what held the
        //keyboard. This also closes on its own when the shown file changes, and taking focus then
        //would pull the caret out of the file list the user is arrowing through.
        if (hadKeyboard)
            pane.TextArea.Focus();

        return true;
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) =>
        UpdateMatches(keepPosition: false);

    /// <summary>
    /// <c>Enter</c> is the next match, <c>Shift+Enter</c> the previous -- which is the whole gesture:
    /// type once, then Enter as many times as it takes.
    ///
    /// It reaches here because the commit window's Enter-commits rule already stands down while the
    /// diff pane holds the keyboard, and the box is inside the pane.
    /// </summary>
    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        Step(Keyboard.Modifiers == ModifierKeys.Shift ? -1 : 1);
        e.Handled = true;
    }

    private void OnSearchNext(object sender, RoutedEventArgs e) => StepFromButton(1);

    private void OnSearchPrevious(object sender, RoutedEventArgs e) => StepFromButton(-1);

    private void OnSearchClose(object sender, RoutedEventArgs e) => CloseSearch();

    /// <summary>Steps, then hands the keyboard back to the box so Enter carries on working.</summary>
    private void StepFromButton(int direction)
    {
        Step(direction);
        SearchBox.Focus();
    }

    /// <summary>
    /// Finds every occurrence of the term in the searched pane.
    ///
    /// Case-insensitive, with no regular expressions and no whole-word switch: none of the three was
    /// asked for, and each is a control on a bar whose whole value is that it needs no reading.
    ///
    /// <paramref name="keepPosition"/> is the difference between the user typing in the box and the
    /// document being rebuilt underneath them. A rebuild lands 200 ms after a keystroke in the right
    /// pane, and moving the selection then would drag the caret out from under whatever they are in
    /// the middle of typing -- so the highlights are recomputed and nothing else is touched.
    /// </summary>
    private void UpdateMatches(bool keepPosition)
    {
        if (_searchPane is null)
            return;

        string term = SearchBox.Text;
        var matches = new List<ISegment>();

        if (term.Length > 0)
        {
            string text = _searchPane.Text;

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
            //Clamped rather than reset: a rebuild that removed the last match must not leave the count
            //pointing past the end of the list.
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
        int from = _searchPane.SelectionLength > 0 ? _searchPane.SelectionStart : _searchPane.CaretOffset;
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

        _leftSearch.SetMatches(ReferenceEquals(_searchPane, LeftEditor) ? matches : []);
        _rightSearch.SetMatches(ReferenceEquals(_searchPane, RightEditor) ? matches : []);

        LeftEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        RightEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    /// <summary>
    /// Wraps, in both directions. A key that silently stops working at the end of the file is the one
    /// thing "Enter, Enter, Enter" must not do.
    /// </summary>
    private void Step(int direction)
    {
        if (_matches.Count == 0)
            return;

        if (_matchIndex < 0)
        {
            ShowMatch(direction > 0 ? 0 : _matches.Count - 1);
            return;
        }

        ShowMatch((_matchIndex + direction + _matches.Count) % _matches.Count);
    }

    /// <summary>
    /// Selects the match and scrolls it into view.
    ///
    /// <b>The selection is how the current match is marked</b>, rather than a second brush in
    /// <see cref="SearchHighlightRenderer"/>: one colour to keep legible instead of two, and the match
    /// is already selected if the user wants to copy it. The other pane follows on its own -- row N is
    /// line N in both documents, so <see cref="Sync"/> carries it.
    /// </summary>
    private void ShowMatch(int index)
    {
        if (_searchPane is null || index < 0 || index >= _matches.Count)
            return;

        _matchIndex = index;

        ISegment match = _matches[index];

        _searchPane.Select(match.Offset, match.Length);

        DocumentLine line = _searchPane.Document.GetLineByOffset(match.Offset);
        _searchPane.ScrollTo(line.LineNumber, match.Offset - line.Offset);

        UpdateSearchCount();
    }

    private void UpdateSearchCount() =>
        SearchCountText.Text = SearchBox.Text.Length == 0
            ? string.Empty
            : _matches.Count == 0
                ? Strings.Get("diff.search.nomatches")
                : Strings.Get("diff.search.count", _matchIndex + 1, _matches.Count);

    private void OnStageHunk(object sender, RoutedEventArgs e) => RaiseHunk(SelectedRows(), unstage: false);

    private void OnUnstageHunk(object sender, RoutedEventArgs e) => RaiseHunk(SelectedRows(), unstage: true);

    private void RaiseHunk(IReadOnlySet<int> rows, bool unstage)
    {
        if (HunkStageRequested is null || WhyCannotStage(rows) is not null)
            return;

        if (rows.Count > 0)
            _ = HunkStageRequested(rows, unstage);
    }

    private void OnRestage(object sender, RoutedEventArgs e)
    {
        if (RestageRequested is not null)
            _ = RestageRequested();
    }

    /// <summary>
    /// Scrolls so the first changed row is near the top, or home when nothing changed.
    ///
    /// The caret goes there too: <see cref="SelectedRows"/> reads the caret line, so leaving it on
    /// line 1 while the view shows line 300 would make Stage hunk act on something off-screen.
    /// </summary>
    private void ScrollToFirstChange(IReadOnlyList<DiffRow> rows)
    {
        int first = -1;

        for (int row = 0; row < rows.Count; row++)
        {
            if (Hunks.IsChange(rows[row]))
            {
                first = row;
                break;
            }
        }

        if (first < 0)
        {
            LeftEditor.ScrollToHome();
            RightEditor.ScrollToHome();
            return;
        }

        int line = first + 1;

        RightEditor.TextArea.Caret.Line = line;
        RightEditor.TextArea.Caret.Column = 1;

        //Offset arithmetic rather than ScrollToLine, because "bring this line into view" scrolls the
        //minimum distance -- from the top of a document that lands the target at the *bottom* of the
        //viewport. Every row is one line and the font is monospace, so the offset is exact.
        double lineHeight = RightEditor.TextArea.TextView.DefaultLineHeight;

        if (lineHeight > 0)
        {
            double offset = Math.Max(0, (line - 1 - ContextLinesAboveFirstChange) * lineHeight);

            RightEditor.ScrollToVerticalOffset(offset);
            LeftEditor.ScrollToVerticalOffset(offset);
        }
        else
        {
            //No layout yet, so there is no line height to multiply.
            RightEditor.ScrollToLine(line);
        }
    }

    /// <summary>
    /// Copies one pane's scroll offset onto the other, in the same breath as the gesture.
    ///
    /// <b>Pushed through <see cref="IScrollInfo"/> on the target's text view, never through
    /// <c>TextEditor.ScrollToVerticalOffset</c>.</b> That one does not scroll; it asks the
    /// <c>ScrollViewer</c> to scroll during the next arrange pass, so the move lands a frame late and
    /// the echo arrives after this method has returned -- which no flag can catch.
    /// <c>SetVerticalOffset</c> is what the <c>ScrollViewer</c> itself calls and moves the view
    /// synchronously, so the echo lands inside the <c>try</c> and one field catches it.
    ///
    /// It also clamps: the two documents have the same line count but not the same longest line, so
    /// a horizontal offset the source can reach the target may not. The clamped result comes back as
    /// a scroll event, and treating that as a gesture drags the source back to wherever the target
    /// could reach.
    /// </summary>
    private void Sync(TextEditor source, TextEditor target)
    {
        //The echo from the pane this sync is moving, which is not a user gesture.
        if (ReferenceEquals(source, _syncTarget))
            return;

        //The text views, not the editors: TextEditor.VerticalOffset reads the ScrollViewer, which has
        //not caught up with its own IScrollInfo child when this fires.
        Vector from = source.TextArea.TextView.ScrollOffset;
        var to = (IScrollInfo)target.TextArea.TextView;

        _syncTarget = target;

        try
        {
            //Vertical offsets are copied outright, which is only correct because both documents have the
            //same number of lines. Horizontal too: reading a long changed line means scrolling both halves.
            if (Math.Abs(to.VerticalOffset - from.Y) > 0.5)
                to.SetVerticalOffset(from.Y);

            if (Math.Abs(to.HorizontalOffset - from.X) > 0.5)
                to.SetHorizontalOffset(from.X);
        }
        finally
        {
            _syncTarget = null;
        }
    }
}
