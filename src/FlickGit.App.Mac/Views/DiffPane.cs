using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit;
using FlickGit.App.Localization;
using FlickGit.App.Mac.Rendering;
using FlickGit.Diff;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// The side-by-side diff, on AvaloniaEdit.
///
/// <b>Read-only for now.</b> The editable right pane, line and hunk staging, the find bar and the
/// overview strip are the rest of this milestone; what is here is the half the commit window cannot
/// do without — two panes that agree about which line is which.
///
/// The alignment is not this control's work. <c>DiffService.BuildRows</c> pairs the two sides in
/// FlickGit.Core and <c>DiffDocument.Build</c> turns those rows into the two padded documents, one
/// document line per row, filler rows included. That is what lets the panes be scrolled together by
/// offset rather than by line number, and it is why <see cref="DiffBackgroundRenderer"/> can index
/// its rows by document line.
/// </summary>
internal sealed class DiffPane : UserControl
{
    private readonly TextEditor _left = Editor();
    private readonly TextEditor _right = Editor();

    private readonly DiffBackgroundRenderer _leftBackground = new(isLeftPane: true);
    private readonly DiffBackgroundRenderer _rightBackground = new(isLeftPane: false);
    private readonly DiffLineNumberMargin _leftNumbers = new(isLeftPane: true);
    private readonly DiffLineNumberMargin _rightNumbers = new(isLeftPane: false);

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

    /// <summary>The rows currently rendered, which the row-selection arithmetic works over.</summary>
    private IReadOnlyList<DiffRow> _diffRows = [];

    /// <summary>The diff currently shown, for the one question the pane asks of it: is it editable.</summary>
    private SideBySideDiff? _diff;

    /// <summary>
    /// Where a stage or unstage request goes. Wired to <c>CommitViewModel.StageHunkAsync</c>, which
    /// builds the patch in FlickGit.Core and applies it with <c>git apply --cached</c> — so the index
    /// moves and the working tree never does. Returns a refusal to show, or null on success.
    /// </summary>
    public Func<IReadOnlySet<int>, bool, Task<string?>>? StageRequested { get; set; }

    /// <summary>True once the user has typed something that is not yet saved.</summary>
    public bool IsDirty { get; private set; }

    public DiffPane()
    {
        _aligned = new AlignedDocument(_right);

        _right.TextChanged += (_, _) =>
        {
            if (!_loading)
                IsDirty = true;
        };

        _left.TextArea.TextView.BackgroundRenderers.Add(_leftBackground);
        _right.TextArea.TextView.BackgroundRenderers.Add(_rightBackground);

        _left.TextArea.LeftMargins.Add(_leftNumbers);
        _right.TextArea.LeftMargins.Add(_rightNumbers);

        _left.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_left, _right);
        _right.TextArea.TextView.ScrollOffsetChanged += (_, _) => Sync(_right, _left);

        BuildContextMenu();

