using Avalonia.Controls;
using Avalonia.Input;
using FlickGit.App.ViewModels;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// The repository palette.
///
/// The view model is the Windows one — <see cref="PaletteViewModel"/> in FlickGit.App.Common — so
/// the fuzzy matching, the scoring, the action mode and the "repositories that have something to do"
/// rule are shared rather than reimplemented. This file owns the keyboard and nothing else.
///
/// <b>Every key is handled here rather than as bindings</b>, because most of them mean something
/// different in a palette than they do anywhere else: Tab completes rather than moving focus, and
/// Backspace at the start of the query leaves action mode rather than deleting a character there is
/// none of.
/// </summary>
public sealed partial class PaletteWindow : Window
{
    private readonly PaletteViewModel _viewModel;

    /// <summary>Parameterless for the Avalonia designer.</summary>
    public PaletteWindow()
    {
        InitializeComponent();
        _viewModel = null!;
    }

    public PaletteWindow(PaletteViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.CloseRequested += Close;

        //The caret is in the query box from the moment it opens: the palette is a thing you type at.
        Opened += (_, _) => QueryBox.Focus();

        //Closing on deactivation is right here and wrong for the commit window: a launcher the user
        //has clicked away from has been dismissed, whereas a commit message being typed must survive
        //an accidental click outside.
        Deactivated += (_, _) => Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                //CLAUDE.md: "Esc closes with no side effects."
                e.Handled = true;
                _viewModel.Cancel();

                return;

            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Control)
                                || e.KeyModifiers.HasFlag(KeyModifiers.Meta):
                e.Handled = true;
                _viewModel.PullAllBehind();

                return;

            case Key.Enter:
            case Key.Tab:
                //Tab completes rather than moving focus: there is only one control worth focusing,
                //and in a palette Tab means "take the highlighted row".
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

            case Key.Back when QueryBox.CaretIndex == 0 || QueryBox.Text?.Length == 0:
                //At the start of the action text, Backspace means "back to the repository list"
                //rather than deleting a character there is none of.
                if (_viewModel.LeaveActionModeIfEmpty())
                {
                    e.Handled = true;
                    QueryBox.CaretIndex = QueryBox.Text?.Length ?? 0;
                }

                return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// Moves the highlight, without wrapping.
    ///
    /// Deliberately not circular: in a list scored by relevance, arriving back at the best match
    /// after passing the worst reads as the list having reset itself.
    /// </summary>
    private void Move(int delta)
    {
        if (_viewModel.Rows.Count == 0)
            return;

        int index = _viewModel.SelectedRow is { } row ? _viewModel.Rows.IndexOf(row) : -1;

        _viewModel.SelectedRow = _viewModel.Rows[Math.Clamp(index + delta, 0, _viewModel.Rows.Count - 1)];

        RowList.ScrollIntoView(_viewModel.SelectedRow);
    }
}
