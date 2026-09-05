using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using FlickGit.App.CommandLine;
using FlickGit.App.Localization;
using FlickGit.App.ViewModels;
using FlickGit.Branches;
using FlickGit.Commits;
using FlickGit.Diff;
using FlickGit.Models;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// The commit window on Avalonia.
///
/// <b>The view model is the Windows one, unchanged.</b> <see cref="CommitViewModel"/> lives in
/// FlickGit.App.Common and already drives the WPF window; nothing about the commit sequence, the
/// staging rules or the AI state machine is duplicated here. What this file owns is the four things
/// a view model deliberately does not: which control has focus, what the keyboard does, which rows a
/// right-click is about, and handing the view model its callbacks.
///
/// <b>Escape closes and Enter commits</b>, both per CLAUDE.md, and both with the same exception the
/// WPF window makes: plain Enter is suspended while the diff pane holds focus, because that pane is
/// an editor over the user's working tree where Enter is a newline in their file.
/// <c>Ctrl/Cmd+Enter</c> is not suspended there — it is a chord no editor claims, and CLAUDE.md's
/// keyboard map promises "commit &amp; push from anywhere".
/// </summary>
public sealed partial class CommitWindow : Window
{
    private readonly CommitViewModel _viewModel;
    private readonly DiffPane _diff = new();

    /// <summary>
    /// Set while the file list's selection is being put back after a refused change, so the
    /// SelectionChanged that causes does not ask the same question again.
    /// </summary>
    private bool _restoringSelection;

    /// <summary>
    /// The user's answer to one discard question, alive only for the re-application it authorised.
    ///
    /// This is what <c>ConfirmDiscardEdit</c> reads, and it is a field rather than a dialog call
    /// because that callback is <i>synchronous</i> and Avalonia has no synchronous modal — a window
    /// here shows and returns, it does not block. So the ask happens after the refusal, and the
    /// answer is carried back into a second attempt rather than out of the callback.
    /// </summary>
    private bool _discardOnce;

    /// <summary>Set while a discard question is on screen, so a second click does not open a second one.</summary>
    private bool _askingDiscard;

    /// <summary>Set once the close-with-unsaved-changes question has been answered "discard".</summary>
    private bool _discardOnClose;

    /// <summary>Parameterless for the Avalonia designer, which constructs the type to preview it.</summary>
    public CommitWindow()
    {
        InitializeComponent();

        //Never reached at run time: the host always uses the other constructor. The designer has no
        //container, so it gets a window with no data rather than an exception.
        _viewModel = null!;
    }

    public CommitWindow(CommitViewModel viewModel, IDialogs dialogs)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        //The view model asks its host to put a question on screen, rather than knowing how. The same
        //callback the WPF window supplies, satisfied here by the Avalonia dialogs.
        viewModel.ConfirmAsync = dialogs.ConfirmAsync;

        viewModel.FocusMessageRequested += FocusMessage;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        viewModel.ErrorRaised += OnErrorRaised;
        viewModel.Committed += OnCommitted;

        //The view model refuses to switch files while an edit is unsaved, and these two are how it
        //asks. See _discardOnce for why the answer arrives from a field rather than from a dialog.
        viewModel.IsEditorDirty = () => _diff.IsDirty;
        viewModel.ConfirmDiscardEdit = () => _discardOnce;

        //Every string the window shows comes from the one key = value file per language, per
        //CLAUDE.md. Set here rather than in the XAML for the reason the WPF window sets its own the
        //same way: the table is read at construction, and a literal in the markup is a string no
        //translator can reach.
        BranchLabel.Text = Strings.Get("branch.label");
        FilesHeader.Text = Strings.Get("commit.files.header");
        MessageHeader.Text = Strings.Get("commit.message.header");
        HintText.Text = Strings.Get("commit.hint");
        SelectAllButton.Content = Strings.Get("commit.selectall");
        SelectNoneButton.Content = Strings.Get("commit.selectnone");
        RefreshButton.Content = Strings.Get("commit.button.refresh");
        GenerateButton.Content = Strings.Get("commit.button.generate");
        CommitButton.Content = Strings.Get("commit.button.commit");
        CloseButton.Content = Strings.Get("common.close");
        MessageBox.PlaceholderText = Strings.Get("commit.message.header");

