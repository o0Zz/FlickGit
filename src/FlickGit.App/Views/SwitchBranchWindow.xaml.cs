using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
///
/// It also creates and deletes branches, which makes it the only surface in the product that does.
/// Both follow the same rule as the stash: <b>the destructive spelling is never reached by the
/// window deciding to reach it.</b> A delete runs <c>branch -d</c>; when Git refuses because the
/// branch is unmerged, the refusal is shown with its own second question, and only an explicit
/// answer to that one calls back with force. Deleting on a remote is confirmed in its own words,
/// because it is the only thing in FlickGit that destroys something other people share.
/// </summary>
public partial class SwitchBranchWindow : Window
{
    private readonly RepositoryInfo _repository;
    private readonly SwitchService _switches;
    private readonly BranchService _branches;
    private readonly List<Candidate> _candidates = [];

    private string? _pendingBranch;

    public SwitchBranchWindow(
        RepositoryInfo repository,
        SwitchService switches,
        BranchService branches,
        string? currentBranch)
    {
        InitializeComponent();

        _repository = repository;
        _switches = switches;
        _branches = branches;

        Title = Strings.Get("switch.title", repository.Name);
        CurrentBranch = currentBranch;

        StashButton.Content = Strings.Get("switch.stash");
        SwitchButton.Content = Strings.Get("switch.button");

        Loaded += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>The branch that was checked out when the window opened.</summary>
    public string? CurrentBranch { get; private set; }

    private async Task LoadAsync()
    {
        SwitchCandidates candidates = await _switches
            .ListCandidatesAsync(_repository, CancellationToken.None)
            .ConfigureAwait(true);

        _candidates.Clear();

        foreach (string branch in candidates.Local)
        {
            bool current = string.Equals(branch, CurrentBranch, StringComparison.Ordinal);

            _candidates.Add(new Candidate(
                branch,
                //The current branch is labelled rather than hidden: seeing it in the list is what
                //tells the user where they are.
                current ? Strings.Get("branch.current") : Strings.Get("switch.local"),
                current ? CandidateKind.Current : CandidateKind.Local));
        }

        //Remote-tracking branches below the local ones and separated, per CLAUDE.md.
        foreach (string branch in candidates.Remote)
            _candidates.Add(new Candidate(branch, Strings.Get("switch.remote"), CandidateKind.Remote));

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

        //The create row, when what has been typed is a branch name that does not exist yet.
        //
        //This is the whole of "create a new branch from here": no second window and no New button,
        //the same gesture the commit window's ComboBox already uses -- "type a new name to create
        //it". It sits last rather than first so Enter on a filter that also matches something keeps
        //switching to the match, which is what the box is for.
        if (CreateCandidate(pattern) is { } create)
            matches.Add(create);

        BranchList.ItemsSource = matches;
        BranchList.SelectedIndex = matches.Count > 0 ? 0 : -1;

        StatusText.Text = matches.Count == 0 ? Strings.Get("switch.none") : string.Empty;
        SwitchButton.IsEnabled = matches.Count > 0;
    }

    /// <summary>
    /// The synthetic "create this branch" row, or null when the text is not a name to create.
    ///
    /// Null for an empty box, for a name Git would refuse, and for one that already exists locally —
    /// offering to create a branch that is right there in the list would be offering an error.
    /// </summary>
    private Candidate? CreateCandidate(string pattern)
    {
        if (pattern.Length == 0 || !BranchService.LooksValid(pattern))
            return null;

        bool exists = _candidates.Any(c =>
            c.Row != CandidateKind.Remote &&
            string.Equals(c.Name, pattern, StringComparison.Ordinal));

        return exists
            ? null
            : new Candidate(pattern, Strings.Get("switch.create.kind"), CandidateKind.Create);
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

    // ---- the context menu ----------------------------------------------------------------

    /// <summary>
    /// Selects the row under the pointer before the menu opens.
    ///
    /// A <c>ListBox</c> does not do this itself, so without it a right-click builds a menu for
    /// whatever was selected before — which for a delete is the difference between removing the
    /// branch that was clicked and removing a different one.
    /// </summary>
    private void OnRowRightClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject).FindAncestor<ListBoxItem>() is { } row)
            row.IsSelected = true;
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        RowMenu.Items.Clear();

        //Nothing to offer for the create row: it names a branch that does not exist, so there is
        //nothing to delete and Enter already creates it.
        if (BranchList.SelectedItem is not Candidate candidate || candidate.Row == CandidateKind.Create)
        {
            e.Handled = true;
            return;
        }

