using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using FlickGit.App.ViewModels;

namespace FlickGit.App.Views;

/// <summary>
/// The repository palette.
///
/// Everything interesting here is keyboard routing. The query box keeps the caret for the whole
/// session — the user is always typing — so the arrow keys, Enter, Esc and Backspace have to be
/// intercepted on the way in and applied to a list that is never focused. A palette where you have
/// to Tab between the filter and the list is a palette nobody uses twice.
/// </summary>
public partial class PaletteWindow : Window
{
    private PaletteViewModel? _viewModel;

    /// <summary>Set while a chosen action runs, so the closing window does not re-enter anything.</summary>
    private bool _closing;

    /// <summary>
    /// Set when Windows refused to give this window the foreground.
    ///
    /// The rule every popup here follows: <c>Topmost</c> without keyboard focus is worse than not
    /// showing at all, because the keys the user types land in whatever is underneath.
    /// </summary>
    private bool _demoted;

    public PaletteWindow()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Deactivated += OnDeactivated;
    }

    /// <summary>
    /// True when the resident service owns this window: closing hides it, so the next hotkey reuses
    /// it instead of throwing away what the pre-warm paid for.
    /// </summary>
    public bool KeepAlive { get; init; }

    /// <summary>Clears the query and re-renders from the cache. No Git — see <see cref="RefreshAsync"/>.</summary>
    public void Reset()
    {
        _closing = false;
        _demoted = false;
        _viewModel?.Reset();

        QueryBox.Clear();
    }

    /// <summary>
    /// Puts the caret in the query box.
    ///
    /// Called by the host *after* Show, not from <see cref="Reset"/>: a control cannot take keyboard
    /// focus while its window is still hidden, and the pre-warm leaves it hidden by design.
    /// </summary>
    public void FocusQuery()
    {
        QueryBox.Focus();
        QueryBox.CaretIndex = QueryBox.Text.Length;
    }

    /// <summary>
    /// Re-reads the repositories. Separate from <see cref="Reset"/> so the palette paints first —
    /// CLAUDE.md gives it 80 ms to appear, which is less than one `git status`.
    /// </summary>
    public Task RefreshAsync() => _viewModel?.RefreshAsync() ?? Task.CompletedTask;

    /// <summary>
    /// Turns this into an ordinary window, because Windows would not bring it to the front.
    ///
    /// <c>Topmost</c> without keyboard focus is the one configuration worse than no popup at all:
    /// over another application's window, the user's Enter goes to whatever is underneath.
    /// </summary>
    public void DemoteFromTopmost()
    {
        _demoted = true;
        Topmost = false;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.CloseRequested -= OnCloseRequested;

        _viewModel = DataContext as PaletteViewModel;

        if (_viewModel is not null)
            _viewModel.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested()
    {
        _closing = true;
        Close();
    }

    /// <summary>
    /// The keys the query box would otherwise swallow or misuse.
    ///
    /// Handled at the window level rather than on the TextBox, so the same routing applies however
    /// focus moved — and marked handled, so the TextBox never also acts on them.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_viewModel is null || _closing)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                //CLAUDE.md: "Esc closes with no side effects."
                e.Handled = true;
                _viewModel.Cancel();
                return;

            case Key.Enter when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                e.Handled = true;
                _viewModel.PullAllBehind();
                return;

            case Key.Enter:
                e.Handled = true;
                _viewModel.Accept();
                return;

            case Key.Down:
                e.Handled = true;
                Move(1);
                return;

            case Key.Up:
                e.Handled = true;
                Move(-1);
                return;

            case Key.Tab:
                //Tab completes rather than moving focus: there is only one control worth focusing,
                //and in a palette Tab means "take the highlighted row".
                e.Handled = true;
                _viewModel.Accept();
                return;

            case Key.Back when QueryBox.CaretIndex == 0 || QueryBox.Text.Length == 0:
                //At the start of the action text, Backspace means "back to the repository list"
                //rather than deleting a character there is none of.
                if (_viewModel.LeaveActionModeIfEmpty())
                {
                    e.Handled = true;
                    QueryBox.CaretIndex = QueryBox.Text.Length;
                }

                return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// Moves the highlight, and scrolls it into view.
    ///
    /// Clamped rather than wrapping: at the bottom of a list of thirty repositories, wrapping to the
    /// top is never what the user meant.
    /// </summary>
    private void Move(int delta)
    {
        if (RowList.Items.Count == 0)
            return;

        int next = Math.Clamp(RowList.SelectedIndex + delta, 0, RowList.Items.Count - 1);

        RowList.SelectedIndex = next;
        RowList.ScrollIntoView(RowList.Items[next]);
    }

    private void OnRowActivated(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PaletteRow)
            _viewModel?.Accept();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        //Same rule as the popup: a transient surface goes away when the user looks elsewhere. There
        //are no owned dialogs here, so there is nothing of ours that could legitimately take focus.
        if (!_closing && !_demoted)
            Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (KeepAlive)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