        Content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,4,*"),
            Children =
            {
                Place(_left, column: 0),
                Place(new GridSplitter { ResizeDirection = GridResizeDirection.Columns }, column: 1),
                Place(_right, column: 2),
            },
        };
    }

    /// <summary>
    /// Renders a diff, or clears the panes when there is nothing selected.
    /// </summary>
    public void Show(SideBySideDiff? diff)
    {
        _loading = true;

        try
        {
            Render(diff);
        }
        finally
        {
            _loading = false;
        }

        //A freshly rendered file is by definition unedited, whatever the previous one's state was.
        IsDirty = false;
    }

    /// <summary>
    /// The file's text as the editor now holds it — filler lines removed.
    ///
    /// <b>The only value that may ever be written to disk</b>, and it comes out of
    /// <see cref="AlignedDocument"/> rather than from <c>Text</c>, because the document contains
    /// alignment padding the file must never see.
    /// </summary>
    public string FileText() => _aligned.ToFileText(_endsWithNewline);

    /// <summary>Called after a successful save, so the pane stops claiming to be dirty.</summary>
    public void MarkSaved() => IsDirty = false;

    private void Render(SideBySideDiff? diff)
    {
        if (diff is null)
        {
            _left.Text = string.Empty;
            _right.Text = string.Empty;
            _aligned.Clear();
            _right.IsReadOnly = true;
            _diff = null;
            SetRows([]);

            return;
        }

        _diff = diff;

        //Unified fallback for a file too large to diff line by line. Both panes show the same text
        //rather than pretending to a side-by-side that was never computed.
        if (diff.RenderMode == DiffRenderMode.UnifiedReadOnly)
        {
            string unified = diff.UnifiedText ?? string.Empty;

            SetRows([]);
            _aligned.Clear();
            _right.IsReadOnly = true;
            _left.Text = unified;
            _right.Text = unified;

            return;
        }

        (string left, string right, IReadOnlyList<int> fillerLines) = DiffDocument.Build(diff.Rows);

        //Rows before text: the renderers read them during the paint the text change triggers, and a
        //paint against the previous file's rows is a pane briefly coloured by the wrong diff.
        SetRows(diff.Rows);

        _left.Text = left;

        //Through AlignedDocument, which anchors the filler lines as it loads. Assigning Text here
        //instead would leave the anchors describing the previous file, and the next save would drop
        //the wrong lines out of the user's source.
        _endsWithNewline = diff.Right.EndsWithNewline;
        _aligned.Load(right, fillerLines);

        //IsEditable is the diff's own answer and consults three things this pane should not
        //second-guess: whether both sides came from the object store, whether the render mode is a
        //real side-by-side, and whether the file is binary.
        _right.IsReadOnly = !diff.IsEditable;

        ScrollToFirstChange(diff.Rows);
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
        var stage = new MenuItem { Header = Strings.Get("hunk.stage") };
        var unstage = new MenuItem { Header = Strings.Get("hunk.unstage") };

        stage.Click += (_, _) => _ = RaiseHunkAsync(_pendingRows, unstage: false);
        unstage.Click += (_, _) => _ = RaiseHunkAsync(_pendingRows, unstage: true);

        var menu = new ContextMenu { ItemsSource = new[] { stage, unstage } };

        _left.ContextMenu = menu;
        _right.ContextMenu = menu;

        //The rows are captured when the menu opens rather than when an item is clicked: opening the
        //menu can move focus, and by the time the click arrives the caret may no longer be where the
        //user pointed.
        _left.ContextRequested += (_, e) => OnContextRequested(_left, e, stage, unstage);
        _right.ContextRequested += (_, e) => OnContextRequested(_right, e, stage, unstage);
    }

    private IReadOnlySet<int> _pendingRows = new HashSet<int>();

    private void OnContextRequested(
        TextEditor editor,
        ContextRequestedEventArgs e,
        MenuItem stage,
        MenuItem unstage)
    {
        _pendingRows = RowsUnder(editor, e);

        //Nothing to stage from a diff that is not against the working tree, or from a selection with
        //no changed row in it. Disabled rather than hidden: a menu whose items move around is harder
        //to use than one whose items grey out.
        bool can = _diff?.IsEditable == true && _pendingRows.Count > 0;

        stage.IsEnabled = can;
        unstage.IsEnabled = can;
    }

    private async Task RaiseHunkAsync(IReadOnlySet<int> rows, bool unstage)
    {
        if (StageRequested is null || rows.Count == 0)
            return;

        await StageRequested(rows, unstage).ConfigureAwait(true);
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

            //Both panes always show both scrollbars, so the two viewports are the same height
            //whatever each document's longest line is. Letting them come and go is what makes one
            //pane able to scroll a scrollbar's width further than the other.
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
        };

    private static T Place<T>(T control, int column)
        where T : Control
    {
        control.SetValue(Grid.ColumnProperty, column);

        return control;
    }
}
