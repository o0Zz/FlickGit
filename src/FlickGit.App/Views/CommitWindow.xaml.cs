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
        CommitPushButton.Content = Strings.Get("commit.button.commitpush");
        CommitButton.Content = Strings.Get("commit.button.commit");
        CancelButton.Content = Strings.Get("commit.button.cancel");

        DataContextChanged += OnDataContextChanged;

        Diff.SaveRequested += OnDiffSaveRequested;
        Diff.RestageRequested += OnDiffRestageRequested;
        Diff.HunkStageRequested += OnDiffHunkStageRequested;

        //Ctrl+Enter commits from inside the message box, where plain Enter has to insert a newline:
        //the message is multi-line and the body matters.
        InputBindings.Add(new KeyBinding
        {
            Key = Key.Enter,
            Modifiers = ModifierKeys.Control,
            Command = new Infrastructure.RelayCommand(() => _viewModel?.CommitAndPushCommand.Execute(null)),
        });

        //Ctrl+S saves the diff pane's edit. Explicit, never automatic — CLAUDE.md: "Never
        //auto-save."
        InputBindings.Add(new KeyBinding
        {
            Key = Key.S,
            Modifiers = ModifierKeys.Control,
            Command = new Infrastructure.RelayCommand(() => Diff.RequestSave()),
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
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ConfirmAsync = null;
        }

        _viewModel = e.NewValue as CommitViewModel;

        if (_viewModel is null)
            return;

        _viewModel.Committed += OnCommitted;
        _viewModel.ErrorRaised += OnErrorRaised;
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

    /// <summary>
    /// Adopts a status the caller already fetched, instead of fetching one.
    ///
    /// The counterpart to <see cref="RefreshAsync"/> for the quick-commit popup's handoff.
    /// </summary>
    public void Adopt(Models.RepositoryStatus status, string message, string branchInput)
    {
        if (_viewModel is null)
            return;

        _viewModel.Adopt(status, message, branchInput);
        MessageBox.Focus();
        MessageBox.CaretIndex = MessageBox.Text.Length;
    }

    /// <summary>Loads the status for the repository <see cref="Reset"/> pointed this window at.</summary>
    public async Task RefreshAsync()
    {
        if (_viewModel is null)
            return;

        await _viewModel.RefreshAsync().ConfigureAwait(true);

        //After the list exists, so the caret lands in the message box rather than being moved by
        //the selection that arriving files bring with them.
        MessageBox.Focus();
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
