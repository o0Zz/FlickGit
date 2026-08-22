using System.Collections.ObjectModel;
using FlickGit.App.CommandLine;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.App.Settings;
using FlickGit.Branches;
using FlickGit.Commits;
using FlickGit.Diff;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Status;

namespace FlickGit.App.ViewModels;

/// <summary>
/// The commit window's state and behaviour. No Git logic of its own.
///
/// Everything it shares with the quick-commit popup — the branch ComboBox, the warning strip, the
/// guardrail consent and the commit itself — is <see cref="CommitSurface"/>. What is left here is
/// what only the window has: the file list, the diff cache and its prefetch, and live editing.
/// </summary>
public sealed class CommitViewModel : CommitSurface
{
    private readonly StatusService _status;
    private readonly DiffCache _diffs;
    private readonly CommitService _commits;
    private readonly PatchService _patches;
    private readonly WorkingTreeWriter _writer;

    private FileChangeItem? _selectedFile;
    private SideBySideDiff? _currentDiff;
    private bool _isDiffLoading;

    public CommitViewModel(
        RepositoryInfo repository,
        StatusService status,
        DiffCache diffs,
        CommitService commits,
        BranchService branches,
        CommitFlow flow,
        UpstreamConsent consent,
        PatchService patches,
        WorkingTreeWriter writer,
        FlickSettings settings,
        ILog log)
        : base(repository, status, branches, flow, consent, settings, log)
    {
        _status = status;
        _diffs = diffs;
        _diffs.Reset(repository);
        _commits = commits;
        _patches = patches;
        _writer = writer;

        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy, ReportError);
        SelectAllCommand = new RelayCommand(() => SetAllSelected(true));
        SelectNoneCommand = new RelayCommand(() => SetAllSelected(false));
    }

    public AsyncCommand RefreshCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }

    public ObservableCollection<FileChangeItem> Files { get; } = [];

    public string Title => Strings.Get("commit.title", Repository.Name);

    public string AheadBehindText =>
        CurrentStatus?.Upstream is null ? string.Empty : $"↑{CurrentStatus.Ahead} ↓{CurrentStatus.Behind}";

    public string SelectionText => Strings.Get("commit.summary.selected", Files.Count(f => f.IsSelected));

    public FileChangeItem? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (!Set(ref _selectedFile, value))
                return;

            _ = LoadDiffAsync(value);
        }
    }

    public SideBySideDiff? CurrentDiff
    {
        get => _currentDiff;

        //The comparison label CLAUDE.md requires in the diff header is the pane's own: it has the
        //diff in front of it, and a second copy here could disagree about what the user is looking
        //at -- which is the confusion that section exists to prevent.
        private set => Set(ref _currentDiff, value);
    }

    public bool IsDiffLoading
    {
        get => _isDiffLoading;
        private set => Set(ref _isDiffLoading, value);
    }

    public string DiffFontFamily => Settings.DiffFontFamily;

    public double DiffFontSize => Settings.DiffFontSize;

    public override void Reset(RepositoryInfo repository)
    {
        _diffs.Reset(repository);
        _selectedFile = null;
        Files.Clear();
        CurrentDiff = null;
        IsDiffLoading = false;

        base.Reset(repository);

        Raise(nameof(Title));
        Raise(nameof(SelectedFile));
        Raise(nameof(AheadBehindText));
        Raise(nameof(SelectionText));
    }

    /// <summary>
    /// Takes a status somebody else already fetched, along with what they had typed.
    ///
    /// This is the popup's <c>Details…</c> handoff: it has just run the same three Git processes for
    /// its own summary, and running them again to fill this window is the difference between the
    /// 60 ms CLAUDE.md budgets for the handoff and another 100 ms of Git.
    /// </summary>
    public void Adopt(RepositoryStatus status, string? message, string? branchInput)
    {
        //Assigned first, because adopting the status reads BranchInput to decide whether to overwrite
        //the ComboBox with the current branch. Setting them afterwards would lose the branch the user
        //had typed in the popup.
        if (message is not null)
            Message = message;

        if (branchInput is { Length: > 0 })
            BranchInput = branchInput;

        Adopt(status);
    }

    public override void Adopt(RepositoryStatus status)
    {
        //The tick boxes the user already set, kept across a refresh. Losing them would make Refresh
        //actively hostile.
        var previousSelection = Files.ToDictionary(f => f.Path, f => f.IsSelected, StringComparer.Ordinal);
        var previousHunks = Files
            .Where(f => f.Change.HasChosenHunks)
            .Select(f => f.Path)
            .ToHashSet(StringComparer.Ordinal);

        string? previouslySelectedPath = _selectedFile?.Path;

        //Rebuilt before the base adopts the status, because the base recomputes the command states
        //and CanCommit counts the ticked files.
        Files.Clear();

        foreach (GitFileChange change in status.Files)
        {
            if (previousSelection.TryGetValue(change.Path, out bool wasSelected))
                change.IsSelected = wasSelected;

            //Kept only while the index still holds something of the file: after a commit it does not,
            //and the choice is spent.
            change.HasChosenHunks = previousHunks.Contains(change.Path) && change.IndexStatus != GitChangeType.None;

            var item = new FileChangeItem(change);
            item.SelectionChanged += OnFileSelectionChanged;
            Files.Add(item);
        }

        base.Adopt(status);

        Raise(nameof(AheadBehindText));
        Raise(nameof(SelectionText));

        _selectedFile = Files.FirstOrDefault(f => f.Path == previouslySelectedPath) ?? Files.FirstOrDefault();
        Raise(nameof(SelectedFile));

        if (_selectedFile is not null)
            _ = LoadDiffAsync(_selectedFile);
        else
            CurrentDiff = null;

        //Started, not awaited: a click on one of the top five files is then a cache hit.
        _ = _diffs.PrefetchAsync(Files.Take(5).Select(f => f.Change).ToArray());
    }

    protected override void RaiseCommandStates()
    {
        base.RaiseCommandStates();
        RefreshCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Called by the window when it closes, so a running diff does not outlive it.</summary>
    public void Cancel() => _diffs.Cancel();

    private void OnFileSelectionChanged()
    {
        Raise(nameof(SelectionText));
        RaiseCommandStates();
    }

    private void SetAllSelected(bool selected)
    {
        foreach (FileChangeItem file in Files)
        {
            //Select-all still refuses conflicted files. A commit containing conflict markers is the
            //one outcome this window must never produce by accident.
            if (selected && file.IsConflicted)
                continue;

            file.IsSelected = selected;
        }
    }

    // ---- diff ---------------------------------------------------------------------

    /// <summary>
    /// Shows the diff for <paramref name="file"/>, from the cache when it is there.
    ///
    /// A cache hit renders with no loading flicker, which is most of them once the prefetch has run.
    /// A miss may be superseded before it finishes, which is why the result is checked against the
    /// still-selected row before it is displayed.
    /// </summary>
    private async Task LoadDiffAsync(FileChangeItem? file)
    {
        if (file is null)
        {
            _diffs.Cancel();
            CurrentDiff = null;
            return;
        }

        if (_diffs.Cached(file.Path) is { } cached)
        {
            CurrentDiff = cached;
            return;
        }

        IsDiffLoading = true;

        try
        {
            SideBySideDiff? diff = await _diffs.GetAsync(file.Change).ConfigureAwait(true);

            //The user may have moved on while this ran. Painting a diff for a row that is no longer
            //selected is worse than not painting one.
            if (_selectedFile == file)
                CurrentDiff = diff;
        }
        finally
        {
            IsDiffLoading = false;
        }
    }

    // ---- editing ------------------------------------------------------------------

    /// <summary>
    /// Saves the edited text of the currently selected file.
    ///
    /// Every guard lives in <see cref="WorkingTreeWriter"/>; this only decides what to do with a
    /// refusal. An externally-modified file comes back as a refusal the user has to answer, which is
    /// the point — CLAUDE.md, "Detect external modification before writing."
    /// </summary>
    public async Task<SaveOutcome> SaveCurrentFileAsync(string newText, bool force)
    {
        if (_currentDiff is null || _selectedFile is null)
            return SaveOutcome.Refused(SaveRefusal.Missing, "No file is open.");

        SaveOutcome outcome = await _writer.SaveAsync(
            Repository.Root,
            _currentDiff.Path,
            _currentDiff.Right,
            newText,
            force,
            CancellationToken.None).ConfigureAwait(true);

        if (!outcome.Succeeded)
            return outcome;

        Log.Info($"Saved {_currentDiff.Path}.");

        //The cached diff is stale the moment the file changes, and the cache is keyed by path alone
        //-- so it has to be dropped here or the next click would render the pre-save text.
        _diffs.Invalidate(_currentDiff.Path);

        //Only this file's counts are refreshed, not the whole list. CLAUDE.md: "After a successful
        //save, refresh that file's counts and re-run its diff. Do not refresh the whole status list."
        CurrentDiff = _currentDiff with { Right = outcome.Saved! };
        StatusText = Strings.Get("edit.saved");

        _ = RefreshSelectedFileCountsAsync();

        return outcome;
    }

    /// <summary>
    /// Re-runs status for the whole repository but applies only the selected file's row.
    ///
    /// A full <see cref="Adopt"/> would rebuild the list and lose the diff pane's scroll position
    /// mid-edit, which is exactly what a save must not do.
    /// </summary>
    private async Task RefreshSelectedFileCountsAsync()
    {
        if (_selectedFile is null)
            return;

        try
        {
            RepositoryStatus refreshed = await _status
                .GetStatusAsync(Repository, CancellationToken.None)
                .ConfigureAwait(true);

            var byPath = refreshed.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);

            //A file appeared or disappeared while this one was being edited, so the list itself is
            //stale and only a full adopt can put it right. It rebuilds the rows, which is what this
            //method otherwise exists to avoid -- but a list missing a row is worse than a lost scroll
            //position.
            if (byPath.Count != Files.Count || !Files.All(f => byPath.ContainsKey(f.Path)))
            {
                Adopt(refreshed);
                return;
            }

            //A chosen-hunks flag is only true while the index still holds part of this file. After a
            //commit it does not, so the flag drops here rather than needing to be cleared by whoever
            //committed -- which is one fewer thing to remember and self-correcting if it is forgotten.
            foreach (FileChangeItem item in Files)
            {
                if (item.Change.HasChosenHunks && byPath[item.Path].IndexStatus == GitChangeType.None)
                    item.Change.HasChosenHunks = false;
            }

            //Every row, not only the edited one.
            //
            //The tick state lives on the status's own change objects, and the commit is built from
            //the status -- so a row left pointing at the previous status's object would take the
            //user's ticks with it into an object nothing reads. The window would show one selection
            //and commit another. Update carries each row's tick across as it repoints it.
            //
            //Updated in place rather than replaced in the collection: replacing the selected item
            //makes the list's two-way SelectedItem binding push a null back through SelectedFile,
            //which closes the diff the user is editing.
            foreach (FileChangeItem item in Files)
                item.Update(byPath[item.Path]);

            AdoptCounts(refreshed);
        }
        catch (Exception ex)
        {
            Log.Debug($"Post-save refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-stages the currently selected file, for the "this file is staged" strip.
    ///
    /// CLAUDE.md: "When the user edits a file that is already staged, show an inline strip… with a
    /// one-click restage."
    /// </summary>
    public async Task RestageCurrentFileAsync()
    {
        if (_selectedFile is null)
            return;

        await _commits.StageAsync(Repository, [_selectedFile.Path], CancellationToken.None).ConfigureAwait(true);

        StatusText = Strings.Get("edit.restaged");
        await RefreshSelectedFileCountsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Stages or unstages part of the selected file.
    ///
    /// The patch is built from the diff on screen and applied to the index with
    /// <c>git apply --cached</c>, which never touches the working tree — so whatever happens, the
    /// file on disk and the editor holding it are unchanged.
    ///
    /// On success the file is marked <see cref="GitFileChange.HasChosenHunks"/>, which is what stops
    /// the commit sequence from running <c>git add</c> over it and swallowing the hunks the user left
    /// out. Without that this feature would appear to work and then quietly commit the whole file.
    /// </summary>
    /// <returns>A sentence for the footer, or null when there was nothing to do.</returns>
    public async Task<string?> StageHunkAsync(IReadOnlySet<int> rows, bool unstage)
    {
        if (_selectedFile is null || _currentDiff is null)
            return null;

        //Built from the rows the pane selected, with each line re-terminated from the file it came
        //from -- see Hunks, where the line-ending rule is the whole difficulty.
        string? patch = Hunks.ToPatch(
            _currentDiff.Path,
            _currentDiff.Rows,
            rows,
            _currentDiff.Left,
            _currentDiff.Right);

        if (patch is null)
            return null;

        PatchResult result = unstage
            ? await _patches.UnstageAsync(Repository, patch, CancellationToken.None).ConfigureAwait(true)
            : await _patches.StageAsync(Repository, patch, CancellationToken.None).ConfigureAwait(true);

        if (!result.Succeeded)
        {
            //Git's own words. The usual cause is an index that moved since the diff was computed,
            //and git apply refuses the whole patch rather than applying half of it.
            RaiseError(Strings.Get("hunk.failed"), result.Error ?? string.Empty);
            return null;
        }

        //Only meaningful while something of this file is still staged; a later refresh drops the flag
        //when the index no longer holds anything, which is what makes it self-correcting after a
        //commit.
        _selectedFile.Change.HasChosenHunks = !unstage;

        int changed = rows.Count(row => row >= 0 && row < _currentDiff.Rows.Count);
        StatusText = Strings.Get(unstage ? "hunk.unstaged" : "hunk.staged", changed);

        //Only this file's row, not the whole list: the diff pane is showing it and a rebuild would
        //lose the caret.
        await RefreshSelectedFileCountsAsync().ConfigureAwait(true);

        return StatusText;
    }

    /// <summary>Reloads the selected file from disk, discarding the editor's contents.</summary>
    public async Task<SideBySideDiff?> ReloadCurrentFileAsync()
    {
        if (_selectedFile is null)
            return null;

        _diffs.Invalidate(_selectedFile.Path);
        await LoadDiffAsync(_selectedFile).ConfigureAwait(true);
        return _currentDiff;
    }

    // ---- commit -------------------------------------------------------------------

    protected override async Task ApplyAsync(CommitFlowResult result)
    {
        //The commit moved HEAD, so every cached diff was computed against the wrong base.
        if (result.Commit is not null)
            _diffs.Clear();

        if (result.Outcome == CommitFlowOutcome.Committed)
        {
            RaiseCommitted(result.Commit!);
        }
        else if (CommitOutcomeReporter.FailureText(result) is { } failure)
        {
            //Adopted before the message is shown for an aborted switch, so the user is looking at the
            //state the message describes.
            if (result.Outcome == CommitFlowOutcome.AbortedSelectionChanged && result.RefreshedStatus is { } refreshed)
                Adopt(refreshed);

            RaiseError(failure.Title, failure.Message);
        }

        //Always, including after a failure: the repository moved and the list on screen is stale. The
        //aborted-switch case is excluded because it has just adopted the refreshed status above.
        if (result.Outcome != CommitFlowOutcome.AbortedSelectionChanged)
            await RefreshAsync().ConfigureAwait(true);
    }
}
