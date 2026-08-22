using System.Windows;
using System.Windows.Input;
using FlickGit.App.Localization;
using FlickGit.Branches;
using FlickGit.Matching;
using FlickGit.Models;

namespace FlickGit.App.Views;

/// <summary>
/// The Switch branch picker.
///
/// Two behaviours here come straight from CLAUDE.md, "Switch Branch", and both are about what the
/// window refuses to do:
///
/// <list type="bullet">
/// <item><description><b>The plain switch is attempted first</b>, because Git carries uncommitted
/// changes across when there is no conflict, and that is usually what the user wants.</description></item>
/// <item><description><b>When Git refuses, nothing is stashed.</b> The blocking files are listed
/// and the user gets three explicit choices. Stash-switch-restore is a button they press, never
/// something that happens for them.</description></item>
/// </list>
/// </summary>
public partial class SwitchBranchWindow : Window
{
    private readonly RepositoryInfo _repository;
    private readonly SwitchService _switches;
    private readonly List<Candidate> _candidates = [];

    private string? _pendingBranch;

    public SwitchBranchWindow(RepositoryInfo repository, SwitchService switches, string? currentBranch)
    {
        InitializeComponent();

        _repository = repository;
        _switches = switches;

        Title = Strings.Get("switch.title", repository.Name);
        CurrentBranch = currentBranch;

        StashButton.Content = Strings.Get("switch.stash");
        SwitchButton.Content = Strings.Get("switch.button");

        Loaded += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>The branch that was checked out when the window opened.</summary>
    public string? CurrentBranch { get; }

    private async Task LoadAsync()
    {
        SwitchCandidates candidates = await _switches
            .ListCandidatesAsync(_repository, CancellationToken.None)
            .ConfigureAwait(true);

        _candidates.Clear();

        foreach (string branch in candidates.Local)
        {
            _candidates.Add(new Candidate(
                branch,
                //The current branch is labelled rather than hidden: seeing it in the list is what
                //tells the user where they are.
                string.Equals(branch, CurrentBranch, StringComparison.Ordinal)
                    ? Strings.Get("branch.current")
                    : Strings.Get("switch.local"),
                IsRemote: false));
        }

        //Remote-tracking branches below the local ones and separated, per CLAUDE.md.
        foreach (string branch in candidates.Remote)
            _candidates.Add(new Candidate(branch, Strings.Get("switch.remote"), IsRemote: true));

        ApplyFilter();
        FilterBox.Focus();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        string pattern = FilterBox.Text.Trim();

        //Local branches keep their ordering advantage over remote ones even under a fuzzy match:
        //switching to a local branch is the common case, and a remote match that scored a point
        //higher should not outrank it.
        IReadOnlyList<FuzzyMatch> ranked = FuzzyMatcher.Rank(
            _candidates.Select(c => c.Name),
            pattern,
            recencyRank: name => _candidates.First(c => c.Name == name).IsRemote ? 8 : 0);

        var matches = ranked
            .Select(m => _candidates.First(c => c.Name == m.Value))
            .ToList();

        BranchList.ItemsSource = matches;
        BranchList.SelectedIndex = matches.Count > 0 ? 0 : -1;

        StatusText.Text = matches.Count == 0 ? Strings.Get("switch.none") : string.Empty;
        SwitchButton.IsEnabled = matches.Count > 0;
    }

    /// <summary>
    /// Down/Up move the selection without leaving the filter box, so the whole interaction is
    /// type-then-Enter and the hands never leave the keyboard.
    /// </summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (BranchList.Items.Count == 0)
            return;

        switch (e.Key)
        {
            case Key.Down:
                BranchList.SelectedIndex = Math.Min(BranchList.SelectedIndex + 1, BranchList.Items.Count - 1);
                BranchList.ScrollIntoView(BranchList.SelectedItem);
                e.Handled = true;
                break;

            case Key.Up:
                BranchList.SelectedIndex = Math.Max(BranchList.SelectedIndex - 1, 0);
                BranchList.ScrollIntoView(BranchList.SelectedItem);
                e.Handled = true;
                break;
        }
    }

    private async void OnAccept(object sender, RoutedEventArgs e)
    {
        if (BranchList.SelectedItem is not Candidate candidate)
            return;

        if (string.Equals(candidate.Name, CurrentBranch, StringComparison.Ordinal))
        {
            //Already there. Doing nothing is the correct switch.
            Close();
            return;
        }

        await AttemptAsync(candidate).ConfigureAwait(true);
    }

    private async Task AttemptAsync(Candidate candidate)
    {
        SetBusy(true);

        try
        {
            //The plain switch first, always.
            SwitchOutcome outcome = candidate.IsRemote
                ? await _switches.SwitchTrackingAsync(_repository, candidate.Name, CancellationToken.None).ConfigureAwait(true)
                : await _switches.SwitchAsync(_repository, candidate.Name, CancellationToken.None).ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                Close();
                return;
            }

            if (outcome.RefusedByLocalChanges)
            {
                //Refused because of local changes. Nothing was modified or discarded; the user now
                //chooses between stashing, committing, and abandoning the switch.
                _pendingBranch = candidate.Name;

                BlockedText.Text = Strings.Get("branch.blocked", string.Empty).TrimEnd('\n');
                BlockedFiles.Text = string.Join('\n', outcome.BlockingFiles);
                BlockedHint.Text = Strings.Get("switch.blocked.hint");
                BlockedPanel.Visibility = Visibility.Visible;
                return;
            }

            //A failure a stash cannot fix. Reported with Git's own words, and no stash button.
            new NoticeWindow(Strings.Get("switch.button"), outcome.GitError ?? string.Empty, compact: false)
            {
                Owner = this,
            }.ShowDialog();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnStashSwitch(object sender, RoutedEventArgs e)
    {
        if (_pendingBranch is null)
            return;

        SetBusy(true);

        try
        {
            SwitchOutcome outcome = await _switches
                .StashSwitchRestoreAsync(_repository, _pendingBranch, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                Close();
                return;
            }

            //The one outcome that must never be reported vaguely: the switch happened and the
            //user's work is sitting in a stash. The reference is the actionable part.
            string message = outcome.RestoreConflicted && outcome.StashRef is not null
                ? $"{outcome.GitError}\n\n{Strings.Get("switch.stashkept", outcome.StashRef)}"
                : outcome.GitError ?? string.Empty;

            new NoticeWindow(Strings.Get("switch.stash"), message, compact: false) { Owner = this }.ShowDialog();

            if (outcome.RestoreConflicted)
            {
                //On the new branch already, so the picker has nothing left to do.
                Close();
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        SwitchButton.IsEnabled = !busy;
        StashButton.IsEnabled = !busy;
        FilterBox.IsEnabled = !busy;
        BranchList.IsEnabled = !busy;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    /// <param name="Name">The branch name, exactly as Git reports it.</param>
    /// <param name="Kind">The label shown on the right: current, local or remote.</param>
    /// <param name="IsRemote">Remote-tracking, so switching creates a local branch.</param>
    /// <summary>
    /// One row in the picker.
    ///
    /// <see cref="ToString"/> is overridden because a `ListBoxItem` whose content is a
    /// `DataTemplate` has no text of its own, so UI Automation falls back to it — and a record's
    /// synthesized version reads out every property name to a screen reader.
    /// </summary>
    private sealed record Candidate(string Name, string Kind, bool IsRemote)
    {
        public override string ToString() => $"{Name} {Kind}".TrimEnd();
    }
}
