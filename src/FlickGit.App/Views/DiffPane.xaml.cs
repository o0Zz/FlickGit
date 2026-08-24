using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using FlickGit.App.Localization;
using FlickGit.App.Rendering;
using FlickGit.Diff;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;

namespace FlickGit.App.Views;

/// <summary>
/// The side-by-side diff viewer: two AvalonEdit instances edge to edge, a background renderer each,
/// a number gutter each, and an overview strip down the right-hand edge. The right pane is editable.
///
/// <b>Alignment.</b> Both documents are built from the same <see cref="DiffRow"/> list, one
/// document line per row, with a blank <i>filler</i> line where a side has no content. So document
/// line N is row N in both panes, which makes synchronised scrolling an offset copy rather than a
/// mapping that could drift — CLAUDE.md: "Synchronised scrolling locked to the diff alignment, not
/// to raw line numbers."
///
/// <b>The consequence of that.</b> The right document is therefore <i>not</i> the file: it has
/// filler lines in it, and saving it verbatim would write blank lines into the user's source. Every
/// conversion between the two lives in <see cref="AlignedDocument"/>, and <see cref="RealText"/> is
/// the only value that may ever be written to disk.
///
/// <b>Re-diffing.</b> On edit, the diff is recomputed from the base text against
/// <see cref="RealText"/>, debounced 200 ms and off the UI thread. The documents are rebuilt only
/// when the filler layout actually changed — typing inside an existing line leaves the row
/// structure identical, so the common keystroke costs a repaint and nothing else. That matters
/// beyond performance: every rebuild is an undo-history entry, and a rebuild per character would
/// make undo useless.
/// </summary>
public partial class DiffPane : UserControl
{
    /// <summary>CLAUDE.md: "Re-diff on edit is debounced 200 ms."</summary>
    private static readonly TimeSpan RediffDelay = TimeSpan.FromMilliseconds(200);

    private readonly DiffBackgroundRenderer _leftRenderer = new(isLeftPane: true);
    private readonly DiffBackgroundRenderer _rightRenderer = new(isLeftPane: false);
    private readonly DiffLineNumberMargin _leftMargin = new(isLeftPane: true);
    private readonly DiffLineNumberMargin _rightMargin = new(isLeftPane: false);
    private readonly DiffOverviewStrip _overview = new();
    private readonly DispatcherTimer _rediffTimer;

    /// <summary>
    /// How much unchanged text to leave above the first change when a file is opened.
    ///
    /// Three, matching the context a unified diff hunk carries, so the opening view shows the same
    /// amount of surrounding code that a patch would have.
    /// </summary>
    private const int ContextLinesAboveFirstChange = 3;

    /// <summary>
    /// The pane the current sync is moving. Its own scroll event is an echo, not a gesture, and
    /// telling the two apart is what stops the panes dragging each other backwards. See
    /// <see cref="Sync"/> for why one field is now the whole guard.
    /// </summary>
    private TextEditor? _syncTarget;

    /// <summary>Suppresses the dirty/re-diff handling while this control rewrites the document.</summary>
    private bool _updatingDocument;

    /// <summary>
    /// The editable document, and everything that knows it holds alignment padding rather than the
    /// file itself. See <see cref="AlignedDocument"/> — nothing else in this control converts
    /// between the two.
    /// </summary>
    private readonly AlignedDocument _document;

    private IReadOnlyList<DiffRow> _rows = [];
    private SideBySideDiff? _diff;

    /// <summary>An untracked file has no index entry, so there is nothing for a patch to apply to.</summary>
    private bool _untracked;
    private CancellationTokenSource? _rediffCancellation;
    private bool _isDirty;

