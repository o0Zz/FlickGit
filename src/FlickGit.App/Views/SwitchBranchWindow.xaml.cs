using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.Branches;
using FlickGit.Matching;
using FlickGit.Models;
using FlickGit.Worktrees;
using Microsoft.Win32;

namespace FlickGit.App.Views;

/// <summary>
/// The Switch branch picker.
///
/// <b>The plain switch is attempted first</b>, because Git carries uncommitted changes across when
/// there is no conflict. <b>When Git refuses, nothing is stashed:</b> the blocking files are
/// listed and the user gets three explicit choices. Stash-switch-restore is a button they press,
/// never something that happens for them.
///
/// It also creates and deletes branches, which makes it the only surface in the product that does.
/// <b>The destructive spelling is never reached by the window deciding to reach it.</b> A delete
/// runs <c>branch -d</c>; when Git refuses an unmerged branch, that refusal gets its own second
/// question, and only an answer to it calls back with force.
///
/// <b>Worktrees live on these rows rather than in a window of their own</b>, because Git allows at
/// most one worktree per branch -- so a branch row is the only index there is. A branch checked out
/// somewhere else cannot be switched to at all, which is the state this used to report as a raw Git
/// error: the row now says where it is, and Enter opens that folder instead of attempting a switch
/// Git is certain to refuse.
/// </summary>
public partial class SwitchBranchWindow : Window
{
    private readonly RepositoryInfo _repository;
    private readonly SwitchService _switches;
    private readonly BranchService _branches;
    private readonly WorktreeService _worktrees;
    private readonly List<Candidate> _candidates = [];

    /// <summary>
    /// Cancelled when the window closes, and passed to this window's <i>reads</i> only.
    ///
    /// The writes keep <see cref="CancellationToken.None"/> on purpose: abandoning one part-way leaves
    /// the repository in a state nobody reported, which is worse than waiting for it.
    /// </summary>
    private readonly CancellationTokenSource _closing = new();

    private bool _busy;

    private string? _pendingBranch;

    public SwitchBranchWindow(
        RepositoryInfo repository,
        SwitchService switches,
        BranchService branches,
        WorktreeService worktrees,
        string? currentBranch)
    {
        InitializeComponent();

        _repository = repository;
        _switches = switches;
        _branches = branches;
        _worktrees = worktrees;

        Title = Strings.Get("switch.title", repository.Name);
        CurrentBranch = currentBranch;

        StashButton.Content = Strings.Get("switch.stash");
        SwitchButton.Content = Strings.Get("switch.button");

        //The footer button only dismisses the window, so it says Close. The one in the blocked strip
        //is the third answer to a question -- it declines the switch -- so that one says Cancel.
        CloseButton.Content = Strings.Get("common.close");
        //"Close", not "Cancel": Git has already refused by the time this panel is on screen, so there
        //is nothing in flight for this button to stop -- it dismisses the window, which is what
        //common.close is for. The one place in the product that had these two words the wrong way
        //round.
        BlockedCloseButton.Content = Strings.Get("common.close");

        //F5 re-reads. A window binding rather than a button, so it works from the filter box and the
        //list alike -- the same shape as the commit window's, which was the only F5 in the product.
        //
        //AsyncCommand rather than RelayCommand over an async void: Commands.cs gives both reasons and
        //both apply here. Its re-entrancy guard stops two F5 presses interleaving two reads of the
        //same list, and its onError keeps an unhandled task exception out of WPF's dispatcher, where
        //it would take the resident process and every pre-warmed window with it.
        InputBindings.Add(new KeyBinding
        {
            Key = Key.F5,
            Command = new AsyncCommand(
                LoadAsync,
                canExecute: () => !_busy,
                onError: exception => Notice.Show(this, Strings.Get("error.title"), exception.Message)),
        });

        Loaded += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }

    public string? CurrentBranch { get; private set; }

    /// <summary>
    /// The window's read, with the one exception a closing window can now raise turned back into
    /// "stop". Without this the token added for _closing would surface as an unhandled
    /// OperationCanceledException inside an async void handler, which ends the process -- a worse
    /// outcome than the leak it was added to fix.
    /// </summary>
    private async Task LoadAsync()
    {
        //A write that finished after the window closed still asks for a reload. There is nothing left
        //to populate, and the read would only be cancelled a moment later anyway.
        if (_closing.IsCancellationRequested)
            return;

        try
        {
            await ReadStateAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            //Closed while the read was in flight. There is no longer anything to populate.
        }
    }