        //AbortMergeButton's label is bound, not set: it names the operation it would throw away, so
        //it reads "Abort rebase…" rather than a word that could mean any of the four.
        ContinueMergeButton.Content = Strings.Get("conflict.continue");

        DiffHost.Content = _diff;

        //Straight through to the view model, which owns the encoding, the patch and the index. The
        //pane decides *which rows* and *what text*; it does not know what a patch is and it never
        //writes a file.
        _diff.HunkStageRequested = OnDiffHunkStageRequestedAsync;
        _diff.SaveRequested = OnDiffSaveRequestedAsync;
        _diff.RestageRequested = OnDiffRestageRequestedAsync;
        _diff.SetTypography(new FontFamily(viewModel.DiffFontFamily), viewModel.DiffFontSize);

        FileList.SelectionChanged += OnFileSelectionChanged;
        FileList.ContextRequested += OnFileListContextRequested;
        FileList.KeyDown += OnFileListKeyDown;
        FileMenu.Opening += OnFileMenuOpening;
        CloseButton.Click += OnCloseClicked;

        UpdateBranchHint();

        //The caret is in the message box from the moment the window is populated -- CLAUDE.md is
        //explicit that this is the point of the whole surface.
        Opened += (_, _) => FocusMessage();
    }

    /// <summary>
    /// Re-points this window at a different repository, without touching Git.
    ///
    /// Split from <see cref="RefreshAsync"/> so the window can be shown between the two: the user
    /// sees the right repository name and an empty list immediately, rather than nothing at all
    /// until four Git processes have answered.
    /// </summary>
    public void Reset(RepositoryInfo repository)
    {
        _diff.Show(null, isLoading: false);
        _viewModel.Reset(repository);
    }

    public async Task RefreshAsync()
    {
        await _viewModel.RefreshAsync().ConfigureAwait(true);

        //After the list exists, so the caret lands in the message box rather than being moved by the
        //selection that arriving files bring with them. This is the window's whole opening gesture.
        FocusMessage();

        //After the status, because the payload is built from the ticked files. Fire-and-forget, and
        //silent when no provider is configured: the AI is an accelerator, never a dependency.
        _viewModel.BeginGeneration(force: false);
    }

    private void FocusMessage()
    {
        MessageBox.Focus();
        MessageBox.CaretIndex = MessageBox.Text?.Length ?? 0;
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnCommitted(CommitFlowResult result)
    {
        //Never over an unsaved edit: this window is the only thing holding it.
        if (_viewModel.CloseAfterCommit && !_diff.IsDirty)
            Close();
    }

    private void OnErrorRaised(string title, string message) => MessageWindow.Notice(title, message);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CommitViewModel.CurrentDiff):
            case nameof(CommitViewModel.IsDiffLoading):
                _diff.Show(
                    _viewModel.CurrentDiff,
                    _viewModel.IsDiffLoading,
                    _viewModel.SelectedFile?.Change.IsStaged ?? false,
                    _viewModel.SelectedFile?.Change.IsUntracked ?? false);

                break;

            case nameof(CommitViewModel.BranchResolution):
            case nameof(CommitViewModel.BranchHint):
                UpdateBranchHint();

                break;
        }
    }

    /// <summary>
    /// Colours the branch hint by what committing would actually do.
    ///
    /// <b>An invalid ref name is the one hint that has to shout</b>: it is the only state where
    /// Commit is disabled, and a muted grey line is not going to explain a button that will not
    /// press. A new branch is merely worth noticing, so it takes the accent.
    ///
    /// In code rather than in the markup because Avalonia's answer to a WPF DataTrigger is a
    /// converter or a style class, either of which would be more machinery than these three lines.
    /// </summary>
    private void UpdateBranchHint()
    {
        BranchIntent intent = _viewModel.BranchResolution.Intent;

        BranchHint.FontWeight = intent == BranchIntent.Invalid ? FontWeight.SemiBold : FontWeight.Normal;

        BranchHint.Foreground = intent switch
        {
            BranchIntent.Invalid => Resource("DangerText"),
            BranchIntent.NewBranch => Resource("Accent"),
            _ => Resource("TextMuted"),
        };
    }

    /// <summary>
    /// Hands the whole selection to the view model. <c>ListBox.SelectedItems</c> is not bindable, so
    /// this is the only way it can be known.
    /// </summary>
    private void OnFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection)
            return;

        FileChangeItem[] wanted = FileList.SelectedItems?.OfType<FileChangeItem>().ToArray() ?? [];

        //The whole selection, not the one row that changed: the file-level commands act on all of
        //it, and the view model recomputes which of them are available from the set.
        if (_viewModel.SetSelectedFiles(wanted))
            return;

        //Refused: the pane is keeping an unsaved edit, so the list goes back to the file it is
        //showing and the user is asked whether to throw the edit away. Re-entrant by construction --
        //setting the selection raises this handler again -- which the flag is for.
        RestoreSelection();

        _ = AskThenReselectAsync(wanted);
    }

    private void RestoreSelection()
    {
        _restoringSelection = true;

        try
        {
            FileList.SelectedItems?.Clear();

            if (_viewModel.CurrentDiff?.Path is { } path
                && _viewModel.Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal))
                    is { } showing)
            {
                FileList.SelectedItem = showing;
            }
        }
        finally
        {
            _restoringSelection = false;
        }
    }

    /// <summary>
    /// Asks whether to discard the unsaved edit, and applies the selection that was refused if the
    /// answer is yes.
    ///
    /// <b>Enter does not accept.</b> The affirmative here is the one that destroys the edit, so the
    /// question opens with "Keep editing" holding the default.
    /// </summary>
    private async Task AskThenReselectAsync(IReadOnlyList<FileChangeItem> wanted)
    {
        if (_askingDiscard || wanted.Count == 0)
            return;

        _askingDiscard = true;

        try
        {
            bool discard = await MessageWindow.AskAsync(
                Strings.Get("edit.discard.title"),
                Strings.Get("edit.discard.body", _viewModel.CurrentDiff?.Path ?? string.Empty),
                Strings.Get("edit.discard.yes"),
                Strings.Get("edit.keepediting"),
                destructive: true).ConfigureAwait(true);

            if (!discard)
                return;

            //The rows may have gone while the question was on screen -- a refresh, or a commit.
            FileChangeItem[] alive = [.. wanted.Where(_viewModel.Files.Contains)];

            if (alive.Length == 0)
                return;

            //Answered once, for this attempt only. The next switch asks again.
            _discardOnce = true;

            try
            {
                _restoringSelection = true;

                try
                {
                    FileList.SelectedItems?.Clear();

                    foreach (FileChangeItem row in alive)
                        FileList.SelectedItems?.Add(row);
                }
                finally
                {
                    _restoringSelection = false;
                }

                _viewModel.SetSelectedFiles(alive);
            }
            finally
            {
                _discardOnce = false;
            }
        }
        finally
        {
            _askingDiscard = false;
        }
    }

    /// <summary>
    /// Settles which rows the menu is about before it is drawn.
    ///
    /// A ListBox does not select on right-click, and without this the menu would silently target
    /// whatever was selected before -- which for a Delete is the wrong file, with the correct path
    /// shown in a confirmation the user is not reading closely. A click inside the selection means
    /// the selection; anywhere else means the row under the pointer.
    ///
    /// A click that missed every row leaves the previous selection alone, and only suppresses the
    /// menu when there was none: right-clicking the empty space below the list with a file already
    /// chosen is a reasonable way to reach that file's menu.
    /// </summary>
    private void OnFileListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (RowUnder(e.Source) is not { } row)
        {
            if (FileList.SelectedItem is null)
                e.Handled = true;

            return;
        }

        if (FileList.SelectedItems?.Contains(row) == true)
            return;

        FileList.SelectedItems?.Clear();
        FileList.SelectedItems?.Add(row);
    }

    /// <summary>The row a pointer event landed on, or null when it missed every one of them.</summary>
    private static FileChangeItem? RowUnder(object? source)
    {
        for (Visual? visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is ListBoxItem { DataContext: FileChangeItem row })
                return row;
        }

        return null;
    }

    /// <summary>
    /// Labels every item with how many rows it would actually touch, and hides the three conflict
    /// items when there is no conflict to act on.
    ///
    /// <b>The labels count what each item would touch</b>, not what is highlighted -- so a five-row
    /// selection holding one untracked file offers "Revert 4 files…". A menu saying five and
    /// reverting four would be the count the user checked the dialog against.
    /// </summary>
    private void OnFileMenuOpening(object? sender, CancelEventArgs e)
    {
        EditFileMenuItem.Header = Label("edit", _viewModel.EditableCount);
        AddFileMenuItem.Header = Label("add", _viewModel.AddableCount);
        RevertFileMenuItem.Header = Label("revert", _viewModel.RevertableCount);

        //Two spellings of one item, chosen by what the click would actually do. A row Git has
        //something for is taken out of the index and the file stays; only an untracked one is
        //deleted -- and a menu is the last place to find out which of those just happened.
        DeleteFileMenuItem.Header = Label(
            _viewModel.DeleteBinsOnly ? "delete" : "delete.untrack",
            _viewModel.DeletableCount);

        TakeOursMenuItem.Header = Label("conflict.ours", _viewModel.ResolvableOursCount);
        TakeTheirsMenuItem.Header = Label("conflict.theirs", _viewModel.ResolvableTheirsCount);
        MarkResolvedMenuItem.Header = Label("conflict.resolve", _viewModel.ConflictedCount);

        //Hidden rather than disabled, which is the opposite of what the four items above do -- and
        //the difference is that those four are always meaningful and these three are meaningless most
        //of the time. A permanent trio of dead items at the top of the menu every user sees every day
        //is a worse trade than a menu that grows when there is a conflict to act on.
        //
        //Use ours and Use theirs go individually: a conflict where one side deleted the path has no
        //version to take on that side, so the item would name a command Git refuses.
        TakeOursMenuItem.IsVisible = _viewModel.ResolvableOursCount > 0;
        TakeTheirsMenuItem.IsVisible = _viewModel.ResolvableTheirsCount > 0;
        MarkResolvedMenuItem.IsVisible = _viewModel.ConflictedCount > 0;
        ConflictSeparator.IsVisible = _viewModel.ConflictedCount > 0;

        //Zero keeps the singular wording: the item is disabled, and "Revert 0 files…" says less than
        //the label the user already knows.
        static string Label(string feature, int count) =>
            count > 1 ? Strings.Get(feature + ".menu.many", count) : Strings.Get(feature + ".menu");
    }

    /// <summary>
    /// <b>Del takes the highlighted rows out of Git and leaves their files alone</b>, through the
    /// very command the context menu's item reaches -- so the two cannot come to mean different
    /// things. <b>Except an untracked row</b>, which Git has nothing to remove from and which goes to
    /// the Trash instead.
    ///
    /// <b>On the list rather than on the window</b>, because everywhere else in this window Del
    /// belongs to text: it is a character in the message box and a character in the diff pane's
    /// editor over the user's file.
    ///
    /// <b>Only the bare key.</b> Shift+Del means "skip the bin" in a file manager, and this window
    /// has nothing that does that.
    /// </summary>
    private void OnFileListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || e.KeyModifiers != KeyModifiers.None)
            return;

        //Asked rather than assumed, and asked of the command: the "not while busy" rule and the
        //filtering of rows whose file is already gone live there, and a second copy of either here is
        //how the key and the menu would come to disagree.
        if (_viewModel.DeleteFileCommand.CanExecute(null))
            _viewModel.DeleteFileCommand.Execute(null);

        //Handled either way. Del over the file list means this and nothing else, including when there
        //is nothing here it can act on.
        e.Handled = true;
    }

    private async Task OnDiffHunkStageRequestedAsync(IReadOnlySet<int> rows, bool unstage)
    {
        try
        {
            if (await _viewModel.StageHunkAsync(rows, unstage).ConfigureAwait(true) is { } refusal)
            {
                MessageWindow.Notice(Strings.Get("hunk.failed"), refusal);

                return;
            }

            //Not a re-show: `apply --cached` touches the index and not the working tree, so the
            //document is unchanged and rebuilding it would move the caret off the hunk the user is
            //working through.
            _diff.MarkIndexChanged(_viewModel.SelectedFile?.Change.IsStaged ?? false);
        }
        catch (Exception ex)
        {
            MessageWindow.Notice(Strings.Get("hunk.failed"), ex.Message);
        }
    }

    /// <summary>
    /// Writes the edited file, through the view model so the encoding, the BOM and the line endings
    /// are put back the way they were read.
    ///
    /// <c>force: false</c> first, so an external modification since load is reported rather than
    /// overwritten -- and then the three-way choice for it, whose default is the one that destroys
    /// nothing: overwriting loses what is on disk and reloading loses what is in the editor, so
    /// neither may be what Esc picks.
    /// </summary>
    private async Task OnDiffSaveRequestedAsync(string text)
    {
        SaveOutcome outcome = await _viewModel.SaveCurrentFileAsync(text, force: false).ConfigureAwait(true);

        if (outcome.Succeeded)
        {
            if (_viewModel.CurrentDiff is { } refreshed)
                _diff.MarkSaved(refreshed);

            return;
        }

        if (outcome.Refusal != SaveRefusal.ExternallyModified)
        {
            MessageWindow.Notice(Strings.Get("edit.save"), outcome.Message ?? string.Empty);

            return;
        }

        MessageChoice choice = await MessageWindow.AskAsync(
            Strings.Get("edit.external.title"),
            outcome.Message ?? string.Empty,
            Strings.Get("edit.external.overwrite"),
            Strings.Get("edit.external.reload"),
            Strings.Get("edit.keepediting"),
            destructive: true).ConfigureAwait(true);

        //Nothing happened: the file on disk and the editor both stand, and the save the user asked
        //for simply did not run. They are back where they were, with the edit still unsaved.
        if (choice == MessageChoice.Cancelled)
            return;

        if (choice == MessageChoice.Yes)
        {
            SaveOutcome forced = await _viewModel.SaveCurrentFileAsync(text, force: true).ConfigureAwait(true);

            if (forced.Succeeded && _viewModel.CurrentDiff is { } saved)
                _diff.MarkSaved(saved);
            else if (!forced.Succeeded)
                MessageWindow.Notice(Strings.Get("edit.save"), forced.Message ?? string.Empty);

            return;
        }

        //Reload: the editor's contents are discarded in favour of what is on disk. The user asked for
        //that explicitly, which is the only way it may ever happen.
        SideBySideDiff? reloaded = await _viewModel.ReloadCurrentFileAsync().ConfigureAwait(true);

        _diff.Show(reloaded, isLoading: false, _viewModel.SelectedFile?.Change.IsStaged ?? false);
    }

    private async Task OnDiffRestageRequestedAsync()
    {
        try
        {
            await _viewModel.RestageCurrentFileAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageWindow.Notice(Strings.Get("edit.restage"), ex.Message);
        }
    }

    /// <summary>
    /// Whether the focused control is inside the diff pane.
    ///
    /// <b>Plain Enter is suspended there, per CLAUDE.md</b>: that pane is an editor over the user's
    /// working tree, where Enter is a newline in their file rather than a commit.
    /// </summary>
    private bool IsInsideDiff(IInputElement? focused)
    {
        for (Visual? visual = focused as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (ReferenceEquals(visual, _diff))
                return true;
        }

        return false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        //Ctrl/Cmd+S saves the edited file. Explicit, never automatic -- CLAUDE.md is unconditional
        //about it, and this is the only keystroke in the window that writes to the working tree.
        if (e.Key == Key.S && command)
        {
            _diff.RequestSave();
            e.Handled = true;

            return;
        }

        //F5 re-reads the status. Through the view model's own command, so it obeys the same "not
        //while busy" rule instead of stacking refreshes on a slow repository.
        if (e.Key == Key.F5)
        {
            if (_viewModel.RefreshCommand.CanExecute(null))
                _viewModel.RefreshCommand.Execute(null);

            e.Handled = true;

            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                //The find bar first, per CLAUDE.md: "esc close the search bar if open, otherwise the
                //window". CloseSearch reports whether it had anything to close.
                if (_diff.CloseSearch())
                {
                    e.Handled = true;

                    break;
                }

                //The view model refuses while a commit is actually executing: that window has to stay
                //to report the outcome.
                if (_viewModel.EscapePressed())
                    Close();

                e.Handled = true;

                break;

            case Key.Enter or Key.Return:
            {
                //Checked before the editor gate below, precisely because that gate exists to protect
                //*plain* Enter. Ctrl/Cmd+Enter is a chord no editor claims.
                if (command)
                {
                    _viewModel.EnterPressed(push: true);
                    e.Handled = true;

                    break;
                }

                //The editor owns the rest of its keyboard.
                if (IsInsideDiff(FocusManager?.GetFocusedElement()))
                    break;

                //Shift+Enter is a newline in the message body, which is the one thing Enter can no
                //longer do.
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    break;

                _viewModel.EnterPressed(push: true);
                e.Handled = true;

                break;
            }
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// An unsaved edit blocks the close. Losing a working-tree edit to a stray Esc is exactly the
    /// kind of silent data loss this product must not have.
    ///
    /// The close is cancelled and the question opened behind it, rather than asked inline: Avalonia
    /// has no synchronous modal, so there is no way to hold the close open while the user reads.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_diff.IsDirty && !_discardOnClose)
        {
            e.Cancel = true;

            _ = AskAboutUnsavedAsync();

            return;
        }

        base.OnClosing(e);
    }

    private async Task AskAboutUnsavedAsync()
    {
        if (_askingDiscard)
            return;

        _askingDiscard = true;

        try
        {
            MessageChoice choice = await MessageWindow.AskAsync(
                Strings.Get("edit.save"),
                Strings.Get("edit.close.dirty", _viewModel.CurrentDiff?.Path ?? string.Empty),
                Strings.Get("edit.close.save"),
                Strings.Get("edit.close.discard"),
                Strings.Get("edit.keepediting"),
                destructive: true).ConfigureAwait(true);

            switch (choice)
            {
                case MessageChoice.Yes:
                    //Saved, and the window stays: the save is asynchronous and its outcome -- an
                    //external modification, a locked file -- is something the user has to see.
                    _diff.RequestSave();

                    break;

                case MessageChoice.No:
                    _discardOnClose = true;
                    Close();

                    break;

                default:
                    //Keep editing. The close is simply abandoned.
                    break;
            }
        }
        finally
        {
            _askingDiscard = false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        //The view model is a singleton and outlives this window, so every subscription made in the
        //constructor is undone here. A second commit window that subscribed twice would show two
        //error dialogs per failure and close itself twice per commit.
        _viewModel.FocusMessageRequested -= FocusMessage;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.ErrorRaised -= OnErrorRaised;
        _viewModel.Committed -= OnCommitted;

        _viewModel.IsEditorDirty = null;
        _viewModel.ConfirmDiscardEdit = null;

        //Cancels any in-flight diff or generation. Without this, work started for a file clicked just
        //before closing keeps a git.exe alive and completes into a window that no longer exists.
        _viewModel.Cancel();

        base.OnClosed(e);
    }

    private static IBrush? Resource(string key) => Application.Current?.FindResource(key) as IBrush;
}
