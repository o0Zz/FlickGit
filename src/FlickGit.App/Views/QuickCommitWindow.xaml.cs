using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using FlickGit.App.Localization;
using FlickGit.App.ViewModels;
using FlickGit.Models;

namespace FlickGit.App.Views;

/// <summary>
/// The quick-commit popup.
///
/// Three behaviours here have no precedent anywhere else in the product, and each one is a way the
/// popup can hurt the user if it is wrong:
///
/// <list type="number">
/// <item><description><b>It closes when it loses focus.</b> That is what makes it a popup rather
/// than a window — and it must not fire while a guardrail question is on screen, or answering the
/// question would dismiss the surface waiting for the answer.</description></item>
/// <item><description><b>Enter commits.</b> Every other window in the product treats Enter as
/// "press the default button"; here it is the whole point, so it is intercepted before the message
/// box sees it.</description></item>
/// <item><description><b>It is <c>Topmost</c> without a title bar.</b> A visible popup that does
/// not hold keyboard focus over an Explorer window is worse than no popup: Enter would reach
/// Explorer's file list and open whatever was selected. The host verifies activation and calls
/// <see cref="DemoteFromTopmost"/> if Windows refused it.</description></item>
/// </list>
/// </summary>
public partial class QuickCommitWindow : Window
{
    private QuickCommitViewModel? _viewModel;

    /// <summary>
    /// How many owned dialogs are on screen.
    ///
    /// A counter rather than a bool because a guardrail can be followed by an error notice, and
    /// because it reads as what it is. While it is above zero, losing focus means "a child of mine
    /// took it" and must not dismiss this window.
    /// </summary>
    private int _dialogDepth;

    /// <summary>Set while handing over to the commit window, which takes focus legitimately.</summary>
    private bool _handingOff;

    /// <summary>
    /// Set when Windows refused to give this window the foreground.
    ///
    /// It is then an ordinary background window rather than a popup, so dismissing it on focus loss
    /// would dismiss it immediately -- it never had focus to lose.
    /// </summary>
    private bool _demoted;

    public QuickCommitWindow()
    {
        InitializeComponent();

        //Every literal comes from the language file. The commit window predates this rule and still
        //hard-codes its English in XAML; a second window repeating that would make the
        //inconsistency look intentional.
        BranchLabel.Text = Strings.Get("branch.label");
        CommitButton.Content = Strings.Get("commit.button.commit");
        DetailsButton.Content = Strings.Get("quick.details");
        HintText.Text = Strings.Get("quick.hint");

        DataContextChanged += OnDataContextChanged;
        Deactivated += OnDeactivated;
    }

    /// <summary>
    /// True when the resident service owns this window: closing hides it, so the next trigger
    /// reuses it instead of throwing away everything the pre-warm paid for.
    /// </summary>
    public bool KeepAlive { get; init; }

    /// <summary>Raised when the user asked for the full commit window.</summary>
    public event Action<QuickCommitViewModel>? DetailsRequested;

    /// <summary>Re-points this popup at a repository. No Git — see <see cref="RefreshAsync"/>.</summary>
    public void Reset(RepositoryInfo repository, bool isFallback)
    {
        _dialogDepth = 0;
        _handingOff = false;

        //Restored, because the instance is reused: a trigger that was refused the foreground once
        //must not leave every later popup demoted.
        _demoted = false;
        Topmost = true;

        _viewModel?.Reset(repository, isFallback);
    }

    /// <summary>
    /// Turns this into an ordinary window, because Windows would not bring it to the front.
    ///
    /// <c>Topmost</c> without keyboard focus is the one configuration that is worse than no popup:
    /// over an Explorer window, the user's Enter goes to Explorer's file list and opens whatever
    /// was selected.
    /// </summary>
    public void DemoteFromTopmost()
    {
        _demoted = true;
        Topmost = false;
    }

    /// <summary>Loads the status. Separate from <see cref="Reset"/> so the popup paints first.</summary>
    public async Task RefreshAsync()
    {
        if (_viewModel is null)
            return;

        await _viewModel.RefreshAsync().ConfigureAwait(true);

        //After the summary lands, so the caret is not moved by the branch text arriving.
        MessageBox.Focus();
        MessageBox.CaretIndex = MessageBox.Text.Length;
    }