    private async Task ReadStateAsync()
    {
        //Both reads at once. The worktree list is one more process on a window with no latency target,
        //and running it in parallel keeps it off the wall-clock entirely.
        Task<SwitchCandidates> candidateTask = _switches.ListCandidatesAsync(_repository, _closing.Token);
        Task<IReadOnlyList<GitWorktree>> worktreeTask = _worktrees.ListAsync(_repository, _closing.Token);

        SwitchCandidates candidates = await candidateTask.ConfigureAwait(true);
        IReadOnlyList<GitWorktree> worktrees = await worktreeTask.ConfigureAwait(true);

        //Keyed by branch because Git allows at most one worktree per branch, which is what makes a
        //dictionary the right shape rather than a list to search. Detached and bare worktrees have no
        //branch and so are absent: there is no row for them here, which the class comment records.
        Dictionary<string, GitWorktree> byBranch = worktrees
            .Where(w => w.Branch is { Length: > 0 })
            .GroupBy(w => w.Branch!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        _candidates.Clear();

        foreach (string branch in candidates.Local)
        {
            bool current = string.Equals(branch, CurrentBranch, StringComparison.Ordinal);

            byBranch.TryGetValue(branch, out GitWorktree? worktree);

            //The worktree this window was opened on is not "somewhere else", so it is dropped rather than
            //labelled. The comparison is against the resolved root rather than against IsMain, because the
            //window can perfectly well be opened *on* a linked worktree -- in which case the main one holds
            //some other branch and is the row that needs the label.
            if (worktree is not null && IsHere(worktree))
                worktree = null;

            _candidates.Add(new Candidate(
                branch,
                //The current branch is labelled rather than hidden: seeing it in the list is what tells the user
                //where they are.
                current ? Strings.Get("branch.current") : Strings.Get("switch.local"),
                current ? CandidateKind.Current : CandidateKind.Local,
                worktree));
        }

        foreach (string branch in candidates.Remote)
            _candidates.Add(new Candidate(branch, Strings.Get("switch.remote"), CandidateKind.Remote));

        ApplyFilter();
        FilterBox.Focus();
    }

    /// <summary>True when this worktree is the checkout the window was opened on.</summary>
    private bool IsHere(GitWorktree worktree) =>
        string.Equals(worktree.Path, _repository.Root, StringComparison.OrdinalIgnoreCase);

    private void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();

    /// <summary>
    /// Keeps the primary button honest about what it will do.
    ///
    /// On a row whose branch is checked out in another worktree the button opens a folder, and Git
    /// would refuse a switch outright -- so a button still reading "Switch" would be naming the one
    /// thing that row cannot do. The label follows the selection rather than the filter, because the
    /// arrow keys move it without re-running <see cref="ApplyFilter"/>.
    /// </summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SwitchButton.Content = BranchList.SelectedItem is Candidate { Worktree: { IsPrunable: false } }
            ? Strings.Get("worktree.open")
            : Strings.Get("switch.button");
    }

    private void ApplyFilter()
    {
        string pattern = FilterBox.Text.Trim();

        //Local branches keep their ordering advantage over remote ones even under a fuzzy match:
        //switching to a local branch is the common case, and a remote match that scored a point higher
        //should not outrank it.
        IReadOnlyList<FuzzyMatch> ranked = FuzzyMatcher.Rank(
            _candidates.Select(c => c.Name),
            pattern,
            recencyRank: name => _candidates.First(c => c.Name == name).IsRemote ? 8 : 0);

        var matches = ranked
            .Select(m => _candidates.First(c => c.Name == m.Value))
            .ToList();

        //The create row, when what has been typed is a branch name that does not exist yet. Last rather
        //than first, so Enter on a filter that also matches something keeps switching to the match.
        if (CreateCandidate(pattern) is { } create)
            matches.Add(create);

        BranchList.ItemsSource = matches;
        BranchList.SelectedIndex = matches.Count > 0 ? 0 : -1;

        StatusText.Text = matches.Count == 0 ? Strings.Get("switch.none") : string.Empty;
        SwitchButton.IsEnabled = matches.Count > 0;
    }

    /// <summary>
    /// The synthetic "create this branch" row, or null when the text is not a name to create: an empty
    /// box, a name Git would refuse, or one that already exists locally.
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

