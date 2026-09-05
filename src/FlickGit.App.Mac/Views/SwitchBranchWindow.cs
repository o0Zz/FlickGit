using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.Branches;
using FlickGit.Matching;
using FlickGit.Models;
using FlickGit.Worktrees;

namespace FlickGit.App.Mac.Views;

/// <summary>
/// Which branch to be on, and what else can be done to the row the user is pointing at.
///
/// <b>The safety rule is the whole of this window, and it lives in the buttons rather than in the
/// code.</b> A plain switch is attempted first; when Git refuses because of local changes, the
/// window does <i>not</i> stash automatically. It shows the blocking files and offers the choice —
/// CLAUDE.md is explicit that stashing on the user's behalf is not a thing this does, and
/// <see cref="SwitchService.StashSwitchRestoreAsync"/> exists precisely so the stash path is one
/// audited sequence in Core rather than three commands assembled here.
///
/// Create is the filter box itself: text matching no ref is a new branch name, validated by
/// <c>check-ref-format</c> through <see cref="BranchService"/> before anything runs.
///
/// <b>Worktrees live on these rows rather than in a window of their own</b>, because Git allows a
/// branch to be checked out in exactly one worktree — so "where is this branch" is a fact about the
/// row the user is already looking at. A row whose branch is checked out elsewhere turns the primary
/// button into <i>open that folder</i>, since a switch is the one thing Git would refuse there.
/// </summary>
internal sealed class SwitchBranchWindow : ReloadableWindow
{
    private readonly SwitchService _switches;
    private readonly BranchService _branches;
    private readonly WorktreeService _worktrees;
    private readonly RepositoryInfo _repository;

    private readonly List<Candidate> _candidates = [];

    private readonly TextBox _filter = new()
    {
        Margin = new Thickness(10, 10, 10, 6),
        PlaceholderText = Strings.Get("switch.filter.hint"),
    };

    private readonly ListBox _list = new() { Margin = new Thickness(10, 0) };
    private readonly ContextMenu _rowMenu = new();

    private readonly TextBlock _status = new()
    {
        Margin = new Thickness(10, 6),
        TextWrapping = TextWrapping.Wrap,
    };

    private readonly Button _primary = new() { MinWidth = 130, Classes = { "primary" } };
    private readonly Button _stashSwitch = new() { MinWidth = 190, IsVisible = false };
    private readonly Button _close = new() { MinWidth = 90 };

    /// <summary>
    /// The branch a refused switch was for, kept so the stash button acts on the same one rather
    /// than on whatever the list happens to highlight by the time it is pressed.
    /// </summary>
    private string? _pendingBranch;

    public SwitchBranchWindow(
        RepositoryInfo repository,
        SwitchService switches,
        BranchService branches,
        WorktreeService worktrees,
        string? currentBranch)
    {
        _repository = repository;
        _switches = switches;
        _branches = branches;
        _worktrees = worktrees;

        CurrentBranch = currentBranch;

        Title = Strings.Get("switch.title", repository.Name);
        Width = 620;
        Height = 540;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        _primary.Content = Strings.Get("switch.button");
        _stashSwitch.Content = Strings.Get("switch.stash");
        _close.Content = Strings.Get("common.close");

        _list.ItemTemplate = RowTemplate();
        _list.ContextMenu = _rowMenu;
        _list.ContextRequested += OnContextRequested;
        _list.DoubleTapped += (_, _) => _ = AcceptAsync();
        _list.SelectionChanged += (_, _) => UpdatePrimaryLabel();

        _filter.TextChanged += (_, _) => ApplyFilter();
        _filter.KeyDown += (_, e) => PickerList.RouteArrows(_list, e);

        _primary.Click += (_, _) => _ = AcceptAsync();
        _stashSwitch.Click += (_, _) => _ = StashSwitchAsync();
        _close.Click += (_, _) => Close();

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Children =
            {
                Row(_filter, 0),
                Row(_list, 1),
                Row(_status, 2),
                Row(Footer(), 3),
            },
        };