    /// <summary>Asks for an AI message, if one is configured. Silent when it is not.</summary>
    public void BeginGeneration() => _viewModel?.BeginGeneration();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            //Unsubscribed before rebinding. A reused window that subscribed twice would show two
            //notices per failure.
            _viewModel.Committed -= OnCommitted;
            _viewModel.ErrorRaised -= OnErrorRaised;
            _viewModel.FocusMessageRequested -= OnFocusMessageRequested;
            _viewModel.ConfirmAsync = null;
        }

        _viewModel = e.NewValue as QuickCommitViewModel;

        if (_viewModel is null)
            return;

        _viewModel.Committed += OnCommitted;
        _viewModel.ErrorRaised += OnErrorRaised;
        _viewModel.FocusMessageRequested += OnFocusMessageRequested;

        //The guardrail questions. Wrapped in the depth counter, because a modal child taking
        //activation raises Deactivated here and must not dismiss the window underneath it.
        _viewModel.ConfirmAsync = (title, question, yes, no) =>
        {
            _dialogDepth++;

            try
            {
                return Task.FromResult(ConfirmWindow.Ask(this, title, question, yes, no));
            }
            finally
            {
                _dialogDepth--;
            }
        };
    }

    /// <summary>
    /// The keyboard map from CLAUDE.md's mock-up.
    ///
    /// <c>Tab</c> is deliberately absent: the message box is the first tab stop, so WPF's own
    /// traversal already is "Tab edits". Nothing here inserts a newline — a body belongs in the
    /// commit window, which is what <c>Details…</c> is for.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Handled)
            return;

        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Shift:
                //Commit without pushing. Queues instead if the message is still being written.
                _viewModel?.EnterPressed(push: false);
                e.Handled = true;
                break;

            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
                _viewModel?.EnterPressed(push: true);
                e.Handled = true;
                break;

            case Key.Escape:
                //The view model decides: cancel a queued commit, or let the popup close. It refuses
                //once a commit is actually running, because there is nothing left to cancel that
                //would not leave the repository half-changed.
                if (_viewModel?.EscapePressed() != false)
                    Close();

                e.Handled = true;
                break;
        }
    }

    private void OnDetails(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;

        //Shown first, hidden second. In a one-shot launch ShutdownMode is OnLastWindowClose, so
        //hiding this window before the commit window exists would end the process.
        _handingOff = true;
        DetailsRequested?.Invoke(_viewModel);
        Close();
    }

    private void OnCommitted(Commits.CommitResult result)
    {
        //The toast is the tray's job. This window's part is to get out of the way, because the
        //next thing the user does is not in it.
        if (_viewModel?.CloseAfterCommit == true)
            Close();
    }

    private void OnErrorRaised(string title, string message)
    {
        _dialogDepth++;

        try
        {
            //Owned, so it appears over the popup rather than behind it, and modal so the popup
            //cannot be dismissed underneath it.
            var notice = new NoticeWindow(title, message, compact: false) { Owner = this };
            notice.ShowDialog();
        }
        finally
        {
            _dialogDepth--;
        }
    }

    /// <summary>
    /// Generation failed with a commit queued. CLAUDE.md: cancel the queue, focus the message box,
    /// keep the popup open — so the user types one word and presses Enter again.
    /// </summary>
    private void OnFocusMessageRequested()
    {
        MessageBox.Focus();
        MessageBox.CaretIndex = MessageBox.Text.Length;
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        //CLAUDE.md: "Closes on focus loss." Except when the thing that took focus is one of ours,
        //or when this window is on its way out already.
        if (_dialogDepth > 0 || _handingOff || _demoted)
            return;

        if (_viewModel?.IsBusy == true)
            return;

        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (KeepAlive)
        {
            //Kept for the next trigger. Hiding rather than closing is what makes the second popup
            //appear in tens of milliseconds instead of hundreds.
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
