using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlickGit.App.Localization;
using FlickGit.App.ViewModels;
using FlickGit.Commits;
using FlickGit.Diff;

namespace FlickGit.App.Views;

/// <summary>
/// The commit window. The code-behind is deliberately thin: what is here is either a view concern
/// (typography, focus, keyboard) or a bridge between a view-model event and a control that has no
/// binding for it.
///
/// The one structural thing it carries is <see cref="Reset"/>. The resident service pre-warms this
/// window and reuses the instance, so it has to be fully re-initialisable with nothing left over
/// from the previous repository.
/// </summary>
public partial class CommitWindow : Window
{
    private CommitViewModel? _viewModel;

    /// <summary>
    /// True when the resident service owns this window: closing hides it so the next right-click
    /// reuses it. False for a one-shot launch, where closing must really close so the process can exit.
    /// </summary>
    public bool KeepAlive { get; init; }

    public CommitWindow()
    {
        InitializeComponent();

        BranchLabel.Text = Strings.Get("branch.label");
        FilesHeader.Text = Strings.Get("commit.files.header");
        MessageHeader.Text = Strings.Get("commit.message.header");
        SelectAllButton.Content = Strings.Get("commit.selectall");
        SelectNoneButton.Content = Strings.Get("commit.selectnone");
        RefreshButton.Content = Strings.Get("commit.button.refresh");
        //CommitPushButton's label is bound, not set: it becomes "Committing when the message arrives..."
        //while an Enter is queued, which is the whole of that feedback.
        CommitButton.Content = Strings.Get("commit.button.commit");
        GenerateButton.Content = Strings.Get("commit.button.generate");
        HintText.Text = Strings.Get("commit.hint");
        CloseButton.Content = Strings.Get("common.close");
        DeleteFileMenuItem.Header = Strings.Get("delete.menu");
        RevertFileMenuItem.Header = Strings.Get("revert.menu");

        DataContextChanged += OnDataContextChanged;

        Diff.SaveRequested += OnDiffSaveRequested;
        Diff.RestageRequested += OnDiffRestageRequested;
        Diff.HunkStageRequested += OnDiffHunkStageRequested;

        //Ctrl+S saves the diff pane's edit. Explicit, never automatic.
        InputBindings.Add(new KeyBinding
        {
            Key = Key.S,
            Modifiers = ModifierKeys.Control,
            Command = new Infrastructure.RelayCommand(() => Diff.RequestSave()),
        });

        //F5 re-reads the status. A window binding rather than a button accelerator, so it works from the
        //diff pane and the file list too -- and it goes through the view model's own command, so it obeys
        //the same "not while busy" rule instead of stacking refreshes on a slow repository.
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
            //Unsubscribed before rebinding. A reused window that subscribed twice would show two error
            //dialogs per failure and close itself twice per commit.
            _viewModel.Committed -= OnCommitted;
            _viewModel.ErrorRaised -= OnErrorRaised;
            _viewModel.FocusMessageRequested -= FocusMessage;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ConfirmAsync = null;
            _viewModel.IsEditorDirty = null;
        }

        _viewModel = e.NewValue as CommitViewModel;

        if (_viewModel is null)
            return;

        _viewModel.Committed += OnCommitted;
        _viewModel.ErrorRaised += OnErrorRaised;
        _viewModel.FocusMessageRequested += FocusMessage;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        //The view model decides *when* to ask; the window owns the only thing that can actually ask.
        _viewModel.ConfirmAsync = (title, question, yes, no) =>
            Task.FromResult(ConfirmWindow.Ask(this, title, question, yes, no));

        //Asked only by the revert confirmation, so it can say that an unsaved edit is not what goes to
        //the Recycle Bin.
        _viewModel.IsEditorDirty = () => Diff.IsDirty;

        Diff.SetTypography(_viewModel.DiffFontFamily, _viewModel.DiffFontSize);