        //The current branch is deletable by nothing, here or in Git. Omitted rather than greyed out,
        //the same rule the Explorer menu follows for an item that does not apply.
        if (candidate.Row == CandidateKind.Current)
        {
            e.Handled = true;
            return;
        }

        if (candidate.Row == CandidateKind.Local)
        {
            RowMenu.Items.Add(MenuItemFor(
                Strings.Get("switch.menu.delete"),
                () => DeleteLocalAsync(candidate.Name, force: false)));
        }
        else
        {
            //The remote's name is resolved when the menu opens rather than when it is clicked, so
            //the label can say where the deletion would land -- "Delete on origin…" is a different
            //promise from "Delete…", and it is the one the user needs before pressing it.
            string label = candidate.Name.Split('/', 2) is [{ Length: > 0 } remote, _]
                ? Strings.Get("switch.menu.deleteremote", remote)
                : Strings.Get("switch.menu.delete");

            RowMenu.Items.Add(MenuItemFor(label, () => DeleteRemoteAsync(candidate.Name)));
        }
    }

    private static MenuItem MenuItemFor(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        item.Click += async (_, _) => await action().ConfigureAwait(true);
        return item;
    }

    // ---- switching and creating ----------------------------------------------------------

    private async void OnAccept(object sender, RoutedEventArgs e)
    {
        if (BranchList.SelectedItem is not Candidate candidate)
            return;

        if (candidate.Row == CandidateKind.Create)
        {
            await CreateAsync(candidate.Name).ConfigureAwait(true);
            return;
        }

        if (candidate.Row == CandidateKind.Current)
        {
            //Already there. Doing nothing is the correct switch.
            Close();
            return;
        }

        await AttemptAsync(candidate).ConfigureAwait(true);
    }

    /// <summary>
    /// Creates the typed branch at the current commit and switches to it.
    ///
    /// From HEAD, not from whatever row happens to be highlighted — CLAUDE.md, "Branch Selector":
    /// "The branch is created from the currently checked-out commit unless the user explicitly
    /// chooses otherwise." Creating from the selection would be a second, invisible meaning for a
    /// list whose whole job so far has been "where do I want to go".
    /// </summary>
    private async Task CreateAsync(string name)
    {
        SetBusy(true);

        try
        {
            //Git's own answer before anything is created, not just the offline regex the row was
            //offered on: check-ref-format knows the rules this build enforces.
            BranchNameValidation validation = await _branches
                .ValidateAsync(_repository, name, CancellationToken.None)
                .ConfigureAwait(true);

            if (!validation.IsValid)
            {
                Report(Strings.Get("switch.create", name), validation.Error ?? string.Empty);
                return;
            }

            SwitchOutcome outcome = await _switches
                .CreateAsync(_repository, name, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                Close();
                return;
            }

            Report(Strings.Get("switch.create", name), outcome.GitError ?? string.Empty);
        }
        finally
        {
            SetBusy(false);
        }
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
            Report(Strings.Get("switch.button"), outcome.GitError ?? string.Empty);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ---- deleting -------------------------------------------------------------------------

    /// <summary>
    /// Deletes a local branch, asking first — and asking a second time before ever forcing.
    ///
    /// <paramref name="force"/> is only ever true on the recursive call this method makes after Git
    /// has refused an unmerged branch and the user has answered the question naming that fact.
    /// CLAUDE.md puts <c>branch -D</c> on the Safety Rules list, and this is what "explicit user
    /// intent, expressed in the moment" looks like: two different questions, neither remembered.
    /// </summary>
    private async Task DeleteLocalAsync(string name, bool force)
    {
        if (!force && !ConfirmWindow.Ask(
                this,
                Strings.Get("branch.delete.title"),
                Strings.Get("branch.delete.local", name),
                Strings.Get("branch.delete.yes"),
                Strings.Get("action.confirm.no")))
        {
            return;
        }

        SetBusy(true);

        try
        {
            BranchDeleteOutcome outcome = await _branches
                .DeleteLocalAsync(_repository, name, CurrentBranch, force, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                await LoadAsync().ConfigureAwait(true);
                StatusText.Text = Strings.Get("branch.deleted", name);
                return;
            }

            if (outcome.WasCurrentBranch)
            {
                //Refused before any command ran, so there is nothing to report but the reason.
                StatusText.Text = Strings.Get("branch.delete.current", name);
                return;
            }

            if (outcome.NotMerged)
            {
                //The one refusal with a way forward. The question names what is at stake rather
                //than repeating Git's hint, and answering it is the only route to -D.
                bool anyway = ConfirmWindow.Ask(
                    this,
                    Strings.Get("branch.delete.title"),
                    Strings.Get("branch.delete.unmerged", name),
                    Strings.Get("branch.delete.force"),
                    Strings.Get("action.confirm.no"));

                if (anyway)
                {
                    //Released first: the recursive call takes it again, and a nested SetBusy(false)
                    //in its finally would otherwise unlock the window while it is still working.
                    SetBusy(false);
                    await DeleteLocalAsync(name, force: true).ConfigureAwait(true);
                }

                return;
            }

            Report(Strings.Get("branch.delete.title"), outcome.GitError ?? string.Empty);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Deletes a branch on its remote — the one operation in FlickGit that destroys something other
    /// people share, and the only one with no local undo.
    ///
    /// The remote is resolved against the configured remotes before anything is asked, so a row
    /// whose prefix is not a remote is refused here rather than becoming a push at a remote that
    /// does not exist.
    /// </summary>
    private async Task DeleteRemoteAsync(string remoteTrackingName)
    {
        SetBusy(true);

        try
        {
            RemoteBranch? target = await _branches
                .ResolveRemoteBranchAsync(_repository, remoteTrackingName, CancellationToken.None)
                .ConfigureAwait(true);

            if (target is null)
            {
                StatusText.Text = Strings.Get("branch.delete.noremote", remoteTrackingName);
                return;
            }

            if (!ConfirmWindow.Ask(
                    this,
                    Strings.Get("branch.delete.title"),
                    Strings.Get("branch.delete.remote", target.Branch, target.Remote),
                    Strings.Get("branch.delete.yes"),
                    Strings.Get("action.confirm.no")))
            {
                return;
            }

            BranchDeleteOutcome outcome = await _branches
                .DeleteRemoteAsync(_repository, target.Remote, target.Branch, CancellationToken.None)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                Report(Strings.Get("branch.delete.title"), outcome.GitError ?? string.Empty);
                return;
            }

            //`push --delete` removes the remote-tracking ref as well, so re-listing is what makes
            //the row disappear. Nothing here prunes anything by hand.
            await LoadAsync().ConfigureAwait(true);
            StatusText.Text = Strings.Get("branch.deleted.remote", target.Branch, target.Remote);
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

            Report(Strings.Get("switch.stash"), message);

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

    private void Report(string title, string message) =>
        new NoticeWindow(title, message, compact: false) { Owner = this }.ShowDialog();

    private void SetBusy(bool busy)
    {
        SwitchButton.IsEnabled = !busy;
        StashButton.IsEnabled = !busy;
        FilterBox.IsEnabled = !busy;
        BranchList.IsEnabled = !busy;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    /// <summary>What a row is, which decides what right-clicking it offers.</summary>
    private enum CandidateKind
    {
        /// <summary>The branch that is checked out. Switched to by doing nothing, deleted by nothing.</summary>
        Current,

        Local,

        /// <summary>Remote-tracking, so switching creates a local branch and deleting is a push.</summary>
        Remote,

        /// <summary>Not a branch yet: the name in the filter box, offered for creation.</summary>
        Create,
    }

    /// <param name="Name">The branch name, exactly as Git reports it.</param>
    /// <param name="Kind">The label shown on the right: current, local, remote or new.</param>
    /// <param name="Row">What the row <i>is</i>, which decides what right-clicking it offers.</param>
    /// <summary>
    /// One row in the picker.
    ///
    /// <see cref="ToString"/> is overridden because a `ListBoxItem` whose content is a
    /// `DataTemplate` has no text of its own, so UI Automation falls back to it — and a record's
    /// synthesized version reads out every property name to a screen reader.
    /// </summary>
    private sealed record Candidate(string Name, string Kind, CandidateKind Row)
    {
        public bool IsRemote => Row == CandidateKind.Remote;

        /// <summary>The create row says what pressing Enter would do; every other row is its name.</summary>
        public string Display => Row == CandidateKind.Create ? Strings.Get("switch.create", Name) : Name;

        /// <summary>
        /// The accent for the create row, so it does not read as a branch that already exists.
        /// Resolved from the window's own resources rather than hard-coded, so it follows the theme.
        /// </summary>
        public Brush Brush => Row == CandidateKind.Create
            ? (Brush)Application.Current.Resources["Accent"]
            : (Brush)Application.Current.Resources["Text"];

        public override string ToString() => $"{Display} {Kind}".TrimEnd();
    }
}

/// <summary>Walks up the visual tree, for the one place that has to find the row under the pointer.</summary>
internal static class VisualTreeSearch
{
    public static T? FindAncestor<T>(this DependencyObject? from) where T : DependencyObject
    {
        for (DependencyObject? node = from; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is T match)
                return match;
        }

        return null;
    }
}