    private void OnFilterKeyDown(object sender, KeyEventArgs e) => FilterList.RouteArrows(BranchList, e);

    private void OnRowRightClick(object sender, MouseButtonEventArgs e) =>
        FilterList.SelectRowUnderPointer(BranchList, e.OriginalSource);

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        RowMenu.Items.Clear();

        //Nothing to offer for the create row: it names a branch that does not exist.
        if (BranchList.SelectedItem is not Candidate candidate || candidate.Row == CandidateKind.Create)
        {
            e.Handled = true;
            return;
        }

        //The current branch is deletable by nothing, here or in Git. Omitted rather than greyed out.
        if (candidate.Row == CandidateKind.Current)
        {
            e.Handled = true;
            return;
        }

        //The worktree items come first: on a row that has one they are the only things that work, since
        //Git refuses to switch to a branch checked out elsewhere and refuses to delete it either.
        if (candidate.Worktree is { } worktree)
        {
            if (worktree.IsPrunable)
            {
                RowMenu.Items.Add(Menus.Item(
                    Strings.Get("worktree.menu.prune"),
                    () => PruneAsync(candidate.Name, worktree)));
            }
            else
            {
                RowMenu.Items.Add(Menus.Item(
                    Strings.Get("worktree.menu.open"),
                    () =>
                    {
                        OpenFolder(worktree.Path);
                        return Task.CompletedTask;
                    }));

                RowMenu.Items.Add(Menus.Item(
                    Strings.Get("worktree.menu.remove"),
                    () => RemoveWorktreeAsync(candidate.Name, worktree)));
            }

            return;
        }

        //Offered on every remaining row, local and remote alike. A remote row creates a local branch
        //tracking it, which is what switching to a remote row already does -- so this is not a hole the
        //user has to work around by switching first. Rows that already have a worktree returned above:
        //Git allows at most one per branch.
        RowMenu.Items.Add(Menus.Item(
            Strings.Get("worktree.menu.add"),
            () => AddWorktreeAsync(candidate)));

        RowMenu.Items.Add(new Separator());