    public DiffPane()
    {
        InitializeComponent();

        //The buttons answer for wherever the caret is, so they follow it.
        RightEditor.TextArea.Caret.PositionChanged += (_, _) => UpdateHunkButtons();
        RightEditor.TextArea.SelectionChanged += (_, _) => UpdateHunkButtons();

        //Named here rather than in the XAML, the same rule as everywhere else: every string the
        //windows show comes from the language file.
        StageHunkButton.Content = Strings.Get("hunk.stage");
        UnstageHunkButton.Content = Strings.Get("hunk.unstage");
        RevertHunkButton.Content = Strings.Get("hunk.revert");

        _document = new AlignedDocument(RightEditor);

        LeftEditor.TextArea.TextView.BackgroundRenderers.Add(_leftRenderer);
        RightEditor.TextArea.TextView.BackgroundRenderers.Add(_rightRenderer);

        LeftEditor.TextArea.LeftMargins.Add(_leftMargin);
        RightEditor.TextArea.LeftMargins.Add(_rightMargin);

        OverviewHost.Content = _overview;

        //Both directions, each guarded. One-way sync breaks the moment the user scrolls the pane
        //that is not the master, which is whichever one the pointer happens to be over.
        LeftEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(LeftEditor, RightEditor);
        RightEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(RightEditor, LeftEditor);

        LeftEditor.TextArea.Caret.CaretBrush = Brushes.Transparent;
        LeftEditor.Options.EnableHyperlinks = false;
        RightEditor.Options.EnableHyperlinks = false;

        //Editor conveniences that must not be on: both would insert characters the user did not
        //type into a file this tool is about to write.
        RightEditor.Options.ConvertTabsToSpaces = false;
        RightEditor.Options.CutCopyWholeLine = false;

        RightEditor.TextChanged += OnRightTextChanged;

        _rediffTimer = new DispatcherTimer { Interval = RediffDelay };
        _rediffTimer.Tick += (_, _) =>
        {
            _rediffTimer.Stop();
            _ = RediffAsync();
        };

        LeftLabel.Text = Strings.Get("diff.left.readonly");
        PlaceholderText.Text = Strings.Get("diff.select.prompt");
    }

    /// <summary>Raised when the user asks to save, with the reconstructed file text.</summary>
    public event Func<string, Task>? SaveRequested;

    /// <summary>Raised when the user clicks Restage in the staged-file strip.</summary>
    public event Func<Task>? RestageRequested;

    /// <summary>
    /// Raised to stage or unstage the rows the user has selected.
    /// </summary>
    /// <remarks>
    /// The rows travel rather than a patch: building one needs the <see cref="FileText"/> of both
    /// sides for their line endings, and that belongs with the code that owns the repository rather
    /// than with a control.
    /// </remarks>
    public event Func<IReadOnlySet<int>, bool, Task>? HunkStageRequested;

    /// <summary>True when the editor holds unsaved changes. Blocks closing.</summary>
    /// <summary>
    /// The diff rows the caret or selection covers, as indices into the current diff.
    ///
    /// The editor holds one line per diff row -- that is how the two panes stay aligned -- so an
    /// editor line number is a row index plus one. That equivalence is only true while the document
    /// is unmodified, which is why <see cref="CanStageHunk"/> refuses once it is dirty.
    /// </summary>
    private IReadOnlySet<int> SelectedRows()
    {
        if (_diff is null)
            return new HashSet<int>();

        int firstLine = RightEditor.TextArea.Caret.Line;
        int lastLine = firstLine;

        if (RightEditor.SelectionLength > 0)
        {
            firstLine = RightEditor.Document.GetLineByOffset(RightEditor.SelectionStart).LineNumber;
            lastLine = RightEditor.Document
                .GetLineByOffset(RightEditor.SelectionStart + RightEditor.SelectionLength).LineNumber;
        }

        var rows = new HashSet<int>();

        //_rows, not _diff.Rows: the live alignment, which is what the document in front of the user
        //actually is. The two are the same until an edit re-diffs, and staging refuses on a dirty
        //document anyway -- but reverting does not, and reverting against a stale row list would
        //rewrite lines the user has since changed.
        for (int line = firstLine; line <= lastLine; line++)
        {
            int row = line - 1;

            if (row >= 0 && row < _rows.Count)
                rows.Add(row);
        }

        //A caret sitting on a context line means the hunk that context belongs to, because "this
        //hunk" is the common case and asking the user to land exactly on a changed line would make
        //it a game.
        if (rows.Count == 1 && Hunks.Find(_rows).FirstOrDefault(h => h.Covers(rows.First())) is { } hunk)
            return Hunks.RowsOf(_rows, hunk);

        return rows;
    }

