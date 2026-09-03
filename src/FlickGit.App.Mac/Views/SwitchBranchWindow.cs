using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FlickGit.App.CommandLine;
using FlickGit.App.Localization;
using FlickGit.Branches;
using FlickGit.Matching;
using FlickGit.Models;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// The branch picker.
///
/// <b>The safety rule is the whole of this window, and it lives in the buttons rather than in the
/// code.</b> A plain switch is attempted first; when Git refuses because of local changes, the
/// window does <i>not</i> stash automatically. It shows the blocking files and offers the choice —
/// CLAUDE.md is explicit that stashing on the user's behalf is not a thing this does, and
/// <c>SwitchService.StashSwitchRestoreAsync</c> exists precisely so the stash path is one audited
/// sequence in Core rather than three commands assembled here.
///
/// Create is the filter box itself: text matching no ref is a new branch name, validated by
/// <c>check-ref-format</c> through <see cref="BranchService"/> before anything runs.
/// </summary>
internal sealed class SwitchBranchWindow : Window
{
    private readonly SwitchService _switches;
    private readonly BranchService _branches;
    private readonly IDialogs _dialogs;
    private readonly RepositoryInfo _repository;

    private readonly TextBox _filter = new()
    {
        Margin = new Thickness(10, 10, 10, 6),
        PlaceholderText = Strings.Get("switch.filter.hint"),
    };
    private readonly ListBox _list = new() { Margin = new Thickness(10, 0) };
    private readonly TextBlock _status = new() { Margin = new Thickness(10, 6), TextWrapping = TextWrapping.Wrap };
    private readonly Button _primary = new() { MinWidth = 130 };
    private readonly Button _stashSwitch = new() { MinWidth = 190, IsVisible = false };

    private SwitchCandidates _candidates = new([], []);
    private IReadOnlyList<string> _shown = [];

    public SwitchBranchWindow(
        RepositoryInfo repository,
        SwitchService switches,
        BranchService branches,
        IDialogs dialogs)
    {
        _repository = repository;
        _switches = switches;
        _branches = branches;
        _dialogs = dialogs;

        Title = Strings.Get("switch.title", repository.Name);
        Width = 560;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _primary.Content = Strings.Get("switch.button");
        _stashSwitch.Content = Strings.Get("switch.stash");

        _filter.TextChanged += (_, _) => ApplyFilter();
        _primary.Click += (_, _) => _ = SwitchAsync(stashFirst: false);
        _stashSwitch.Click += (_, _) => _ = SwitchAsync(stashFirst: true);

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Children =
            {
                Row(_filter, 0),
                Row(_list, 1),
                Row(_status, 2),
                Row(Buttons(), 3),
            },
        };

        Opened += (_, _) => { _filter.Focus(); _ = LoadAsync(); };
    }

    private Control Buttons() =>
        new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(10),
            Children = { _stashSwitch, _primary },
        };

    private async Task LoadAsync()
    {
        _candidates = await _switches.ListCandidatesAsync(_repository, CancellationToken.None)
            .ConfigureAwait(true);

        ApplyFilter();
    }

    /// <summary>
    /// Local branches first, remote-tracking below, scored by the same subsequence matcher the
    /// palette uses so "cnb" finds "create-new-branch" here too.
    /// </summary>
    private void ApplyFilter()
    {
        string term = _filter.Text ?? string.Empty;

        IEnumerable<string> all = _candidates.Local.Concat(_candidates.Remote);

        _shown = term.Length == 0
            ? all.ToArray()
            : all.Where(b => FuzzyMatcher.Score(b, term) is > 0)
                 .OrderByDescending(b => FuzzyMatcher.Score(b, term) ?? 0)
                 .ToArray();

        _list.ItemsSource = _shown;

        if (_shown.Count > 0 && _list.SelectedIndex < 0)
            _list.SelectedIndex = 0;

        //Text matching no ref is a new branch name. Saying so before Enter is pressed is the whole
        //point -- the button changes, so the user knows which of the two things is about to happen.
        bool creating = term.Length > 0 && !_candidates.Local.Contains(term) && !_candidates.Remote.Contains(term);

        _primary.Content = creating
            ? Strings.Get("switch.create", term)
            : Strings.Get("switch.button");
    }

    private string? Chosen()
    {
        string term = _filter.Text ?? string.Empty;

        if (term.Length > 0 && !_candidates.Local.Contains(term) && !_candidates.Remote.Contains(term))
            return term;

        return _list.SelectedItem as string ?? (term.Length > 0 ? term : null);
    }

    private async Task SwitchAsync(bool stashFirst)
    {
        if (Chosen() is not { Length: > 0 } branch)
            return;

        _status.Text = string.Empty;
        _stashSwitch.IsVisible = false;

        bool creating = !_candidates.Local.Contains(branch) && !_candidates.Remote.Contains(branch);

        if (creating)
        {
            //check-ref-format, before anything runs. A name Git will refuse is refused here with
            //Git's own reason rather than after a half-done sequence.
            BranchNameValidation validation = await _branches
                .ValidateAsync(_repository, branch, CancellationToken.None)
                .ConfigureAwait(true);

            if (!validation.IsValid)
            {
                _status.Text = validation.Error;

                return;
            }
        }

        SwitchOutcome outcome = stashFirst
            ? await _switches.StashSwitchRestoreAsync(_repository, branch, CancellationToken.None).ConfigureAwait(true)
            : creating
                ? await _switches.CreateAsync(_repository, branch, CancellationToken.None).ConfigureAwait(true)
                : await _switches.SwitchAsync(_repository, branch, CancellationToken.None).ConfigureAwait(true);

        if (outcome.Succeeded)
        {
            Close();

            return;
        }

        //Git's own words, and the blocking files named. Never a generic failure.
        _status.Text = outcome.RefusedByLocalChanges
            ? Strings.Get("switch.blocked.hint") + Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine, outcome.BlockingFiles)
            : outcome.GitError ?? Strings.Get("switch.none");

        //The stash path is offered only for the refusal it actually answers. A refusal with no named
        //files is a different failure, and leading the user to that button would be a stash that
        //could not have helped.
        _stashSwitch.IsVisible = outcome.RefusedByLocalChanges;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();

                return;

            case Key.Enter:
                e.Handled = true;
                _ = SwitchAsync(stashFirst: false);

                return;

            case Key.Down when _shown.Count > 0:
                e.Handled = true;
                _list.SelectedIndex = Math.Min(_list.SelectedIndex + 1, _shown.Count - 1);

                return;

            case Key.Up when _shown.Count > 0:
                e.Handled = true;
                _list.SelectedIndex = Math.Max(_list.SelectedIndex - 1, 0);

                return;
        }

        base.OnKeyDown(e);
    }

    private static T Row<T>(T control, int row)
        where T : Control
    {
        control.SetValue(Grid.RowProperty, row);

        return control;
    }
}