        if (candidate.Row == CandidateKind.Local)
        {
            RowMenu.Items.Add(Menus.Item(
                Strings.Get("switch.menu.delete"),
                () => DeleteLocalAsync(candidate.Name, force: false)));
        }
        else
        {
            //The remote's name is resolved when the menu opens rather than when it is clicked, so the label
            //can say where the deletion would land -- "Delete on origin..." is a different promise from
            //"Delete...", and it is the one the user needs before pressing it.
            string label = candidate.Name.Split('/', 2) is [{ Length: > 0 } remote, _]
                ? Strings.Get("switch.menu.deleteremote", remote)
                : Strings.Get("switch.menu.delete");

            RowMenu.Items.Add(Menus.Item(label, () => DeleteRemoteAsync(candidate.Name)));
        }
    }

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

        //Checked out in another worktree, where Git refuses a switch outright. Opening that folder is the
        //only thing this row can usefully do, so it is what the primary gesture does -- rather than
        //running a command whose failure the user can do nothing about.
        if (candidate.Worktree is { } worktree)
        {
            if (worktree.IsPrunable)
            {
                //The directory is gone and Git still believes the branch is checked out there, which is why
                //every switch to it is refused. Pruning is the fix, and it is offered rather than performed.
                await PruneAsync(candidate.Name, worktree).ConfigureAwait(true);
                return;
            }

            OpenFolder(worktree.Path);
            return;
        }

        await AttemptAsync(candidate).ConfigureAwait(true);
    }

    /// <summary>
    /// Shows a worktree in Explorer.
    ///
    /// Explorer rather than an editor: which editor the user wants is a guess, and a folder window is
    /// the one answer that is right on every machine. From there the folder is an ordinary repository
    /// -- <c>flick commit</c> and the context menu work in it, because <c>RepositoryService</c> asks
    /// Git for the root rather than looking for a <c>.git</c> directory.
    /// </summary>
    private void OpenFolder(string path)
    {
        try
        {
            //UseShellExecute is required to hand a directory to the shell; without it this would be an
            //attempt to execute the folder.
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            StatusText.Text = Strings.Get("worktree.opened", path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or IOException)
        {
            //A path that has gone away since the list was read, or one the shell refuses. Reported rather
            //than thrown: nothing has changed, and the row is still there to try again.
            StatusText.Text = Strings.Get("worktree.openfailed", path);
        }
    }

    /// <summary>
    /// Creates the typed branch at the current commit and switches to it.
    ///
    /// From HEAD, not from whatever row happens to be highlighted. Creating from the selection would
    /// be a second, invisible meaning for a list whose whole job so far has been "where do I want to
    /// go".
    /// </summary>
    private async Task CreateAsync(string name)
    {
        SetBusy(true);

        try
        {
            //Git's own answer before anything is created, not just the offline regex the row was offered on:
            //check-ref-format knows the rules this build enforces.
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
                //Refused because of local changes. Nothing was modified or discarded.
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

    /// <summary>
    /// Deletes a local branch, asking first -- and asking a second time before ever forcing.
    ///
    /// <paramref name="force"/> is only ever true on the recursive call this method makes after Git
    /// has refused an unmerged branch and the user has answered the question naming that fact. Two
    /// different questions, neither remembered.
    /// </summary>
    private async Task DeleteLocalAsync(string name, bool force)
    {
        if (!force && !ConfirmWindow.Ask(
                this,
                Strings.Get("branch.delete.title"),
                Strings.Get("branch.delete.local", name),
                Strings.Get("branch.delete.yes"),
                Strings.Get("common.cancel"),
                destructive: true))
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
                //The one refusal with a way forward. The question names what is at stake rather than repeating
                //Git's hint, and answering it is the only route to -D.
                bool anyway = ConfirmWindow.Ask(
                    this,
                    Strings.Get("branch.delete.title"),
                    Strings.Get("branch.delete.unmerged", name),
                    Strings.Get("branch.delete.force"),
                    Strings.Get("common.cancel"),
                    destructive: true);

                if (anyway)
                {
                    //Released first: the recursive call takes it again, and a nested SetBusy(false) in its finally
                    //would otherwise unlock the window while it is still working.
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
    /// Deletes a branch on its remote -- the one operation in FlickGit that destroys something other
    /// people share, and the only one with no local undo.
    ///
    /// The remote is resolved against the configured remotes before anything is asked, so a row whose
    /// prefix is not a remote is refused here rather than becoming a push at a remote that does not
    /// exist.
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
                StatusText.Text = Strings.Get("branch.noremote", remoteTrackingName);
                return;
            }

            if (!ConfirmWindow.Ask(
                    this,
                    Strings.Get("branch.delete.title"),
                    Strings.Get("branch.delete.remote", target.Branch, target.Remote),
                    Strings.Get("branch.delete.yes"),
                    Strings.Get("common.cancel"),
                    destructive: true))
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

            //`push --delete` removes the remote-tracking ref as well, so re-listing is what makes the row
            //disappear. Nothing here prunes anything by hand.
            await LoadAsync().ConfigureAwait(true);
            StatusText.Text = Strings.Get("branch.deleted.remote", target.Branch, target.Remote);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Creates a worktree for the clicked branch: a second checkout of this repository, in its own
    /// folder, on its own branch.
    ///
    /// <b>Not confirmed, and it needs no confirmation.</b> Nothing existing is touched -- it makes a
    /// directory and checks a branch out into it, which is the only Git operation reachable from this
    /// window that cannot lose anything.
    ///
    /// The dialog picks the <i>parent</i> and the leaf is derived from the repository and branch names,
    /// which is what keeps the whole interaction one dialog. A path typed by hand would need a window
    /// of its own to type it into, and the folder picker already knows how to make a directory.
    /// </summary>
    private async Task AddWorktreeAsync(Candidate candidate)
    {
        //What to check out, and under what local name. A remote row creates a local branch tracking it --
        //the same thing switching to a remote row does -- unless that branch already exists here, in
        //which case there is nothing to create and `--track -b` would fail on the name.
        string branch = candidate.Name;
        WorktreeStart start;

        if (candidate.IsRemote)
        {
            RemoteBranch? resolved = await _branches
                .ResolveRemoteBranchAsync(_repository, candidate.Name, CancellationToken.None)
                .ConfigureAwait(true);

            if (resolved is null)
            {
                //The same refusal a remote deletion makes, and the same key: the sentence is about the
                //row's name rather than about what was being done with it, which is why there is one
                //string rather than a second copy of it under a worktree name.
                StatusText.Text = Strings.Get("branch.noremote", candidate.Name);
                return;
            }

            branch = resolved.Branch;

            bool existsLocally = _candidates.Any(c =>
                c.Row is CandidateKind.Local or CandidateKind.Current &&
                string.Equals(c.Name, branch, StringComparison.Ordinal));

            start = existsLocally
                ? WorktreeStart.Existing(branch)
                : WorktreeStart.Track(branch, candidate.Name);
        }
        else
        {
            start = WorktreeStart.Existing(branch);
        }

        //Defaulting beside the repository rather than inside it, which is both the shape of the
        //suggestion and the thing CheckTarget refuses outright.
        var dialog = new OpenFolderDialog
        {
            Title = Strings.Get("worktree.pick", branch),
            InitialDirectory = Directory.GetParent(_repository.Root)?.FullName ?? _repository.Root,
        };

        if (dialog.ShowDialog(this) != true)
            return;

        string path = Path.Combine(dialog.FolderName, WorktreeService.SuggestFolderName(_repository.Name, branch));

        SetBusy(true);

        try
        {
            WorktreeOutcome outcome = await _worktrees
                .AddAsync(_repository, path, start, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                //Re-listed rather than patched, so the new row comes from `worktree list` like every other --
                //there is no second place for what a worktree row says to be decided.
                await LoadAsync().ConfigureAwait(true);
                StatusText.Text = Strings.Get("worktree.created", path);
                return;
            }

            if (outcome.Refusal != WorktreeRefusal.None)
            {
                StatusText.Text = RefusalText(outcome.Refusal, path);
                return;
            }

            Report(Strings.Get("worktree.menu.add"), outcome.GitError ?? string.Empty);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Removes a worktree, asking once.
    ///
    /// <b>There is no second question and no forced spelling</b>, which is the one place this window's
    /// worktree items depart from its branch ones. <c>git worktree remove --force</c> deletes modified
    /// and untracked files outright -- no reflog, and no Recycle Bin, because nothing in Git has ever
    /// seen them. So a dirty worktree is reported with the two ways out that destroy nothing, and
    /// forcing stays something the user types themselves.
    /// </summary>
    private async Task RemoveWorktreeAsync(string branch, GitWorktree worktree)
    {
        if (!ConfirmWindow.Ask(
                this,
                Strings.Get("worktree.remove.title"),
                Strings.Get("worktree.remove.ask", branch, worktree.Path),
                Strings.Get("worktree.remove.yes"),
                Strings.Get("common.cancel"),
                destructive: true))
        {
            return;
        }

        SetBusy(true);

        try
        {
            WorktreeOutcome outcome = await _worktrees
                .RemoveAsync(_repository, worktree, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                await LoadAsync().ConfigureAwait(true);
                StatusText.Text = Strings.Get("worktree.removed", worktree.Path);
                return;
            }

            if (outcome.Refusal != WorktreeRefusal.None)
            {
                StatusText.Text = RefusalText(outcome.Refusal, worktree.Path);
                return;
            }

            //Git refused because there is work in there. Reported with what to do about it rather than with
            //a button that would delete it.
            Report(
                Strings.Get("worktree.remove.title"),
                outcome.HasLocalChanges
                    ? Strings.Get("worktree.remove.dirty", worktree.Path)
                    : outcome.GitError ?? string.Empty);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// Drops Git's bookkeeping for worktrees whose folder is gone -- the state a user reaches by
    /// deleting a worktree in Explorer, and the only one in this feature they cannot otherwise get out
    /// of: until it is pruned, Git still believes the branch is checked out and refuses every switch to
    /// it, naming a directory that does not exist.
    ///
    /// Confirmed because it is repository-wide rather than about the one row that was clicked, and
    /// there is no such thing as pruning a single entry. Nothing on disk is destroyed: a worktree that
    /// still exists is not prunable, whatever state it is in.
    /// </summary>
    private async Task PruneAsync(string branch, GitWorktree worktree)
    {
        if (!ConfirmWindow.Ask(
                this,
                Strings.Get("worktree.prune.title"),
                Strings.Get("worktree.prune.ask", branch, worktree.Path),
                Strings.Get("worktree.prune.yes"),
                Strings.Get("common.cancel"),
                destructive: true))
        {
            return;
        }

        SetBusy(true);

        try
        {
            WorktreeOutcome outcome = await _worktrees
                .PruneAsync(_repository, CancellationToken.None)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                Report(Strings.Get("worktree.prune.title"), outcome.GitError ?? string.Empty);
                return;
            }

            await LoadAsync().ConfigureAwait(true);
            StatusText.Text = Strings.Get("worktree.pruned", branch);
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// A refusal that happened before Git was asked anything, in words that say what to do instead.
    /// One function rather than a message per call site, so the same refusal cannot come to mean two
    /// things depending on which operation hit it.
    /// </summary>
    private static string RefusalText(WorktreeRefusal refusal, string path) => refusal switch
    {
        WorktreeRefusal.InsideRepository => Strings.Get("worktree.refused.inside", path),
        WorktreeRefusal.NotEmpty => Strings.Get("worktree.refused.notempty", path),
        WorktreeRefusal.IsMainWorktree => Strings.Get("worktree.refused.main"),
        WorktreeRefusal.IsLocked => Strings.Get("worktree.refused.locked", path),

        //NotAbsolute is unreachable from here -- the path is built from a folder dialog's answer -- but a
        //silent empty status line would be worse than naming it.
        _ => Strings.Get("worktree.refused.path", path),
    };

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

            //The one outcome that must never be reported vaguely: the switch happened and the user's work is
            //sitting in a stash. The reference is the actionable part.
            string message = outcome.RestoreConflicted && outcome.StashRef is not null
                ? $"{outcome.GitError}\n\n{Strings.Get("switch.stashkept", outcome.StashRef)}"
                : outcome.GitError ?? string.Empty;

            Report(Strings.Get("switch.stash"), message);

            if (outcome.RestoreConflicted)
            {
                Close();
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Report(string title, string message) => Notice.Show(this, title, message);

    private void SetBusy(bool busy)
    {
        _busy = busy;

        SwitchButton.IsEnabled = !busy;
        StashButton.IsEnabled = !busy;
        FilterBox.IsEnabled = !busy;
        BranchList.IsEnabled = !busy;
    }

    protected override void OnClosed(EventArgs e)
    {
        //Cancel, and deliberately *not* Dispose. Every write in this window runs to completion on
        //CancellationToken.None and then reloads, so a token read can still happen after the window is
        //gone -- and CancellationTokenSource.Token throws ObjectDisposedException once disposed, which
        //in an async continuation means the resident process dies. Cancelling is what this needs; the
        //source is collected with the window.
        _closing.Cancel();

        base.OnClosed(e);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private enum CandidateKind
    {
        Current,

        Local,

        Remote,

        Create,
    }

    /// <summary>
    /// One row in the picker. <see cref="ToString"/> is overridden because a <c>ListBoxItem</c> whose
    /// content is a <c>DataTemplate</c> has no text of its own, so UI Automation falls back to it --
    /// and a record's synthesised version reads every property name out to a screen reader.
    /// </summary>
    /// <param name="Worktree">
    /// The linked worktree holding this branch, when one does and it is not the checkout this window
    /// was opened on. Null for every other row, which is what <see cref="Kind"/> and the context menu
    /// both branch on.
    /// </param>
    private sealed record Candidate(string Name, string BaseKind, CandidateKind Row, GitWorktree? Worktree = null)
    {
        public bool IsRemote => Row == CandidateKind.Remote;

        public string Display => Row == CandidateKind.Create ? Strings.Get("switch.create", Name) : Name;

        /// <summary>
        /// What the right-hand column says. A worktree replaces "Local" rather than sitting beside it: a
        /// branch checked out elsewhere is not switchable, so "Local" would name the one thing this row
        /// cannot do.
        /// </summary>
        public string Kind => Worktree switch
        {
            //Distinguished because the two need opposite things done to them -- one is opened, the other is
            //pruned -- and because "missing" is the only honest word for a directory that is gone.
            { IsPrunable: true } => Strings.Get("worktree.kind.missing"),
            not null => Strings.Get("worktree.kind"),
            _ => BaseKind,
        };

        /// <summary>The worktree's path, for the row's tooltip. Null leaves the tooltip off entirely.</summary>
        public string? Hint => Worktree?.Path;

        /// <summary>
        /// The accent for the create row, resolved from the window's own resources rather than hard-coded
        /// so it follows the theme.
        /// </summary>
        public Brush Brush => Row == CandidateKind.Create
            ? (Brush)Application.Current.Resources["Accent"]
            : (Brush)Application.Current.Resources["Text"];

        public override string ToString() => $"{Display} {Kind}".TrimEnd();
    }
}
