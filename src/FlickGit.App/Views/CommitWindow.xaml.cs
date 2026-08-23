using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using FlickGit.App.Localization;
using FlickGit.App.ViewModels;
using FlickGit.Commits;
using FlickGit.Diff;

namespace FlickGit.App.Views;

/// <summary>
/// The commit window.
///
/// The code-behind is deliberately thin — CLAUDE.md, "Coding Guidelines" rules out business logic
/// in WPF code-behind. What is here is either a view concern (typography, focus, keyboard) or a
/// bridge between a view-model event and a control that has no binding for it.
///
/// The one structural thing it carries is <see cref="Reset"/>. The resident
/// service pre-warms this window and reuses the instance, so it has to be fully re-initialisable
/// with nothing left over from the previous repository.
/// </summary>
public partial class CommitWindow : Window
{
    private CommitViewModel? _viewModel;

    /// <summary>
    /// True when the resident service owns this window: closing hides it so the next right-click
    /// reuses it, instead of throwing away everything the pre-warm paid for.
    ///
    /// False for a one-shot launch, where closing must really close so the process can exit.
    /// </summary>
    public bool KeepAlive { get; init; }

    public CommitWindow()
    {
        InitializeComponent();

        //Named once, here, rather than duplicated as literals in the XAML. CLAUDE.md, "Interface
        //Text": every string the windows show comes from the language file.
        BranchLabel.Text = Strings.Get("branch.label");
        FilesHeader.Text = Strings.Get("commit.files.header");
        MessageHeader.Text = Strings.Get("commit.message.header");
        SelectAllButton.Content = Strings.Get("commit.selectall");
        SelectNoneButton.Content = Strings.Get("commit.selectnone");
        RefreshButton.Content = Strings.Get("commit.button.refresh");
        //CommitPushButton's label is bound, not set: it becomes "Committing when the message
        //arrives…" while an Enter is queued, which is the whole of that feedback.
        CommitButton.Content = Strings.Get("commit.button.commit");
        GenerateButton.Content = Strings.Get("commit.button.generate");
        HintText.Text = Strings.Get("commit.hint");
        CancelButton.Content = Strings.Get("commit.button.cancel");

        DataContextChanged += OnDataContextChanged;

        Diff.SaveRequested += OnDiffSaveRequested;
        Diff.RestageRequested += OnDiffRestageRequested;
        Diff.HunkStageRequested += OnDiffHunkStageRequested;

        //Ctrl+S saves the diff pane's edit. Explicit, never automatic — CLAUDE.md: "Never
        //auto-save."
        InputBindings.Add(new KeyBinding
        {
            Key = Key.S,
            Modifiers = ModifierKeys.Control,
            Command = new Infrastructure.RelayCommand(() => Diff.RequestSave()),
        });

        //F5 re-reads the status, which the Refresh button already does. A window binding rather than
        //a button accelerator, so it works from the diff pane and the file list as well as from the
        //message box -- and it is bound to the view model's own command, so it obeys the same
        //"not while busy" rule the button does instead of stacking refreshes on a slow repository.
        InputBindings.Add(new KeyBinding
        {
            Key = Key.F5,
            Command = new Infrastructure.RelayCommand(() => _viewModel?.RefreshCommand.Execute(null)),
        });
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            //Unsubscribed before rebinding. A reused window that subscribed twice would show two
            //error dialogs per failure and close itself twice per commit.
            _viewModel.Committed -= OnCommitted;
            _viewModel.ErrorRaised -= OnErrorRaised;
            _viewModel.FocusMessageRequested -= FocusMessage;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ConfirmAsync = null;
        }

        _viewModel = e.NewValue as CommitViewModel;

        if (_viewModel is null)
            return;

        _viewModel.Committed += OnCommitted;
        _viewModel.ErrorRaised += OnErrorRaised;
        _viewModel.FocusMessageRequested += FocusMessage;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        //The guardrail questions. The view model decides *when* to ask; the window owns the only
        //thing that can actually ask.
        _viewModel.ConfirmAsync = (title, question, yes, no) =>
            Task.FromResult(ConfirmWindow.Ask(this, title, question, yes, no));

        Diff.SetTypography(_viewModel.DiffFontFamily, _viewModel.DiffFontSize);