        Opened += (_, _) =>
        {
            _filter.Focus();
            _ = LoadAsync();
        };
    }

    /// <summary>
    /// The branch HEAD is on, or null when it is detached.
    ///
    /// Handed in rather than read here: the verb already has a status in hand, and a second
    /// <c>rev-parse</c> for a value it is holding is a process this window does not need to start.
    /// </summary>
    private string? CurrentBranch { get; }

    private Candidate? Selected => _list.SelectedItem as Candidate;

    private Control Footer() =>
        new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(10),
            Children = { _stashSwitch, _primary, _close },
        };

    protected override void SetBusy(bool busy)
    {
        IsBusy = busy;

        _primary.IsEnabled = !busy;
        _stashSwitch.IsEnabled = !busy;
        _list.IsEnabled = !busy;
        _filter.IsEnabled = !busy;
    }

    /// <summary>
    /// Both reads at once. The worktree list is one more process on a window with no latency target,
    /// and running it in parallel keeps it off the wall clock entirely.
    /// </summary>
    protected override async Task ReadStateAsync()
    {
        Task<SwitchCandidates> candidateTask = _switches.ListCandidatesAsync(_repository, ClosingToken);
        Task<IReadOnlyList<GitWorktree>> worktreeTask = _worktrees.ListAsync(_repository, ClosingToken);

        SwitchCandidates candidates = await candidateTask.ConfigureAwait(true);
        IReadOnlyList<GitWorktree> worktrees = await worktreeTask.ConfigureAwait(true);

        //Keyed by branch because Git allows at most one worktree per branch, which is what makes a
        //dictionary the right shape rather than a list to search. Detached and bare worktrees have no
        //branch and so are absent: there is no row for them here.
        Dictionary<string, GitWorktree> byBranch = worktrees
            .Where(w => w.Branch is { Length: > 0 })
            .GroupBy(w => w.Branch!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        _candidates.Clear();

        foreach (string branch in candidates.Local)
        {
            bool current = string.Equals(branch, CurrentBranch, StringComparison.Ordinal);

            byBranch.TryGetValue(branch, out GitWorktree? worktree);

            //The worktree this window was opened on is not "somewhere else", so it is dropped rather
            //than labelled. Compared against the resolved root rather than against IsMain, because
            //the window can perfectly well be opened *on* a linked worktree — in which case the main
            //one holds some other branch and is the row that needs the label.
            if (worktree is not null && IsHere(worktree))
                worktree = null;

            _candidates.Add(new Candidate(
                branch,
                //The current branch is labelled rather than hidden: seeing it in the list is what
                //tells the user where they are.
                current ? Strings.Get("branch.current") : Strings.Get("switch.local"),
                current ? CandidateKind.Current : CandidateKind.Local,
                worktree));
        }

        foreach (string branch in candidates.Remote)
            _candidates.Add(new Candidate(branch, Strings.Get("switch.remote"), CandidateKind.Remote));

        ApplyFilter();
    }

    private bool IsHere(GitWorktree worktree) =>
        string.Equals(worktree.Path, _repository.Root, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Local branches keep their ordering advantage over remote ones even under a fuzzy match:
    /// switching to a local branch is the common case, and a remote match that scored a point higher
    /// should not outrank it.
    /// </summary>
    private void ApplyFilter()
    {
        string pattern = (_filter.Text ?? string.Empty).Trim();

        IReadOnlyList<FuzzyMatch> ranked = FuzzyMatcher.Rank(
            _candidates.Select(c => c.Name),
            pattern,
            recencyRank: name => _candidates.First(c => c.Name == name).IsRemote ? 8 : 0);

        var matches = ranked
            .Select(m => _candidates.First(c => c.Name == m.Value))
            .ToList();

        //The create row, when what has been typed is a branch name that does not exist yet. Last
        //rather than first, so Enter on a filter that also matches something keeps switching to the
        //match.
        if (CreateCandidate(pattern) is { } create)
            matches.Add(create);

        _list.ItemsSource = matches;
        _list.SelectedIndex = matches.Count > 0 ? 0 : -1;

        _status.Text = matches.Count == 0 ? Strings.Get("switch.none") : string.Empty;
        _primary.IsEnabled = matches.Count > 0;

        UpdatePrimaryLabel();
    }

    /// <summary>
    /// The synthetic "create this branch" row, or null when the text is not a name to create: an
    /// empty box, a name Git would refuse, or one that already exists locally.
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
    /// Keeps the primary button honest about what it will do.
    ///
    /// On a row whose branch is checked out in another worktree the button opens a folder, and Git
    /// would refuse a switch outright — so a button still reading "Switch" would be naming the one
    /// thing that row cannot do.
    /// </summary>
    private void UpdatePrimaryLabel() =>
        _primary.Content = Selected switch
        {
            { Worktree: { IsPrunable: false } } => Strings.Get("worktree.open"),
            { Row: CandidateKind.Create } create => Strings.Get("switch.create", create.Name),
            _ => Strings.Get("switch.button"),
        };

    /// <summary>
    /// The rows the menu is about, then the items that actually apply to them.
    ///
    /// Built on opening rather than declared, because which items are meaningful depends entirely on
    /// the row: the current branch is deletable by nothing, a create row names a branch that does not
    /// exist, and a row with a worktree can be opened but not switched to.
    /// </summary>
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!PickerList.SelectRowUnderPointer(_list, e.Source))
        {
            e.Handled = true;

            return;
        }

        //Nothing to offer for the create row, and nothing for the current branch: it names a branch
        //that does not exist yet, and the one you are standing on is deletable by nothing, here or in
        //Git. Omitted rather than greyed out.
        if (Selected is not { } candidate || candidate.Row is CandidateKind.Create or CandidateKind.Current)
        {
            e.Handled = true;

            return;
        }

        var items = new List<Control>();

        //The worktree items come first: on a row that has one they are the only things that work,
        //since Git refuses to switch to a branch checked out elsewhere and refuses to delete it too.
        if (candidate.Worktree is { } worktree)
        {
            if (worktree.IsPrunable)
            {
                items.Add(PickerList.Item(
                    Strings.Get("worktree.menu.prune"),
                    () => PruneAsync(candidate.Name, worktree)));
            }
            else
            {
                items.Add(PickerList.Item(
                    Strings.Get("worktree.menu.open"),
                    () => OpenFolder(worktree.Path)));

                items.Add(PickerList.Item(
                    Strings.Get("worktree.menu.remove"),
                    () => RemoveWorktreeAsync(candidate.Name, worktree)));
            }

            _rowMenu.ItemsSource = items;

            return;
        }

        //Offered on every remaining row, local and remote alike. A remote row creates a local branch
        //tracking it, which is what switching to a remote row already does — so this is not a hole
        //the user has to work around by switching first.
        items.Add(PickerList.Item(
            Strings.Get("worktree.menu.add"),
            () => AddWorktreeAsync(candidate)));

        items.Add(new Separator());

        if (candidate.Row == CandidateKind.Local)
        {
            items.Add(PickerList.Item(
                Strings.Get("switch.menu.delete"),
                () => DeleteLocalAsync(candidate.Name, force: false)));
        }
        else
        {
            //The remote's name is resolved when the menu opens rather than when it is clicked, so the
            //label can say where the deletion would land — "Delete on origin…" is a different promise
            //from "Delete…", and it is the one the user needs before pressing it.
            string label = candidate.Name.Split('/', 2) is [{ Length: > 0 } remote, _]
                ? Strings.Get("switch.menu.deleteremote", remote)
                : Strings.Get("switch.menu.delete");

            items.Add(PickerList.Item(label, () => DeleteRemoteAsync(candidate.Name)));
        }

        _rowMenu.ItemsSource = items;
    }

    private async Task AcceptAsync()
    {
        if (Selected is not { } candidate)
            return;

        switch (candidate.Row)
        {
            case CandidateKind.Create:
                await CreateAsync(candidate.Name).ConfigureAwait(true);

                return;

            case CandidateKind.Current:
                //Already there. Doing nothing is the correct switch.
                Close();

                return;
        }

        //Checked out in another worktree, where Git refuses a switch outright. Opening that folder is
        //the only thing this row can usefully do, so it is what the primary gesture does — rather
        //than running a command whose failure the user can do nothing about.
        if (candidate.Worktree is { } worktree)
        {
            if (worktree.IsPrunable)
            {
                //The directory is gone and Git still believes the branch is checked out there, which
                //is why every switch to it is refused. Pruning is the fix, and it is offered rather
                //than performed.
                await PruneAsync(candidate.Name, worktree).ConfigureAwait(true);

                return;
            }

            OpenFolder(worktree.Path);

            return;
        }

        await AttemptAsync(candidate).ConfigureAwait(true);
    }

    /// <summary>
    /// Creates the typed branch at the current commit and switches to it.
    ///
    /// From HEAD, not from whatever row happens to be highlighted. Creating from the selection would
    /// be a second, invisible meaning for a list whose whole job so far has been "where do I want to
    /// go".
    /// </summary>
    private Task CreateAsync(string name) =>
        RunBusyAsync(async () =>
        {
            //Git's own answer before anything is created, not just the offline check the row was
            //offered on: check-ref-format knows the rules this build enforces.
            BranchNameValidation validation = await _branches
                .ValidateAsync(_repository, name, CancellationToken.None)
                .ConfigureAwait(true);

            if (!validation.IsValid)
            {
                _status.Text = validation.Error ?? string.Empty;

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

            Report(Strings.Get("switch.create", name), outcome.GitError);
        });

    private Task AttemptAsync(Candidate candidate) =>
        RunBusyAsync(async () =>
        {
            //The plain switch first, always.
            SwitchOutcome outcome = candidate.IsRemote
                ? await _switches.SwitchTrackingAsync(_repository, candidate.Name, CancellationToken.None)
                    .ConfigureAwait(true)
                : await _switches.SwitchAsync(_repository, candidate.Name, CancellationToken.None)
                    .ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                Close();

                return;
            }

            if (outcome.RefusedByLocalChanges)
            {
                //Refused because of local changes. Nothing was modified or discarded, and the button
                //that offers the stash appears only for the refusal it actually answers.
                _pendingBranch = candidate.Name;

                _status.Text = Strings.Get("switch.blocked.hint")
                    + Environment.NewLine + Environment.NewLine
                    + string.Join(Environment.NewLine, outcome.BlockingFiles);

                _stashSwitch.IsVisible = true;

                return;
            }

            //A failure a stash cannot fix. Reported in Git's own words, and no stash button.
            _stashSwitch.IsVisible = false;

            Report(Strings.Get("switch.button"), outcome.GitError);
        });

    private Task StashSwitchAsync()
    {
        if (_pendingBranch is not { } branch)
            return Task.CompletedTask;

        return RunBusyAsync(async () =>
        {
            SwitchOutcome outcome = await _switches
                .StashSwitchRestoreAsync(_repository, branch, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                Close();

                return;
            }

            //The one outcome that must never be reported vaguely: the switch happened and the user's
            //work is sitting in a stash. The reference is the actionable part.
            string message = outcome.RestoreConflicted && outcome.StashRef is not null
                ? $"{outcome.GitError}{Environment.NewLine}{Environment.NewLine}"
                    + Strings.Get("switch.stashkept", outcome.StashRef)
                : outcome.GitError ?? string.Empty;

            MessageWindow.Notice(Strings.Get("switch.stash"), message);

            if (outcome.RestoreConflicted)
                Close();
        });
    }

    /// <summary>
    /// Deletes a local branch, asking first — and asking a second time before ever forcing.
    ///
    /// <paramref name="force"/> is only ever true on the recursive call this method makes after Git
    /// has refused an unmerged branch and the user has answered the question naming that fact. Two
    /// different questions, neither remembered.
    /// </summary>
    private async Task DeleteLocalAsync(string name, bool force)
    {
        if (!force && !await MessageWindow.AskAsync(
                Strings.Get("branch.delete.title"),
                Strings.Get("branch.delete.local", name),
                Strings.Get("branch.delete.yes"),
                Strings.Get("common.cancel"),
                destructive: true).ConfigureAwait(true))
        {
            return;
        }

        bool forceNext = false;

        await RunBusyAsync(async () =>
        {
            BranchDeleteOutcome outcome = await _branches
                .DeleteLocalAsync(_repository, name, CurrentBranch, force, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                await LoadAsync().ConfigureAwait(true);
                _status.Text = Strings.Get("branch.deleted", name);

                return;
            }

            if (outcome.WasCurrentBranch)
            {
                //Refused before any command ran, so there is nothing to report but the reason.
                _status.Text = Strings.Get("branch.delete.current", name);

                return;
            }

            if (outcome.NotMerged)
            {
                //The one refusal with a way forward. The question names what is at stake rather than
                //repeating Git's hint, and answering it is the only route to -D.
                forceNext = await MessageWindow.AskAsync(
                    Strings.Get("branch.delete.title"),
                    Strings.Get("branch.delete.unmerged", name),
                    Strings.Get("branch.delete.force"),
                    Strings.Get("common.cancel"),
                    destructive: true).ConfigureAwait(true);

                return;
            }

            Report(Strings.Get("branch.delete.title"), outcome.GitError);
        }).ConfigureAwait(true);

        //Outside the busy scope rather than inside it: the recursive call takes the flag again, and a
        //nested SetBusy(false) in its finally would unlock the window while it is still working.
        if (forceNext)
            await DeleteLocalAsync(name, force: true).ConfigureAwait(true);
    }

    /// <summary>
    /// Deletes a branch on its remote — the one operation in FlickGit that destroys something other
    /// people share, and the only one with no local undo.
    ///
    /// The remote is resolved against the configured remotes before anything is asked, so a row whose
    /// prefix is not a remote is refused here rather than becoming a push at a remote that does not
    /// exist.
    /// </summary>
    private Task DeleteRemoteAsync(string remoteTrackingName) =>
        RunBusyAsync(async () =>
        {
            RemoteBranch? target = await _branches
                .ResolveRemoteBranchAsync(_repository, remoteTrackingName, CancellationToken.None)
                .ConfigureAwait(true);

            if (target is null)
            {
                _status.Text = Strings.Get("branch.noremote", remoteTrackingName);

                return;
            }

            if (!await MessageWindow.AskAsync(
                    Strings.Get("branch.delete.title"),
                    Strings.Get("branch.delete.remote", target.Branch, target.Remote),
                    Strings.Get("branch.delete.yes"),
                    Strings.Get("common.cancel"),
                    destructive: true).ConfigureAwait(true))
            {
                return;
            }

            BranchDeleteOutcome outcome = await _branches
                .DeleteRemoteAsync(_repository, target.Remote, target.Branch, CancellationToken.None)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                Report(Strings.Get("branch.delete.title"), outcome.GitError);

                return;
            }

            //`push --delete` removes the remote-tracking ref as well, so re-listing is what makes the
            //row disappear. Nothing here prunes anything by hand.
            await LoadAsync().ConfigureAwait(true);
            _status.Text = Strings.Get("branch.deleted.remote", target.Branch, target.Remote);
        });

    /// <summary>
    /// Creates a worktree for the clicked branch: a second checkout of this repository, in its own
    /// folder, on its own branch.
    ///
    /// <b>Not confirmed, and it needs no confirmation.</b> Nothing existing is touched — it makes a
    /// directory and checks a branch out into it, which is the only Git operation reachable from this
    /// window that cannot lose anything.
    ///
    /// The picker chooses the <i>parent</i> and the leaf is derived from the repository and branch
    /// names, which is what keeps the whole interaction one dialog.
    /// </summary>
    private async Task AddWorktreeAsync(Candidate candidate)
    {
        //What to check out, and under what local name. A remote row creates a local branch tracking
        //it — the same thing switching to a remote row does — unless that branch already exists here,
        //in which case there is nothing to create and `--track -b` would fail on the name.
        string branch = candidate.Name;
        WorktreeStart start;

        if (candidate.IsRemote)
        {
            RemoteBranch? resolved = await _branches
                .ResolveRemoteBranchAsync(_repository, candidate.Name, CancellationToken.None)
                .ConfigureAwait(true);

            if (resolved is null)
            {
                _status.Text = Strings.Get("branch.noremote", candidate.Name);

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
        IReadOnlyList<IStorageFolder> picked = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = Strings.Get("worktree.pick", branch),
                AllowMultiple = false,
                SuggestedStartLocation = await StorageProvider
                    .TryGetFolderFromPathAsync(ParentOfRepository())
                    .ConfigureAwait(true),
            }).ConfigureAwait(true);

        if (picked is not [{ } folder] || folder.Path.LocalPath is not { Length: > 0 } parent)
            return;

        string path = Path.Combine(parent, WorktreeService.SuggestFolderName(_repository.Name, branch));

        await RunBusyAsync(async () =>
        {
            WorktreeOutcome outcome = await _worktrees
                .AddAsync(_repository, path, start, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                //Re-listed rather than patched, so the new row comes from `worktree list` like every
                //other — there is no second place for what a worktree row says to be decided.
                await LoadAsync().ConfigureAwait(true);
                _status.Text = Strings.Get("worktree.created", path);

                return;
            }

            if (outcome.Refusal != WorktreeRefusal.None)
            {
                _status.Text = RefusalText(outcome.Refusal, path);

                return;
            }

            Report(Strings.Get("worktree.menu.add"), outcome.GitError);
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Removes a worktree, asking once.
    ///
    /// <b>There is no second question and no forced spelling</b>, which is the one place this
    /// window's worktree items depart from its branch ones. <c>git worktree remove --force</c>
    /// deletes modified and untracked files outright — no reflog, and no Trash, because nothing in
    /// Git has ever seen them. So a dirty worktree is reported with the two ways out that destroy
    /// nothing, and forcing stays something the user types themselves.
    /// </summary>
    private async Task RemoveWorktreeAsync(string branch, GitWorktree worktree)
    {
        if (!await MessageWindow.AskAsync(
                Strings.Get("worktree.remove.title"),
                Strings.Get("worktree.remove.ask", branch, worktree.Path),
                Strings.Get("worktree.remove.yes"),
                Strings.Get("common.cancel"),
                destructive: true).ConfigureAwait(true))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            WorktreeOutcome outcome = await _worktrees
                .RemoveAsync(_repository, worktree, CancellationToken.None)
                .ConfigureAwait(true);

            if (outcome.Succeeded)
            {
                await LoadAsync().ConfigureAwait(true);
                _status.Text = Strings.Get("worktree.removed", worktree.Path);

                return;
            }

            if (outcome.Refusal != WorktreeRefusal.None)
            {
                _status.Text = RefusalText(outcome.Refusal, worktree.Path);

                return;
            }

            //Git refused because there is work in there. Reported with what to do about it rather
            //than with a button that would delete it.
            MessageWindow.Notice(
                Strings.Get("worktree.remove.title"),
                outcome.HasLocalChanges
                    ? Strings.Get("worktree.remove.dirty", worktree.Path)
                    : outcome.GitError ?? string.Empty);
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Drops Git's bookkeeping for worktrees whose folder is gone — the state a user reaches by
    /// deleting a worktree in Finder, and the only one in this feature they cannot otherwise get out
    /// of: until it is pruned, Git still believes the branch is checked out and refuses every switch
    /// to it, naming a directory that does not exist.
    ///
    /// Confirmed because it is repository-wide rather than about the one row that was clicked, and
    /// there is no such thing as pruning a single entry. Nothing on disk is destroyed: a worktree
    /// that still exists is not prunable, whatever state it is in.
    /// </summary>
    private async Task PruneAsync(string branch, GitWorktree worktree)
    {
        if (!await MessageWindow.AskAsync(
                Strings.Get("worktree.prune.title"),
                Strings.Get("worktree.prune.ask", branch, worktree.Path),
                Strings.Get("worktree.prune.yes"),
                Strings.Get("common.cancel"),
                destructive: true).ConfigureAwait(true))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            WorktreeOutcome outcome = await _worktrees
                .PruneAsync(_repository, CancellationToken.None)
                .ConfigureAwait(true);

            if (!outcome.Succeeded)
            {
                Report(Strings.Get("worktree.prune.title"), outcome.GitError);

                return;
            }

            await LoadAsync().ConfigureAwait(true);
            _status.Text = Strings.Get("worktree.pruned", branch);
        }).ConfigureAwait(true);
    }

    /// <summary>
    /// Shows a worktree in Finder.
    ///
    /// Finder rather than an editor: which editor the user wants is a guess, and a folder window is
    /// the one answer that is right on every machine. From there the folder is an ordinary
    /// repository — <c>flick commit</c> and the Finder menu work in it, because
    /// <c>RepositoryService</c> asks Git for the root rather than looking for a <c>.git</c> directory.
    /// </summary>
    private void OpenFolder(string path) =>
        _status.Text = ShellOpen.Folder(path) is null
            ? Strings.Get("worktree.opened", path)
            : Strings.Get("worktree.openfailed", path);

    private string ParentOfRepository() =>
        Path.GetDirectoryName(_repository.Root.TrimEnd('/', '\\')) ?? _repository.Root;

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

        //NotAbsolute is unreachable from here — the path is built from a folder picker's answer — but
        //a silent empty status line would be worse than naming it.
        _ => Strings.Get("worktree.refused.path", path),
    };

    /// <summary>Reports a failure in Git's own words. Never a generic sentence.</summary>
    private void Report(string title, string? gitError) =>
        MessageWindow.Notice(title, string.IsNullOrWhiteSpace(gitError) ? title : gitError);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        //Enter before the base, which owns F5 and Esc. Handled here rather than through a default
        //button, because what Enter does depends on the row: switch, create, open a folder, or prune.
        if (e.Key is Key.Enter or Key.Return && !IsBusy)
        {
            e.Handled = true;
            _ = AcceptAsync();

            return;
        }

        base.OnKeyDown(e);
    }

    private static FuncDataTemplate<Candidate> RowTemplate() =>
        new((candidate, _) => new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Thickness(6, 3),
            Children =
            {
                Column(
                    new TextBlock
                    {
                        Text = candidate.Name,
                        FontFamily = new FontFamily("monospace"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    0),

                //Where the branch is checked out, when that is somewhere else. The reason the primary
                //button on this row says "Open folder" rather than "Switch", so it has to be visible
                //on the row rather than only in the menu.
                Column(
                    new TextBlock
                    {
                        Text = candidate.Worktree is { } worktree
                            ? (worktree.IsPrunable
                                ? Strings.Get("worktree.kind.missing")
                                : worktree.Path)
                            : string.Empty,
                        Opacity = 0.55,
                        Margin = new Thickness(10, 0),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    1),

                Column(new TextBlock { Text = candidate.Kind, Opacity = 0.7 }, 2),
            },
        });

    private static T Row<T>(T control, int row)
        where T : Control
    {
        control.SetValue(Grid.RowProperty, row);

        return control;
    }

    private static T Column<T>(T control, int column)
        where T : Control
    {
        control.SetValue(Grid.ColumnProperty, column);

        return control;
    }

    /// <summary>
    /// Which kind of row this is. An enum rather than a pair of bools because the four cases are
    /// mutually exclusive and each one answers the primary gesture differently.
    /// </summary>
    private enum CandidateKind
    {
        Local,
        Current,
        Remote,
        Create,
    }

    private sealed record Candidate(
        string Name,
        string Kind,
        CandidateKind Row,
        GitWorktree? Worktree = null)
    {
        public bool IsRemote => Row == CandidateKind.Remote;
    }
}