    /// <summary>
    /// Why hunk staging is unavailable, or null when it is available.
    ///
    /// Every refusal is a sentence rather than a disabled button with no explanation, because each
    /// one has a different fix.
    /// </summary>
    private string? WhyCannotStage()
    {
        if (_diff is null || !_diff.IsEditable)
            return null;

        //The row-to-line equivalence holds only for an unmodified document. Staging from a dirty one
        //would build a patch describing lines that are not what is on disk.
        if (IsDirty)
            return Strings.Get("hunk.savefirst");

        //An untracked file is in neither HEAD nor the index, so there is no old side to patch against
        //-- the whole file is the only unit that means anything.
        if (_untracked)
            return Strings.Get("hunk.untracked");

        //Not merely "is anything selected": a caret parked on a context line outside every hunk
        //selects a row that stages nothing, and a button that is enabled and then does nothing is
        //worse than one that explains itself.
        IReadOnlySet<int> rows = SelectedRows();

        return rows.Any(row => Hunks.IsChange(_rows[row]))
            ? null
            : Strings.Get("hunk.nothing");
    }

    private void UpdateHunkButtons()
    {
        bool editable = _diff?.IsEditable == true;

        string? refusal = editable ? WhyCannotStage() : null;
        bool can = editable && refusal is null;

        StageHunkButton.IsEnabled = can;
        UnstageHunkButton.IsEnabled = can;
        StageHunkButton.ToolTip = refusal;
        UnstageHunkButton.ToolTip = refusal;

        //Reverting has one condition of the three staging has: something changed under the
        //selection. It does not need a clean document, because it edits the document rather than
        //describing it to Git -- and it does not need a tracked file, because the left side of an
        //untracked file is empty and "revert to nothing" is a legitimate thing to ask for.
        string? revertRefusal = editable ? WhyCannotRevert() : null;

        RevertHunkButton.IsEnabled = editable && revertRefusal is null;
        RevertHunkButton.ToolTip = revertRefusal;
    }

