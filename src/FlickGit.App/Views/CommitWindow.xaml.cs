using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.App.ViewModels;
using FlickGit.Commits;
using FlickGit.Diff;
using FlickGit.Logging;

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
    /// Set while the file list's selection is being put back after a refused change, so the
    /// SelectionChanged that causes does not ask the same question again.
    /// </summary>
    private bool _restoringSelection;

    /// <summary>
    /// True when the resident service owns this window: closing hides it so the next right-click
    /// reuses it. False for a one-shot launch, where closing must really close so the process can exit.
    /// </summary>
    public bool KeepAlive { get; init; }

    /// <summary>
    /// Handed to the console pane, which is constructed by XAML and so cannot be injected. The same
    /// route KeepAlive takes, for the same reason.
    /// </summary>
    public ILog? Log { get; init; }

    /// <summary>
    /// Whether the file list, the diff and the message are folded away so the console has the window.
    /// Not persisted, like the splitter positions: the state lasts the session.
    /// </summary>
    private bool _consoleMaximised;

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

        //AbortMergeButton's label is bound, not set: it names the operation it would throw away, so
        //it reads "Abort rebase..." rather than a word that could mean any of the four.
        ContinueMergeButton.Content = Strings.Get("conflict.continue");
        HintText.Text = Strings.Get("commit.hint");
        CloseButton.Content = Strings.Get("common.close");

        //A tooltip rather than another item in the footer hint: the chevron is the discoverable
        //affordance, and the hint line is already at the width it can carry.
        ConsoleToggleButton.ToolTip = Strings.Get("console.toggle");
        ConsoleMaximiseButton.ToolTip = Strings.Get("console.maximise");

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

        //Ctrl+` opens the console and puts the caret in it. The way *out* cannot be a KeyBinding: while
        //the console has focus its window procedure receives the keyboard and WPF is sent nothing at
        //all, so the pane claims the same gesture as a real hotkey for exactly as long as it holds
        //focus. One gesture, two mechanisms, because the boundary genuinely has two sides.
        InputBindings.Add(new KeyBinding
        {
            Key = Key.Oem3,
            Modifiers = ModifierKeys.Control,
            Command = new Infrastructure.RelayCommand(() => _ = OpenOrFocusConsoleAsync()),
        });

        Console.EscapeRequested += FocusMessage;

        //Whether the console holds the keyboard has to be an *observation*, not something this window
        //remembers. Ctrl+` is one gesture over two mechanisms -- a KeyBinding going in, a RegisterHotKey
        //coming out -- and only one of them is armed at a time. So a state that still says "the console
        //has focus" after the caret has come back to WPF arms the wrong half: Ctrl+` fires the hotkey and
        //takes the user *out* of a console they are not in, which looks like the key doing nothing at all
        //and needs a second press to get back in.
        //
        //Every route out other than the hotkey lands here -- a click in the file list, the diff or the
        //message box, Tab, a collapsed element handing focus on, an activation that restores WPF's own
        //focus. WPF raises this only when an element of this window really has the Win32 focus, which is
        //exactly the moment the console does not, so the pane can stop guessing.
        GotKeyboardFocus += (_, _) => Console.ReleaseFocus();
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
            _viewModel.ConfirmDiscardEdit = null;
        }

        _viewModel = e.NewValue as CommitViewModel;

        if (_viewModel is null)
            return;

        _viewModel.Committed += OnCommitted;
        _viewModel.ErrorRaised += OnErrorRaised;
        _viewModel.FocusMessageRequested += FocusMessage;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        //The view model decides *when* to ask; the window owns the only thing that can actually ask.
        //
        //Onto the dispatcher, and that is not a formality. CommitFlow raises its guardrail questions
        //from wherever its own ConfigureAwait(false) left it, which is a thread-pool thread from the
        //first git.exe call onwards -- so a `switch -c` followed by `push -u` asks "create an
        //upstream?" off the UI thread. Constructing a Window there throws "the calling thread must be
        //STA", and the Task.FromResult around a synchronous body is exactly what hid it: an async
        //signature moves no work anywhere. ShowDialog pumps its own message loop, so the flow waits
        //on the dialog rather than the dialog blocking the pump.
        _viewModel.ConfirmAsync = (title, question, yes, no, defaultIsAffirmative) =>
            Dispatcher.InvokeAsync(
                () => ConfirmWindow.Ask(this, title, question, yes, no, defaultIsAffirmative)).Task;

        //Asked only by the revert confirmation, so it can say that an unsaved edit is not what goes to
        //the Recycle Bin.
        _viewModel.IsEditorDirty = () => Diff.IsDirty;

        //Enter does not accept: the affirmative answer here is the one that destroys the edit.
        _viewModel.ConfirmDiscardEdit = () => ConfirmWindow.Ask(
            this,
            Strings.Get("edit.discard.title"),
            Strings.Get("edit.discard.body", _viewModel.CurrentDiff?.Path ?? string.Empty),
            Strings.Get("edit.discard.yes"),
            Strings.Get("edit.keepediting"),
            defaultIsAffirmative: false);

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

        //A shell still rooted at the previous repository is leaked state of the most literal kind, and
        //on Windows an open directory handle there is enough to make a later switch or branch delete
        //fail for a reason the user cannot see.
        Console.Stop();
        CollapseConsole();

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
    /// Hands the whole selection to the view model. <c>ListBox.SelectedItems</c> is not bindable, so
    /// this is the only way it can be known -- the log window's list does the same.
    /// </summary>
    private void OnFileListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection || _viewModel is null)
            return;

        if (_viewModel.SetSelectedFiles([.. FileList.SelectedItems.OfType<FileChangeItem>()]))
            return;

        //Refused: the pane is keeping an unsaved edit, so the list goes back to the file it is showing.
        //Re-entrant by construction -- setting the selection raises this handler again -- which the flag
        //is for.
        _restoringSelection = true;

        try
        {
            FileList.SelectedItems.Clear();

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
    /// Settles which rows the menu is about, then labels it with how many.
    ///
    /// A ListBox does not select on right-click, and without this the menu would silently target
    /// whatever was selected before -- which for a Delete is the wrong file, with the correct path
    /// shown in a confirmation the user is not reading closely. Under Extended selection the rule
    /// gains a second half, which <see cref="FilterList.SelectRowUnderPointer"/> owns: a click inside
    /// the selection means the selection, anywhere else means the row under the pointer.
    ///
    /// A click that missed every row leaves the previous selection alone, and only suppresses the
    /// menu when there was none: right-clicking the empty space below the list with a file already
    /// chosen is a reasonable way to reach that file's menu.
    ///
    /// <b>The labels count what each item would actually touch</b>, not what is highlighted -- so a
    /// five-row selection holding one untracked file offers "Revert 4 files...". A menu saying five
    /// and reverting four would be the count the user checked the dialog against.
    /// </summary>
    private void OnFileListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (!FilterList.SelectRowUnderPointer(FileList, e.OriginalSource) && FileList.SelectedItem is null)
        {
            e.Handled = true;
            return;
        }

        if (_viewModel is null)
            return;

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

        //Hidden rather than disabled, which is the opposite of what the four items above do — and the
        //difference is that those four are always meaningful and these three are meaningless most of
        //the time. A permanent trio of dead items at the top of the menu every user sees every day is
        //a worse trade than a menu that grows when there is a conflict to act on.
        //
        //Use ours and Use theirs go individually: a conflict where one side deleted the path has no
        //version to take on that side, so the item would name a command Git refuses.
        Show(TakeOursMenuItem, _viewModel.ResolvableOursCount > 0);
        Show(TakeTheirsMenuItem, _viewModel.ResolvableTheirsCount > 0);
        Show(MarkResolvedMenuItem, _viewModel.ConflictedCount > 0);
        Show(ConflictSeparator, _viewModel.ConflictedCount > 0);

        static void Show(UIElement item, bool visible) =>
            item.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        //Zero keeps the singular wording: the item is disabled, and "Revert 0 files..." says less than
        //the label the user already knows.
        static string Label(string feature, int count) =>
            count > 1 ? Strings.Get(feature + ".menu.many", count) : Strings.Get(feature + ".menu");
    }

    /// <summary>
    /// <b>Del takes the highlighted rows out of Git and leaves their files alone</b>, through the very
    /// command the context menu's item reaches — so the two cannot come to mean different things.
    ///
    /// <b>Except an untracked row</b>, which Git has nothing to remove from and which goes to the
    /// Recycle Bin instead. Neither half asks: the Git side destroys nothing, and the bin is the way
    /// back from the other.
    ///
    /// <b>On the list rather than on the window</b>, because everywhere else in this window Del belongs
    /// to text: it is a character in the message box and a character in the diff pane's editor over the
    /// user's file. <c>KeyDown</c> rather than a preview, so a row's tick box keeps its own keyboard and
    /// the key still arrives here by bubbling.
    ///
    /// <b>Only the bare key.</b> Shift+Del means "skip the Recycle Bin" everywhere in Explorer, and
    /// this window has nothing that does that — so it falls through to nothing rather than quietly
    /// meaning the recoverable thing, which for the untracked half is the only thing it means.
    /// </summary>
    private void OnFileListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Keyboard.Modifiers != ModifierKeys.None || _viewModel is null)
            return;

        //Asked rather than assumed, and asked of the command: the "not while busy" rule and the
        //filtering of rows whose file is already gone live there, and a second copy of either here is
        //how the key and the menu would come to disagree.
        if (_viewModel.DeleteFileCommand.CanExecute(null))
            _viewModel.DeleteFileCommand.Execute(null);

        //Handled either way. Del over the file list means this and nothing else, including when there is
        //nothing here it can act on.
        e.Handled = true;
    }

    /// <summary>
    /// Puts the caret in the message box, at the end of whatever is already there. Called on open, and
    /// again whenever the view model says the caret belongs back here.
    /// </summary>
    private void FocusMessage()
    {
        //Not while the user is typing in the console. The caret is in another process's window, and
        //taking it back mid-command would lose whatever they had half-written.
        if (Console.IsConsoleFocused)
            return;

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
            //The search bar owns Esc while it is open, and it has to be asked here: this window
            //intercepts the key before the pane ever sees it, so the pane cannot dismiss its own bar
            //the way it does in the log window. Throwing away a half-typed message because the user
            //tried to close a search box is the worse of the two outcomes.
            if (Diff.CloseSearch())
            {
                e.Handled = true;
                return;
            }

            if (_viewModel.EscapePressed())
                Close();

            e.Handled = true;
            return;
        }

        //Ctrl+Enter commits from anywhere, and "anywhere" has to include the diff pane -- which is
        //where the user spends the time that makes a modified gesture worth having. It is checked
        //before the editor gate below precisely because that gate exists to protect *plain* Enter:
        //Enter in the editor is a newline in the user's file, while Ctrl+Enter is a chord no editor
        //claims. CLAUDE.md's keyboard map promises "Ctrl+⏎ commit & push from anywhere".
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _viewModel.EnterPressed(push: true);
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

            case Key.Enter when Keyboard.Modifiers == ModifierKeys.None:
                //Plain Enter, from the message box and the file list. The Ctrl form is handled above,
                //ahead of the editor gate, so it is deliberately not spelled again here.
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
        //non-destructive one: overwriting loses what is on disk and reloading loses what is in the
        //editor, so neither may be what Esc picks.
        ConfirmChoice choice = ConfirmWindow.AskWithCancel(
            this,
            Strings.Get("edit.external.title"),
            outcome.Message ?? string.Empty,
            Strings.Get("edit.external.overwrite"),
            Strings.Get("edit.external.reload"),
            Strings.Get("edit.keepediting"));

        //Nothing happened: the file on disk and the editor both stand, and the save the user asked for
        //simply did not run. They are back where they were, with the edit still unsaved.
        if (choice == ConfirmChoice.Cancelled)
            return;

        if (choice == ConfirmChoice.Yes)
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

    private void OnCommitted(CommitFlowResult result)
    {
        if (_viewModel?.CloseAfterCommit == true && !Diff.IsDirty)
            Close();
    }

    private void OnErrorRaised(string title, string message) => Notice.Show(this, title, message);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private async void OnToggleConsole(object sender, RoutedEventArgs e)
    {
        if (IsConsoleOpen)
            CollapseConsole();
        else
            await OpenConsoleAsync().ConfigureAwait(true);
    }

    private bool IsConsoleOpen => ConsoleRow.Height.Value > 0;

    /// <summary>Ctrl+`: opens the pane if it is shut, and otherwise just goes there.</summary>
    private async Task OpenOrFocusConsoleAsync()
    {
        if (IsConsoleOpen)
        {
            Console.FocusConsole();
            return;
        }

        await OpenConsoleAsync().ConfigureAwait(true);
    }

    private async Task OpenConsoleAsync()
    {
        if (_viewModel?.Repository is not { } repository)
            return;

        ConsoleSplitterRow.Height = new GridLength(4);
        //A star rather than a fixed strip, so opening the console splits the flexible space with the
        //file list and the diff instead of carving a fixed 220 px out of them. The splitter below the
        //message can only trade against that message row, which is 28 px of travel -- so a generous
        //default and the maximise button are what actually make the pane a usable size.
        ConsoleRow.Height = new GridLength(1, GridUnitType.Star);
        ConsoleRow.MinHeight = 80;
        ConsoleSplitter.Visibility = Visibility.Visible;
        Console.Visibility = Visibility.Visible;
        ConsoleToggleButton.Content = "\u2304";
        ConsoleMaximiseButton.Visibility = Visibility.Visible;

        Console.Log ??= Log;

        //The pane's container window has to exist and have a size before a console can be parented
        //into it, and it was Collapsed until the line above.
        UpdateLayout();

        await Console.StartAsync(repository.Root).ConfigureAwait(true);
        Console.FocusConsole();
    }

    /// <summary>
    /// Hides the pane without ending the session -- collapsing is not closing, and a build running in
    /// there should survive being folded away.
    ///
    /// By height, never by Visibility: a hosted HWND is destroyed when its element unloads, which would
    /// leave the console parented to a window that no longer exists.
    /// </summary>
    private void CollapseConsole()
    {
        //Restored first. Folding the console away while the commit area is still hidden would leave a
        //window with a header, a footer and nothing in between.
        if (_consoleMaximised)
            RestoreCommitArea();

        ConsoleMaximiseButton.Visibility = Visibility.Collapsed;
        ConsoleSplitterRow.Height = new GridLength(0);
        ConsoleRow.Height = new GridLength(0);
        ConsoleRow.MinHeight = 0;
        ConsoleSplitter.Visibility = Visibility.Collapsed;
        ConsoleToggleButton.Content = "^";

        Console.ReleaseFocus();
    }

    private void OnToggleConsoleMaximised(object sender, RoutedEventArgs e)
    {
        if (_consoleMaximised)
            RestoreCommitArea();
        else
            HideCommitArea();

        //The console changed size by a lot, and the debounce only runs off SizeChanged, which a row
        //going to zero does not always raise on the pane itself.
        Console.ResizeNow();
        Console.FocusConsole();
    }

    /// <summary>
    /// Folds the file list, the diff and the message away, leaving the header, the console and the
    /// footer.
    ///
    /// The footer stays because the commit buttons are in it: this is a bigger console, not a different
    /// window, and it must not become a state the user has to undo before they can commit. The header
    /// stays because it names the repository and the branch the shell is sitting in.
    /// </summary>
    private void HideCommitArea()
    {
        FilesRow.MinHeight = 0;
        FilesRow.Height = new GridLength(0);
        MessageSplitterRow.Height = new GridLength(0);
        MessageSplitter.Visibility = Visibility.Collapsed;
        MessageRow.MinHeight = 0;
        MessageRow.Height = new GridLength(0);

        //Nothing above it left to drag against.
        ConsoleSplitterRow.Height = new GridLength(0);
        ConsoleSplitter.Visibility = Visibility.Collapsed;

        ConsoleMaximiseButton.Content = "\u25A3";
        ConsoleMaximiseButton.ToolTip = Strings.Get("console.restore");
        _consoleMaximised = true;
    }

    private void RestoreCommitArea()
    {
        FilesRow.Height = new GridLength(1, GridUnitType.Star);
        FilesRow.MinHeight = 160;
        MessageSplitterRow.Height = new GridLength(4);
        MessageSplitter.Visibility = Visibility.Visible;
        MessageRow.Height = new GridLength(104);
        MessageRow.MinHeight = 76;

        ConsoleSplitterRow.Height = new GridLength(4);
        ConsoleSplitter.Visibility = Visibility.Visible;

        ConsoleMaximiseButton.Content = "\u25A1";
        ConsoleMaximiseButton.ToolTip = Strings.Get("console.maximise");
        _consoleMaximised = false;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        Console.SuspendEscape();
        base.OnDeactivated(e);
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Console.ResumeEscape();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        //An unsaved edit blocks the close, whether it is a real close or a hide. Losing a working-tree
        //edit to a stray Esc is exactly the kind of silent data loss this product must not have.
        if (Diff.IsDirty)
        {
            ConfirmChoice choice = ConfirmWindow.AskWithCancel(
                this,
                Strings.Get("edit.save"),
                Strings.Get("edit.close.dirty", _viewModel?.CurrentDiff?.Path ?? string.Empty),
                Strings.Get("edit.close.save"),
                Strings.Get("edit.close.discard"),
                Strings.Get("edit.keepediting"));

            if (choice == ConfirmChoice.Yes)
            {
                e.Cancel = true;
                Diff.RequestSave();
                return;
            }

            //Keep editing: the close itself is abandoned, which is the answer Esc now gives. Without
            //this the escape key out of a confirmation would discard the edit it was asking about.
            if (choice == ConfirmChoice.Cancelled)
            {
                e.Cancel = true;
                return;
            }
        }

        //Before the branch, so it covers the resident hide and the one-shot close alike. The job object
        //behind this is what makes it hold even when nothing here runs -- the MSI force-kills FlickGit
        //on every upgrade.
        Console.Stop();

        if (KeepAlive)
        {
            //Kept for the next request. Hiding rather than closing is what makes the second window open in
            //tens of milliseconds instead of hundreds.
            //
            //Cancelled on the way out, because this branch returns before base.OnClosing and OnClosed --
            //the only other caller of Cancel -- therefore never runs on the resident window, which is
            //every window the hotkey and the context menu open. Without it Esc during generation leaves
            //the HTTP request and any in-flight `git show` running into a window nobody can see.
            _viewModel?.Cancel();

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