        //The message box, not the file list: the list has safe defaults and the message is the
        //only thing the user has to supply.
        MessageBox.Focus();
    }

    /// <summary>
    /// Re-points this window at a different repository, without touching Git. See the class remarks.
    ///
    /// Split from <see cref="RefreshAsync"/> so the window can be shown between the two: the user
    /// sees the right repository name and an empty list immediately, rather than nothing at all
    /// until four Git processes have answered.
    /// </summary>
    public void Reset(Models.RepositoryInfo repository)
    {
        if (_viewModel is null)
            return;

        Diff.Show(null, isLoading: false);
        _viewModel.Reset(repository);
    }

    /// <summary>Loads the status for the repository <see cref="Reset"/> pointed this window at.</summary>
    public async Task RefreshAsync()
    {
        if (_viewModel is null)
            return;

        await _viewModel.RefreshAsync().ConfigureAwait(true);

        //After the list exists, so the caret lands in the message box rather than being moved by
        //the selection that arriving files bring with them. This is the window's whole opening
        //gesture: the caret is already where the user has to type, and Enter commits from there.
        FocusMessage();

        //After the status, because the payload is built from the ticked files. Fire-and-forget, and
        //silent when no provider, key or consent is configured -- CLAUDE.md: "The AI is an
        //accelerator, never a dependency."
        _viewModel.BeginGeneration(force: false);
    }

    /// <summary>
    /// Puts the caret in the message box, at the end of whatever is already there.
    ///
    /// Called on open, and again whenever the view model says the caret belongs back here: a
    /// generation that failed with an Enter queued, or one that just landed and is waiting for it.
    /// </summary>
    private void FocusMessage()
    {
        MessageBox.Focus();
        MessageBox.CaretIndex = MessageBox.Text.Length;
    }

    /// <summary>
    /// The keyboard map.
    ///
    /// <b>Enter commits and pushes.</b> That is the fast path this window inherited when the
    /// quick-commit popup was removed: the caret is in the message box from the moment the window
    /// opens, so type-Enter-done is the whole interaction. <c>Shift+Enter</c> is how a multi-line
    /// body gets its newline, which is the trade Enter costs.
    ///
    /// Two exceptions, both load-bearing:
    ///
    /// <list type="bullet">
    /// <item><description><b>Not while the diff pane has focus.</b> Its right-hand pane is an
    /// editor over the user's working tree, and Enter there is a newline in their file. Committing
    /// instead would be both surprising and unrecoverable in the same keystroke.</description></item>
    /// <item><description><b>Esc closes, and is handled here even in the diff pane.</b> The Cancel
    /// button is <c>IsCancel</c>, so an unhandled Esc would close the window directly and bypass the
    /// one case that must refuse: a commit already executing.</description></item>
    /// </list>
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Handled || _viewModel is null)
            return;

        //Esc first, and from everywhere including the editor. The Cancel button carries
        //IsCancel="True", so without handling it here Esc inside the diff pane would reach the button
        //directly and close the window even mid-commit -- skipping the one guard that matters.
        if (e.Key == Key.Escape)
        {
            if (_viewModel.EscapePressed())
                Close();

            e.Handled = true;
            return;
        }

        //The editor owns the rest of its keyboard. Enter there is a newline in the user's file.
        if (Diff.IsKeyboardFocusWithin)
            return;

        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Shift:
                //A newline in the message body, which is the one thing Enter can no longer do.
                break;

            case Key.Enter when Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Control:
                //Ctrl+Enter as well as Enter: it was the commit key before this window had the fast
                //path, and it still works from anywhere including the file list.
                _viewModel.EnterPressed(push: true);
                e.Handled = true;
                break;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
            return;

        switch (e.PropertyName)
        {
            case nameof(CommitViewModel.CurrentDiff):
            case nameof(CommitViewModel.IsDiffLoading):
                //Guarded: switching files while an edit is unsaved would silently discard it.
                Diff.Show(
                    _viewModel.CurrentDiff,
                    _viewModel.IsDiffLoading,
                    _viewModel.SelectedFile?.Change.IsStaged ?? false,
                    _viewModel.SelectedFile?.Change.IsUntracked ?? false);
                break;

            case nameof(CommitViewModel.Title):
                Title = _viewModel.Title;
                break;
        }
    }

    // ---- editing ------------------------------------------------------------------

    private async Task OnDiffSaveRequested(string text)
    {
        if (_viewModel is null)
            return;

        SaveOutcome outcome = await _viewModel.SaveCurrentFileAsync(text, force: false).ConfigureAwait(true);

        if (outcome.Succeeded)
        {
            if (_viewModel.CurrentDiff is { } refreshed)
                Diff.MarkSaved(refreshed);

            return;
        }

        if (outcome.Refusal != SaveRefusal.ExternallyModified)
        {
            OnErrorRaised(Strings.Get("edit.save"), outcome.Message ?? string.Empty);
            return;
        }

        //The external-modification prompt. Three explicit choices, and the default is the
        //non-destructive one -- CLAUDE.md: "do not overwrite. Offer reload, overwrite, or
        //save-as."
        bool overwrite = ConfirmWindow.Ask(
            this,
            Strings.Get("edit.external.title"),
            outcome.Message ?? string.Empty,
            Strings.Get("edit.external.overwrite"),
            Strings.Get("edit.external.reload"));

        if (overwrite)
        {
            SaveOutcome forced = await _viewModel.SaveCurrentFileAsync(text, force: true).ConfigureAwait(true);

            if (forced.Succeeded && _viewModel.CurrentDiff is { } saved)
                Diff.MarkSaved(saved);
            else if (!forced.Succeeded)
                OnErrorRaised(Strings.Get("edit.save"), forced.Message ?? string.Empty);

            return;
        }

        //Reload: the editor's contents are discarded in favour of what is on disk. The user asked
        //for that explicitly, which is the only way it may ever happen.
        SideBySideDiff? reloaded = await _viewModel.ReloadCurrentFileAsync().ConfigureAwait(true);

        Diff.Show(reloaded, isLoading: false, _viewModel.SelectedFile?.Change.IsStaged ?? false);
    }

    private async Task OnDiffHunkStageRequested(IReadOnlySet<int> rows, bool unstage)
    {
        if (_viewModel is null)
            return;

        try
        {
            await _viewModel.StageHunkAsync(rows, unstage).ConfigureAwait(true);

            //Not a re-show: `apply --cached` touches the index and not the working tree, so the
            //document is unchanged and rebuilding it would move the caret off the hunk the user is
            //working through.
            Diff.MarkIndexChanged(_viewModel.SelectedFile?.Change.IsStaged ?? false);
        }
        catch (Exception ex)
        {
            OnErrorRaised(Strings.Get("hunk.failed"), ex.Message);
        }
    }

    private async Task OnDiffRestageRequested()
    {
        if (_viewModel is null)
            return;

        try
        {
            await _viewModel.RestageCurrentFileAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            OnErrorRaised(Strings.Get("edit.restage"), ex.Message);
        }
    }

    // ---- commit -------------------------------------------------------------------

    private void OnCommitted(CommitResult result)
    {
        //The short hash, per CLAUDE.md: "Display the short hash from `git rev-parse --short HEAD`."
        //Already in StatusText via the view model; closing is the only thing left to decide.
        if (_viewModel?.CloseAfterCommit == true && !Diff.IsDirty)
            Close();
    }

    private void OnErrorRaised(string title, string message)
    {
        var notice = new NoticeWindow(title, message, compact: false) { Owner = this };
        notice.ShowDialog();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        //An unsaved edit blocks the close, whether it is a real close or a hide. CLAUDE.md: "Dirty
        //state shown in the header, blocking on close." Losing a working-tree edit to a stray Esc is
        //exactly the kind of silent data loss this product must not have.
        if (Diff.IsDirty)
        {
            bool save = ConfirmWindow.Ask(
                this,
                Strings.Get("edit.save"),
                Strings.Get("edit.close.dirty", _viewModel?.CurrentDiff?.Path ?? string.Empty),
                Strings.Get("edit.close.save"),
                Strings.Get("edit.close.discard"));

            if (save)
            {
                e.Cancel = true;
                Diff.RequestSave();
                return;
            }
        }

        if (KeepAlive)
        {
            //Kept for the next request. Hiding rather than closing is what makes the second window
            //open in tens of milliseconds instead of hundreds.
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        //Cancels any in-flight diff. Without this, a diff started for a file clicked just before
        //closing keeps a git.exe alive and completes into a window that no longer exists.
        _viewModel?.Cancel();
        base.OnClosed(e);
    }
}