        //The message box, not the file list: the list has safe defaults and the message is the only
        //thing the user has to supply.
        MessageBox.Focus();
    }

    /// <summary>
    /// Re-points this window at a different repository, without touching Git.
    ///
    /// Split from <see cref="RefreshAsync"/> so the window can be shown between the two: the user sees
    /// the right repository name and an empty list immediately, rather than nothing at all until four
    /// Git processes have answered.
    /// </summary>
    public void Reset(Models.RepositoryInfo repository)
    {
        if (_viewModel is null)
            return;

        Diff.Show(null, isLoading: false);
        _viewModel.Reset(repository);
    }

    public async Task RefreshAsync()
    {
        if (_viewModel is null)
            return;

        await _viewModel.RefreshAsync().ConfigureAwait(true);

        //After the list exists, so the caret lands in the message box rather than being moved by the
        //selection that arriving files bring with them. This is the window's whole opening gesture.
        FocusMessage();

        //After the status, because the payload is built from the ticked files. Fire-and-forget, and
        //silent when no provider is configured: the AI is an accelerator, never a dependency.
        _viewModel.BeginGeneration(force: false);
    }

    /// <summary>
    /// Makes the right-clicked row the selected one, so the menu acts on what the pointer is over.
    ///
    /// A ListBox does not select on right-click, and without this the menu would silently target
    /// whatever was selected before -- which for a Delete is the wrong file, with the correct path
    /// shown in a confirmation the user is not reading closely.
    /// </summary>
    private void OnFileListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        //ContainerFromElement rather than a hand-rolled VisualTreeHelper walk: the row template puts the
        //path in two Runs, which are ContentElements rather than Visuals, and GetParent throws on one.
        if (e.OriginalSource is DependencyObject source
            && FileList.ContainerFromElement(source) is ListBoxItem row)
        {
            row.IsSelected = true;
        }
        else if (FileList.SelectedItem is null)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Puts the caret in the message box, at the end of whatever is already there. Called on open, and
    /// again whenever the view model says the caret belongs back here.
    /// </summary>
    private void FocusMessage()
    {
        MessageBox.Focus();
        MessageBox.CaretIndex = MessageBox.Text.Length;
    }

    /// <summary>
    /// The keyboard map.
    ///
    /// <b>Enter commits and pushes.</b> The caret is in the message box from the moment the window
    /// opens, so type-Enter-done is the whole interaction; <c>Shift+Enter</c> is how a multi-line body
    /// gets its newline.
    ///
    /// Two exceptions, both load-bearing. <b>Not while the diff pane has focus:</b> its right-hand
    /// pane is an editor over the user's working tree, and Enter there is a newline in their file.
    /// <b>Esc is handled here even in the diff pane:</b> the Cancel button is <c>IsCancel</c>, so an
    /// unhandled Esc would close the window directly and bypass the one case that must refuse -- a
    /// commit already executing.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (e.Handled || _viewModel is null)
            return;

        //Esc first, and from everywhere including the editor. Without handling it here it would reach
        //the IsCancel button directly and close the window even mid-commit.
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
                //Ctrl+Enter as well as Enter, so it works from anywhere including the file list.
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
        //non-destructive one.
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

        //Reload: the editor's contents are discarded in favour of what is on disk. The user asked for
        //that explicitly, which is the only way it may ever happen.
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

            //Not a re-show: `apply --cached` touches the index and not the working tree, so the document is
            //unchanged and rebuilding it would move the caret off the hunk the user is working through.
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

    private void OnCommitted(CommitResult result)
    {
        if (_viewModel?.CloseAfterCommit == true && !Diff.IsDirty)
            Close();
    }

    private void OnErrorRaised(string title, string message)
    {
        var notice = new NoticeWindow(title, message, compact: false) { Owner = this };
        notice.ShowDialog();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        //An unsaved edit blocks the close, whether it is a real close or a hide. Losing a working-tree
        //edit to a stray Esc is exactly the kind of silent data loss this product must not have.
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
            //Kept for the next request. Hiding rather than closing is what makes the second window open in
            //tens of milliseconds instead of hundreds.
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        //Cancels any in-flight diff. Without this, a diff started for a file clicked just before closing
        //keeps a git.exe alive and completes into a window that no longer exists.
        _viewModel?.Cancel();
        base.OnClosed(e);
    }
}