    /// <summary>Why reverting is unavailable, or null when it is available.</summary>
    private string? WhyCannotRevert()
    {
        if (_diff is null || !_diff.IsEditable)
            return null;

        return SelectedRows().Any(row => Hunks.IsChange(_rows[row]))
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
    /// The file's text as the editor now holds it, with the filler lines removed.
    ///
    /// <b>The only value that may ever be written to disk.</b> The document contains alignment
    /// padding that is not part of the file; saving it verbatim would insert blank lines into the
    /// user's source, which is the worst thing this control could do.
    ///
    /// A filler line that the user has typed into is no longer a filler — it is a line they added,
    /// and it is kept.
    /// </summary>
    private string RealText() => _document.ToFileText(_diff?.Right.EndsWithNewline ?? true);

    /// <summary>Applied to both editors and both gutters, so the panes stay glyph-aligned.</summary>
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

    /// <summary>
    /// Renders a diff, or clears the panes when nothing is selected.
    /// </summary>
    /// <param name="fileIsStaged">Drives the "this file is staged" strip.</param>
    public void Show(SideBySideDiff? diff, bool isLoading, bool fileIsStaged = false, bool isUntracked = false)
    {
        _untracked = isUntracked;

        _rediffTimer.Stop();
        _rediffCancellation?.Cancel();

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
            return;
        }

        //A historical diff labels its range instead of a comparison mode: "Working tree ↔ HEAD"
        //over two blobs out of the object store would not merely be unhelpful, it would be false.
        ModeText.Text = diff.Range is { } range
            ? range.Label
            : diff.ComparisonMode == DiffComparisonMode.WorkingTreeVsIndex
                ? Strings.Get("diff.mode.index")
                : Strings.Get("diff.mode.head");

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
                return;

            case DiffRenderMode.UnifiedReadOnly:
                ShowUnified(diff);
                return;

            default:
                ShowSideBySide(diff, fileIsStaged);
                return;
        }
    }

    /// <summary>Called after a successful save, so the pane stops reporting itself dirty.</summary>
    /// <summary>
    /// Updates the "this file is staged" strip and the hunk buttons, without rebuilding anything.
    ///
    /// For after a hunk was staged. <c>git apply --cached</c> touches the index and not the working
    /// tree, so the document, the caret and the scroll position are all still correct — and a full
    /// <see cref="Show"/> would reset the caret, disabling the very buttons the user is in the middle
    /// of using to stage the next hunk.
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

            //Nothing to align and nothing editable, so the document holds no fillers.
            _document.Load(string.Empty, []);
        }
        finally
        {
            _updatingDocument = false;
        }

        //The unified view is a diff, not a file, so it gets the diff highlighting rather than the
        //file's own language.
        LeftEditor.SyntaxHighlighting = HighlightingManager.Instance.GetDefinition("Patch");

        SetEditable(false, diff);
        Placeholder.Visibility = Visibility.Collapsed;
        StagedStrip.Visibility = Visibility.Collapsed;
    }

    private void ShowSideBySide(SideBySideDiff diff, bool fileIsStaged)
    {
        BuildDocuments(diff.Rows, preserveCaret: false);

        SetEditable(diff.IsEditable, diff);

        //Not ScrollToHome. A change three hundred lines down is a diff that opens on a screenful of
        //unchanged text, and the user has to hunt for the thing they clicked the file to see.
        ScrollToFirstChange(diff.Rows);

        //The trap strip, from CLAUDE.md, "The staged-versus-worktree trap": the right pane is the
        //working tree, so an edit here is not in the commit until the file is restaged.
        StagedStripText.Text = Strings.Get("edit.staged.notice");
        StagedStrip.Visibility = fileIsStaged && diff.IsEditable ? Visibility.Visible : Visibility.Collapsed;
        SavedText.Visibility = Visibility.Collapsed;

        Placeholder.Visibility = Visibility.Collapsed;
    }

    private void SetEditable(bool editable, SideBySideDiff diff)
    {
        RightEditor.IsReadOnly = !editable;
        RightEditor.TextArea.Caret.CaretBrush = editable ? Brushes.Black : Brushes.Transparent;

        EditingBar.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = false;

        RightLabel.Text = editable
            ? Strings.Get("edit.right.editable")
            : Strings.Get("diff.right.readonly");

        _ = diff;
    }

    /// <summary>
    /// Rewrites both documents from a row list, optionally keeping the caret where the user left
    /// it.
    ///
    /// The caret is mapped through the <i>file's</i> coordinates rather than the document's,
    /// because the filler layout on either side of the rebuild is different — that mapping is the
    /// whole reason this is not a plain text assignment.
    /// </summary>
    private void BuildDocuments(IReadOnlyList<DiffRow> rows, bool preserveCaret)
    {
        //Captured before the rebuild, in the file's coordinates: the filler layout either side of
        //it is different, so a document offset would land somewhere else.
        int caretFileOffset = preserveCaret ? _document.CaretFileOffset : 0;
        double verticalOffset = RightEditor.VerticalOffset;

        (string left, string right, IReadOnlyList<int> fillerLines) = DiffDocument.Build(rows);

        _updatingDocument = true;

        try
        {
            SetRows(rows);

            LeftEditor.Text = left;
            _document.Load(right, fillerLines);

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

    // ---- editing ------------------------------------------------------------------

    private void OnRightTextChanged(object? sender, EventArgs e)
    {
        if (_updatingDocument || RightEditor.IsReadOnly)
            return;

        IsDirty = true;
        SavedText.Visibility = Visibility.Collapsed;

        //Cancelled and restarted on each keystroke, so a burst of typing costs one re-diff rather
        //than one per character.
        _rediffCancellation?.Cancel();
        _rediffTimer.Stop();
        _rediffTimer.Start();
    }

    /// <summary>
    /// Recomputes the diff from the base text against the edited file text.
    ///
    /// No Git call: this is why the rendering source is a pair of in-memory buffers rather than
    /// `git diff` output. CLAUDE.md, "Diff Viewer": "The moment the user types a character, any
    /// hunk list produced by `git diff` is stale."
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

            //Only rebuild when the alignment actually moved. Typing inside a line leaves the row
            //structure identical, and a rebuild per keystroke would both flicker and fill the undo
            //stack with entries the user never made.
            if (FillerLayoutMatches(rows))
            {
                SetRows(rows);
                LeftEditor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
                RightEditor.TextArea.TextView.InvalidateLayer(ICSharpCode.AvalonEdit.Rendering.KnownLayer.Background);
                _overview.InvalidateVisual();
            }
            else
            {
                BuildDocuments(rows, preserveCaret: true);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Whether the new row list would produce the same filler layout as the document already has.
    /// </summary>
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

    /// <summary>
    /// Asks the owner to save. Public so <c>Ctrl+S</c> from the window's input bindings lands here
    /// too.
    /// </summary>
    public void RequestSave()
    {
        if (!IsDirty || SaveRequested is null)
            return;

        //RealText, never the document. See the class remarks.
        _ = SaveRequested(RealText());
    }

    private void OnRevertHunk(object sender, RoutedEventArgs e) => _ = RevertSelectionAsync();

    /// <summary>
    /// Puts the selected lines back the way the left pane has them.
    ///
    /// <b>An edit, not a Git operation.</b> Nothing is staged, nothing is written, and no process
    /// runs: the reverted text goes into the editor exactly as if the user had typed it there, so
    /// <c>Ctrl+Z</c> takes it back and <c>Ctrl+S</c> is still the only thing that reaches the disk.
    /// That is what makes this safe to offer on one click for an operation that otherwise reads as
    /// "discard my work" — CLAUDE.md's rule is that uncommitted work is never discarded, and until
    /// the user saves, none has been.
    ///
    /// The rows are rebuilt from the new text rather than patched in place. A revert changes which
    /// lines are filler on both sides, and the filler layout is the alignment — so recomputing it is
    /// the only way to leave the two panes describing the same file.
    /// </summary>
    private async Task RevertSelectionAsync()
    {
        if (_diff is null || RightEditor.IsReadOnly)
            return;

        if (Hunks.RevertRows(_rows, SelectedRows()) is not { } reverted)
            return;

        //Any re-diff already queued from earlier typing would otherwise land after this one and
        //recompute from text this is about to replace.
        _rediffCancellation?.Cancel();
        _rediffTimer.Stop();

        bool wordLevel = _diff.RenderMode == DiffRenderMode.SideBySideWithWordDiff;
        string baseText = _diff.Left.Text;

        //Off the UI thread, like every other re-diff here: a revert on a large file is the same
        //amount of work as a keystroke on one.
        IReadOnlyList<DiffRow> rows = await Task.Run(
            () => DiffService.Rediff(baseText, reverted, wordLevel)).ConfigureAwait(true);

        BuildDocuments(rows, preserveCaret: true);

        //Dirty, because the file on disk still has what was just taken out of the editor. The user
        //saves when they are satisfied, or closes and is asked.
        IsDirty = true;
        SavedText.Visibility = Visibility.Collapsed;
        UpdateHunkButtons();
    }

    private void OnStageHunk(object sender, RoutedEventArgs e) => RaiseHunk(unstage: false);

    private void OnUnstageHunk(object sender, RoutedEventArgs e) => RaiseHunk(unstage: true);

    private void RaiseHunk(bool unstage)
    {
        if (HunkStageRequested is null || WhyCannotStage() is not null)
            return;

        IReadOnlySet<int> rows = SelectedRows();

        if (rows.Count > 0)
            _ = HunkStageRequested(rows, unstage);
    }

    private void OnRestage(object sender, RoutedEventArgs e)
    {
        if (RestageRequested is not null)
            _ = RestageRequested();
    }

    /// <summary>
    /// Scrolls so the first changed row is near the top, or home when the file has no changes.
    ///
    /// The caret goes there too, and that is not cosmetic: <c>SelectedRows</c> reads the caret line,
    /// so a caret left on line 1 while the view shows line 300 would make Stage hunk and Revert lines
    /// act on something off-screen.
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
            //No change to go to: an identical file, or one whose only difference is a mode or a
            //rename. The top is the honest place to be.
            LeftEditor.ScrollToHome();
            RightEditor.ScrollToHome();
            return;
        }

        int line = first + 1;

        RightEditor.TextArea.Caret.Line = line;
        RightEditor.TextArea.Caret.Column = 1;

        //Offset arithmetic rather than ScrollToLine, because "bring this line into view" scrolls the
        //minimum distance -- from the top of a document that means the target lands at the *bottom*
        //of the viewport, which is the opposite of what is wanted. Every row is exactly one line and
        //the font is monospace, so the offset is exact.
        double lineHeight = RightEditor.TextArea.TextView.DefaultLineHeight;

        if (lineHeight > 0)
        {
            double offset = Math.Max(0, (line - 1 - ContextLinesAboveFirstChange) * lineHeight);

            RightEditor.ScrollToVerticalOffset(offset);
            LeftEditor.ScrollToVerticalOffset(offset);
        }
        else
        {
            //No layout has happened yet, so there is no line height to multiply. Minimal scrolling
            //is worse than none here; the change is at least on screen.
            RightEditor.ScrollToLine(line);
        }
    }

    /// <summary>
    /// Copies one pane's scroll offset onto the other, in the same breath as the gesture that
    /// caused it.
    ///
    /// <b>The offset is pushed through <see cref="IScrollInfo"/> on the target's text view, not
    /// through <c>TextEditor.ScrollToVerticalOffset</c>, and that is the whole of it.</b>
    /// <c>ScrollToVerticalOffset</c> does not scroll; it asks the <c>ScrollViewer</c> to scroll
    /// during the next arrange pass. So the move landed a frame late and — worse — the target's own
    /// <c>ScrollOffsetChanged</c> arrived <i>after</i> this method had returned, which meant the
    /// echo could not be recognised by anything as cheap as a flag. The guard had to be lowered at
    /// <c>DispatcherPriority.Background</c> instead, and every gesture that arrived while it was up
    /// was parked in a field and replayed later. Under a continuous wheel spin that made the target
    /// pane update once per Background dispatch — starved by the very rendering the scroll was
    /// causing — which is the second pane lagging behind the first.
    ///
    /// <c>IScrollInfo.SetVerticalOffset</c> is what the <c>ScrollViewer</c> itself calls, and it
    /// moves the text view <i>synchronously</i>: the offset changes, <c>ScrollOffsetChanged</c> is
    /// raised, and both happen before the call returns. So the echo lands inside the
    /// <c>try</c> below, one field catches it, and there is nothing left to defer or replay. Both
    /// panes are repainted from the same frame as the wheel notch.
    ///
    /// It also clamps, which is the other half of the old bug: the two documents have the same line
    /// count but not the same longest line, so a horizontal offset the source can reach is one the
    /// target may not. The clamped result comes back as a scroll event, and treating that as a
    /// gesture is what used to drag the pane the user actually scrolled back to wherever the other
    /// one could reach. It is an echo like any other now, and ignored like one.
    /// </summary>
    private void Sync(TextEditor source, TextEditor target)
    {
        //The echo from the pane this sync is moving, which is not a user gesture.
        if (ReferenceEquals(source, _syncTarget))
            return;

        //The text views, not the editors: TextEditor.VerticalOffset reads the ScrollViewer, which
        //has not caught up with its own IScrollInfo child yet at the moment this fires. Reading the
        //stale value would sync the target to where the source was one notch ago.
        Vector from = source.TextArea.TextView.ScrollOffset;
        var to = (IScrollInfo)target.TextArea.TextView;

        _syncTarget = target;

        try
        {
            //Vertical offsets are copied outright, which is only correct because both documents have
            //the same number of lines. Horizontal too: reading a long changed line means scrolling
            //both halves together.
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
