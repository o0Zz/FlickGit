using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia;
using Avalonia.Media;
using Avalonia.VisualTree;
using FlickGit.App.CommandLine;
using FlickGit.App.Localization;
using FlickGit.App.ViewModels;
using FlickGit.Diff;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// The commit window on Avalonia.
///
/// <b>The view model is the Windows one, unchanged.</b> <see cref="CommitViewModel"/> is 2,032 lines
/// in FlickGit.App.Common and already drives the WPF window; nothing about the commit sequence,
/// the staging rules or the AI state machine is duplicated here. What this file owns is the three
/// things a view model deliberately does not: which control has focus, what the keyboard does, and
/// handing the view model its two callbacks.
///
/// <b>Escape closes and Enter commits</b>, both per CLAUDE.md, and both with the same exception the
/// WPF window makes: Enter is suspended while the diff pane holds focus, because that pane is an
/// editor over the user's working tree where Enter is a newline in their file. There is no diff pane
/// here yet, so that check has nothing to consult — when the AvaloniaEdit pass lands it has to be
/// added, and this comment is the reminder.
/// </summary>
public sealed partial class CommitWindow : Window
{
    private readonly CommitViewModel _viewModel;
    private readonly DiffPane _diff = new();

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

        //The view model refuses to switch files while an edit is unsaved, and this is how it asks.
        viewModel.IsEditorDirty = () => _diff.IsDirty;

        //ConfirmDiscardEdit is deliberately left unset. The view model reads `!= true`, so an unset
        //callback *refuses* the switch — the conservative answer, and the right one until there is a
        //dialog to ask with: the user keeps their edit and is simply asked to save it first. Wiring a
        //callback that answered "yes, discard" without asking is how an edit disappears.

        //Every string the window shows comes from the one key = value file per language, per
        //CLAUDE.md. Set here rather than in the XAML for the reason the WPF window sets its own the
        //same way: the table is read at construction, and a literal in the markup is a string no
        //translator can reach.
        SelectAllButton.Content = Strings.Get("commit.selectall");
        SelectNoneButton.Content = Strings.Get("commit.selectnone");
        GenerateButton.Content = Strings.Get("commit.button.generate");
        CommitButton.Content = Strings.Get("commit.button.commit");
        CloseButton.Content = Strings.Get("common.close");
        MessageBox.PlaceholderText = Strings.Get("commit.message.header");

        DiffHost.Content = _diff;

        //Straight through to the view model, which builds the patch in FlickGit.Core and applies it
        //to the index. The pane decides *which rows*; it does not know what a patch is.
        _diff.StageRequested = viewModel.StageHunkAsync;
        _diff.SetTypography(new FontFamily(viewModel.DiffFontFamily), viewModel.DiffFontSize);

        //The view model computes the diff off the selection and publishes it as a property; the pane
        //renders whatever lands. Nothing here decides *what* to show.
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        FileList.SelectionChanged += OnFileSelectionChanged;
        CloseButton.Click += OnCloseClicked;

        //The caret is in the message box from the moment the window is populated -- CLAUDE.md is
        //explicit that this is the point of the whole surface.
        Opened += (_, _) => FocusMessage();
    }

    private void FocusMessage()
    {
        MessageBox.Focus();
        MessageBox.CaretIndex = MessageBox.Text?.Length ?? 0;
    }

    private void OnFileSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        //The whole selection, not the one row that changed: the file-level commands act on all of
        //it, and the view model recomputes which of them are available from the set.
        _viewModel.SetSelectedFiles(FileList.SelectedItems?.OfType<FileChangeItem>().ToArray() ?? []);

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Writes the edited file, through the view model so the encoding, the BOM and the line endings
    /// are put back the way they were read.
    ///
    /// <c>force: false</c>, so an external modification since load is reported rather than
    /// overwritten. The three-way choice the WPF window offers for that — reload, overwrite, save a
    /// copy — needs a dialog this window does not have yet, so for now the refusal is shown and the
    /// user's edit stays in the editor where they can still get at it.
    /// </summary>
    private async Task SaveEditedFileAsync()
    {
        if (!_diff.IsDirty)
            return;

        SaveOutcome outcome = await _viewModel
            .SaveCurrentFileAsync(_diff.FileText(), force: false)
            .ConfigureAwait(true);

        if (outcome.Succeeded)
            _diff.MarkSaved();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CommitViewModel.CurrentDiff))
            _diff.Show(_viewModel.CurrentDiff);
    }

    /// <summary>
    /// Whether the focused control is inside the diff pane.
    ///
    /// <b>Enter is suspended there, per CLAUDE.md</b>, and the reason survives the pane being
    /// read-only today: that pane is an editor over the user's working tree, where Enter is a
    /// newline in their file rather than a commit. Wiring the check now rather than when the pane
    /// becomes editable means the rule cannot be forgotten at the moment it starts to matter.
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
        //Ctrl/Cmd+S saves the edited file. Explicit, never automatic -- CLAUDE.md is unconditional
        //about it, and this is the only keystroke in the window that writes to the working tree.
        if (e.Key == Key.S && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            _ = SaveEditedFileAsync();
            e.Handled = true;

            return;
        }

        switch (e.Key)
        {
            case Key.Escape:
                //The view model refuses while a commit is actually executing: that window has to stay
                //to report the outcome.
                if (_viewModel.EscapePressed())
                {
                    Close();
                    e.Handled = true;
                }

                break;

            case Key.Enter or Key.Return:
            {
                //Shift+Enter is a newline, which is what the message box would do anyway, so it is
                //left alone. Ctrl/Cmd+Enter commits from anywhere in the window.
                bool newline = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                bool anywhere = e.KeyModifiers.HasFlag(KeyModifiers.Control)
                                || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

                if (!newline
                    && !anywhere
                    && !ReferenceEquals(FocusManager?.GetFocusedElement(), MessageBox))
                {
                    //Focus is somewhere neither committing nor typing a message. Leave it alone.
                    break;
                }

                //Ctrl/Cmd+Enter commits from anywhere -- except the diff pane, which is the one place
                //Enter belongs to the user's file rather than to this window.
                if (!newline && !IsInsideDiff(FocusManager?.GetFocusedElement()))
                {
                    _viewModel.EnterPressed(push: true);
                    e.Handled = true;
                }

                break;
            }
        }

        base.OnKeyDown(e);
    }
}
