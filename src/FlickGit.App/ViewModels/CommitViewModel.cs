using System.Collections.ObjectModel;
using FlickGit.App.Ai;
using FlickGit.App.CommandLine;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.App.Settings;
using FlickGit.Branches;
using FlickGit.Commits;
using FlickGit.Diff;
using FlickGit.Git;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Status;

namespace FlickGit.App.ViewModels;

/// <summary>
/// The commit window's state and behaviour. No Git logic of its own.
///
/// <b>Reuse is the correctness risk here.</b> The resident service keeps one instance alive for
/// the whole session, so <see cref="Reset"/> must leave nothing behind from the previous
/// repository. Every mutable field declared below is assigned there -- one place to look when
/// adding a field.
/// </summary>
public sealed class CommitViewModel : ObservableObject
{
    private readonly StatusService _status;
    private readonly BranchService _branches;
    private readonly CommitFlow _flow;
    private readonly UpstreamConsent _consent;
    private readonly DiffCache _diffs;
    private readonly CommitService _commits;
    private readonly PatchService _patches;
    private readonly WorkingTreeWriter _writer;
    private readonly WorkingTreeDeleter _deleter;
    private readonly RestoreService _restore;
    private readonly AiTextService _messages;
    private readonly FlickSettings _settings;
    private readonly ILog _log;

    private RepositoryInfo _repository;
    private RepositoryStatus? _currentStatus;
    private string _message = string.Empty;
    private string _branchInput = string.Empty;
    private BranchResolution _branchResolution = new(BranchIntent.Empty, string.Empty);
    private string? _primaryBranch;
    private bool _isBusy;
    private string? _notice;
    private string? _statusText;

    private FileChangeItem? _selectedFile;
    private SideBySideDiff? _currentDiff;
    private bool _isDiffLoading;

    private CommitStage _stage;
    private bool _queuedPush;
    private bool _applyingStream;
    private CancellationTokenSource? _generation;

    /// <remarks>
    /// No repository parameter: the window is pre-warmed long before anybody right-clicks, and
    /// <see cref="Reset(RepositoryInfo)"/> is what points it at a folder.
    /// </remarks>
    public CommitViewModel(
        StatusService status,
        DiffCache diffs,
        CommitService commits,
        BranchService branches,
        CommitFlow flow,
        UpstreamConsent consent,
        PatchService patches,
        WorkingTreeWriter writer,
        WorkingTreeDeleter deleter,
        RestoreService restore,
        AiTextService messages,
        FlickSettings settings,
        ILog log)
    {
        _repository = RepositoryInfo.None;
        _status = status;
        _diffs = diffs;
        _commits = commits;
        _branches = branches;
        _flow = flow;
        _consent = consent;
        _patches = patches;
        _writer = writer;
        _deleter = deleter;
        _restore = restore;
        _messages = messages;
        _settings = settings;
        _log = log;

        CommitCommand = new AsyncCommand(() => CommitAsync(push: false), () => CanCommit, ReportError);
        CommitAndPushCommand = new AsyncCommand(() => CommitAsync(push: true), () => CanCommit, ReportError);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy, ReportError);
        SelectAllCommand = new RelayCommand(() => SetAllSelected(true));
        SelectNoneCommand = new RelayCommand(() => SetAllSelected(false));
        DeleteFileCommand = new AsyncCommand(DeleteSelectedFileAsync, () => CanDeleteFile, ReportError);
        RevertFileCommand = new AsyncCommand(RevertSelectedFileAsync, () => CanRevertFile, ReportError);

        //Replaces whatever is in the box, unlike the automatic pass when the window opens: the user
        //pressed a button labelled "generate".
        GenerateCommand = new RelayCommand(() => BeginGeneration(force: true), () => CanGenerate);
    }

    public AsyncCommand CommitCommand { get; }
    public AsyncCommand CommitAndPushCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand GenerateCommand { get; }

    public AsyncCommand DeleteFileCommand { get; }

    public AsyncCommand RevertFileCommand { get; }

    public ObservableCollection<FileChangeItem> Files { get; } = [];

    public ObservableCollection<string> Branches { get; } = [];

    public event Action<CommitResult>? Committed;

    public event Action<string, string>? ErrorRaised;

    public event Action? FocusMessageRequested;

    /// <summary>
    /// Asks the user a yes/no question and waits. A callback rather than a dialog call, because a
    /// view model must not construct windows.
    /// </summary>
    public Func<string, string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>
    /// Whether the diff pane holds an unsaved edit. Only the revert confirmation reads it: an edit
    /// that was never saved is not on disk, so it is not what goes to the Recycle Bin, and a dialog
    /// promising recoverability has to be right about what it is promising.
    /// </summary>
    public Func<bool>? IsEditorDirty { get; set; }

    public RepositoryInfo Repository
    {
        get => _repository;
        private set => Set(ref _repository, value);
    }

    public string RepositoryName => _repository.Name;

    public string Title => Strings.Get("commit.title", _repository.Name);

    public RepositoryStatus? CurrentStatus => _currentStatus;

    public string CurrentBranch => _currentStatus?.Branch ?? string.Empty;

    public string SummaryText =>
        _currentStatus is null
            ? string.Empty
            : Strings.Get("commit.summary.counts", _currentStatus.TrackedChangeCount, _currentStatus.UntrackedCount);

    public string AheadBehindText =>
        _currentStatus?.Upstream is null ? string.Empty : $"↑{_currentStatus.Ahead} ↓{_currentStatus.Behind}";

    public string SelectionText => Strings.Get("commit.summary.selected", Files.Count(f => f.IsSelected));

    public string Message
    {
        get => _message;
        set
        {
            if (!Set(ref _message, value))
                return;

            //A keystroke in the box means the user is taking over from the stream. Their text wins.
            if (!_applyingStream && _stage is CommitStage.Generating or CommitStage.Queued)
            {
                CancelGeneration();
                Stage = CommitStage.Idle;
                StatusText = null;
            }

            RaiseCommandStates();
        }
    }

    /// <summary>Free text: anything that is not an existing branch is a new branch name.</summary>
    public string BranchInput
    {
        get => _branchInput;
        set
        {
            if (!Set(ref _branchInput, value))
                return;

            //Resolved on every keystroke with no Git call: the branch list is already in memory and name
            //validity is an offline check.
            BranchResolution = BranchResolution.Resolve(value, CurrentBranch, Branches);
            RaiseCommandStates();
        }
    }

    public BranchResolution BranchResolution
    {
        get => _branchResolution;
        private set
        {
            if (Set(ref _branchResolution, value))
                Raise(nameof(BranchHint));
        }
    }

    public string BranchHint => BranchHintText.For(_branchResolution.Intent);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
                RaiseCommandStates();
        }
    }

    public string? Notice
    {
        get => _notice;
        private set
        {
            if (Set(ref _notice, value))
                Raise(nameof(HasNotice));
        }
    }

    public bool HasNotice => _notice is not null;

    public string? StatusText
    {
        get => _statusText;
        private set
        {
            if (Set(ref _statusText, value))
                Raise(nameof(HasStatusText));
        }
    }

    public bool HasStatusText => _statusText is not null;

    public CommitStage Stage
    {
        get => _stage;
        private set
        {
            if (Set(ref _stage, value))
            {
                Raise(nameof(CommitAndPushText));
                RaiseCommandStates();
            }
        }
    }

    /// <summary>
    /// The primary button's label, which is the whole of the queued-Enter feedback: pressing Enter
    /// during generation has to look like it did something.
    /// </summary>
    public string CommitAndPushText => _stage switch
    {
        CommitStage.Queued => Strings.Get("commit.queued"),
        CommitStage.Committing => Strings.Get("commit.committing"),
        _ => Strings.Get("commit.button.commitpush"),
    };

    /// <summary>
    /// The tick state lives on the status's own <c>GitFileChange</c> instances -- which is what
    /// <c>FileChangeItem</c> writes -- so this is the same question wherever it is asked from.
    /// </summary>
    public bool CanCommit =>
        _stage is not (CommitStage.Queued or CommitStage.Committing)
        && !_isBusy
        && _currentStatus is not null
        && _currentStatus.Files.Any(f => f.IsSelected)
        && !string.IsNullOrWhiteSpace(_message)
        && _branchResolution.IsCommittable
        && !_currentStatus.HasConflicts;

    public bool CanGenerate =>
        _messages.IsUsable
        && _stage is not (CommitStage.Queued or CommitStage.Committing)
        && _currentStatus is not null
        && _currentStatus.Files.Any(f => f.IsSelected);

    /// <summary>
    /// A row whose file is already gone -- deleted from the working tree, or removed with
    /// <c>git rm</c> -- is greyed out rather than offered and then refused. The <c>D</c> on the row
    /// already says the answer.
    /// </summary>
    public bool CanDeleteFile => !_isBusy && _selectedFile is { IsOnDisk: true };

    /// <summary>
    /// The reasons a file is not revertable are <see cref="RestoreService.CanRevert"/>'s, and they
    /// are all one reason: HEAD does not have this path.
    /// </summary>
    public bool CanRevertFile =>
        !_isBusy && _selectedFile is { } file && RestoreService.CanRevert(file.Change);

    /// <summary>
    /// False hides the button rather than showing a permanently dead one -- with no key stored there
    /// is nothing the user can do with it here, and Settings is where that is fixed.
    /// </summary>
    public bool IsAiConfigured => _messages.IsUsable;

    public bool CloseAfterCommit => _settings.CloseCommitWindowAfterSuccess;

    public FileChangeItem? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (!Set(ref _selectedFile, value))
                return;

            RaiseCommandStates();

            _ = LoadDiffAsync(value);
        }
    }

    public SideBySideDiff? CurrentDiff
    {
        get => _currentDiff;

        //The comparison label CLAUDE.md requires in the diff header is the pane's own: a second copy
        //here could disagree about what the user is looking at.
        private set => Set(ref _currentDiff, value);
    }

    public bool IsDiffLoading
    {
        get => _isDiffLoading;
        private set => Set(ref _isDiffLoading, value);
    }

    public string DiffFontFamily => _settings.DiffFontFamily;

    public double DiffFontSize => _settings.DiffFontSize;

    /// <summary>
    /// Clears everything, so the reused window shows nothing of the previous repository. A field
    /// added above and not cleared here is the leak this window's whole reuse story depends on.
    /// </summary>
    public void Reset(RepositoryInfo repository)
    {
        //A generation left running would stream the previous repository's message into this one.
        CancelGeneration();
        Stage = CommitStage.Idle;
        _queuedPush = false;
        _applyingStream = false;

        _diffs.Reset(repository);
        _selectedFile = null;
        Files.Clear();
        CurrentDiff = null;
        IsDiffLoading = false;

        Repository = repository;
        _currentStatus = null;
        _primaryBranch = null;
        Branches.Clear();

        Message = string.Empty;
        BranchInput = string.Empty;
        BranchResolution = new BranchResolution(BranchIntent.Empty, string.Empty);
        Notice = null;
        StatusText = null;
        IsBusy = false;

        Raise(nameof(Repository));
        Raise(nameof(RepositoryName));
        Raise(nameof(Title));
        Raise(nameof(CurrentStatus));
        Raise(nameof(CurrentBranch));
        Raise(nameof(SummaryText));
        Raise(nameof(SelectedFile));
        Raise(nameof(AheadBehindText));
        Raise(nameof(SelectionText));
        RaiseCommandStates();
    }

    /// <summary>
    /// Reads the status and adopts it. Deliberately not called from <see cref="Reset"/>: the window
    /// is shown between the two, and CLAUDE.md budgets the window appearing separately from its
    /// contents arriving.
    /// </summary>
    public async Task RefreshAsync()
    {
        IsBusy = true;

        try
        {
            RepositoryStatus status = await _status
                .GetStatusAsync(_repository, CancellationToken.None)
                .ConfigureAwait(true);

            Adopt(status);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The branch list and the primary-branch warning are started and not awaited: both are worth
    /// having, and worth nothing if waiting for them delays the window.
    /// </summary>
    public void Adopt(RepositoryStatus status)
    {
        //The tick boxes the user already set, kept across a refresh.
        var previousSelection = Files.ToDictionary(f => f.Path, f => f.IsSelected, StringComparer.Ordinal);
        var previousHunks = Files
            .Where(f => f.Change.HasChosenHunks)
            .Select(f => f.Path)
            .ToHashSet(StringComparer.Ordinal);

        string? previouslySelectedPath = _selectedFile?.Path;

        //Rebuilt before the command states are recomputed below, because CanCommit counts the ticked
        //files.
        Files.Clear();

        foreach (GitFileChange change in status.Files)
        {
            if (previousSelection.TryGetValue(change.Path, out bool wasSelected))
                change.IsSelected = wasSelected;

            //Kept only while the index still holds something of the file: after a commit it does not, and
            //the choice is spent.
            change.HasChosenHunks = previousHunks.Contains(change.Path) && change.IndexStatus != GitChangeType.None;

            var item = new FileChangeItem(change);
            item.SelectionChanged += OnFileSelectionChanged;
            Files.Add(item);
        }

        _currentStatus = status;

        //The ComboBox opens on the current branch, so committing without touching it involves no switch.
        if (_branchInput.Length == 0 && status.Branch is { Length: > 0 } branch)
            BranchInput = branch;
        else
            BranchResolution = BranchResolution.Resolve(_branchInput, status.Branch, Branches);

        Raise(nameof(CurrentStatus));
        Raise(nameof(CurrentBranch));
        Raise(nameof(SummaryText));
        Raise(nameof(AheadBehindText));
        Raise(nameof(SelectionText));
        UpdateNotice();
        RaiseCommandStates();

        _ = ResolvePrimaryBranchAsync();
        _ = LoadBranchesAsync();

        _selectedFile = Files.FirstOrDefault(f => f.Path == previouslySelectedPath) ?? Files.FirstOrDefault();
        Raise(nameof(SelectedFile));

        if (_selectedFile is not null)
            _ = LoadDiffAsync(_selectedFile);
        else
            CurrentDiff = null;

        //Started, not awaited: a click on one of the top five files is then a cache hit.
        _ = _diffs.PrefetchAsync(Files.Take(5).Select(f => f.Change).ToArray());
    }

    public void Cancel()
    {
        CancelGeneration();
        _diffs.Cancel();
    }

    /// <param name="force">
    /// True for the Generate button, which replaces whatever is in the box. False for the automatic
    /// pass when the window opens, where text already in the box means the user got there first.
    /// </param>
    public void BeginGeneration(bool force)
    {
        if (!CanGenerate)
            return;

        if (!force && Message.Length > 0)
            return;

        CancelGeneration();

        var generation = new CancellationTokenSource();
        _generation = generation;

        //Cleared through the stream flag, so emptying the box does not read as the user typing and
        //cancel the generation that is about to start.
        if (force)
            ApplyStreamedText(string.Empty);

        Stage = CommitStage.Generating;
        StatusText = Strings.Get("ai.generating");

        _ = RunGenerationAsync(generation);
    }

    /// <summary>
    /// Enter, and what it means right now. <b>During generation it queues rather than refusing</b> --
    /// which is what makes the one-key path work: trigger, Enter, done, without waiting to read
    /// anything.
    /// </summary>
    public void EnterPressed(bool push)
    {
        switch (_stage)
        {
            case CommitStage.Generating:
                _queuedPush = push;
                Stage = CommitStage.Queued;
                break;

            case CommitStage.Queued:
            case CommitStage.Committing:
                //Already under way. A second Enter is not a second commit.
                break;

            default:
                if (push)
                    CommitAndPushCommand.Execute(null);
                else
                    CommitCommand.Execute(null);

                break;
        }
    }

    /// <summary>
    /// Esc. <b>Closes the window</b>, whatever else is in flight -- generation starts on every open,
    /// so a generation that ate the first Esc would make the window look stuck for the first
    /// half-second of its life.
    ///
    /// Closing is safe with a generation or a queued commit outstanding: <c>OnClosed</c> calls
    /// <see cref="Cancel"/>, <see cref="RunGenerationAsync"/> then finds its own token source
    /// replaced and returns without committing, and a queued Enter cannot fire into a window that is
    /// gone.
    /// </summary>
    /// <returns>
    /// False only while a commit is actually executing: there is nothing to take back that would not
    /// leave the repository half-changed, and the window has to stay to report the outcome.
    /// </returns>
    public bool EscapePressed() => _stage != CommitStage.Committing;

    private async Task RunGenerationAsync(CancellationTokenSource generation)
    {
        GenerationOutcome outcome = await _messages
            .StreamCommitMessageAsync(
                _repository,
                _currentStatus!,
                ApplyStreamedText,
                generation.Token)
            .ConfigureAwait(true);

        //A newer generation started, or the window moved on.
        if (!ReferenceEquals(_generation, generation))
            return;

        _generation = null;
        generation.Dispose();

        if (!outcome.Succeeded)
        {
            bool wasQueued = _stage == CommitStage.Queued;

            Stage = CommitStage.Idle;
            StatusText = outcome.FailureReason;

            //CLAUDE.md: cancel the queue, focus the message box, keep it open. Never commit an empty or
            //placeholder message.
            if (wasQueued)
                FocusMessageRequested?.Invoke();

            return;
        }

        ApplyStreamedText(outcome.Message);
        StatusText = null;

        bool commitNow = _stage == CommitStage.Queued;
        Stage = CommitStage.Idle;

        if (!commitNow)
        {
            //The caret belongs at the end of what just arrived, so Enter commits it and typing appends.
            FocusMessageRequested?.Invoke();
            return;
        }

        //The queued Enter, cashed in. CanCommit is re-checked inside CommitAsync, so a message that
        //arrived blank cannot reach a commit from here.
        Stage = CommitStage.Committing;

        try
        {
            await CommitAsync(_queuedPush).ConfigureAwait(true);
        }
        finally
        {
            Stage = CommitStage.Idle;
        }
    }

    private void ApplyStreamedText(string text)
    {
        _applyingStream = true;

        try
        {
            Message = text;
        }
        finally
        {
            _applyingStream = false;
        }
    }

    private void CancelGeneration()
    {
        CancellationTokenSource? generation = _generation;
        _generation = null;

        if (generation is null)
            return;

        generation.Cancel();
        generation.Dispose();
    }

    private void OnFileSelectionChanged()
    {
        Raise(nameof(SelectionText));
        RaiseCommandStates();
    }

    private void SetAllSelected(bool selected)
    {
        foreach (FileChangeItem file in Files)
        {
            //Select-all still refuses conflicted files. A commit containing conflict markers is the one
            //outcome this window must never produce by accident.
            if (selected && file.IsConflicted)
                continue;

            file.IsSelected = selected;
        }
    }

    /// <summary>
    /// Shows the diff for <paramref name="file"/>, from the cache when it is there. A miss may be
    /// superseded before it finishes, which is why the result is checked against the still-selected
    /// row before it is displayed.
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

            //The user may have moved on while this ran.
            if (_selectedFile == file)
                CurrentDiff = diff;
        }
        finally
        {
            IsDiffLoading = false;
        }
    }

    /// <summary>
    /// Deletes the selected file from the working tree, to the Recycle Bin.
    ///
    /// <b>The only destructive thing this window does, so it is the only thing here that asks
    /// first.</b> The Recycle Bin is what keeps the answer recoverable if it was the wrong one.
    ///
    /// No Git command runs. Deleting a tracked file leaves an ordinary <c>D</c> row the user can
    /// commit or put back with <c>git restore</c>; deleting an untracked one simply removes it. The
    /// warning that distinguishes those two is why the question has a second line.
    /// </summary>
    private async Task DeleteSelectedFileAsync()
    {
        if (_selectedFile is not { } file || !file.IsOnDisk)
            return;

        bool confirmed = await (ConfirmAsync?.Invoke(
            Strings.Get("delete.title"),
            Strings.Get("delete.question", file.Path)
                + "\n\n"
                + Strings.Get(file.IsUntracked ? "delete.untracked" : "delete.tracked"),
            Strings.Get("delete.yes"),
            Strings.Get("delete.no")) ?? Task.FromResult(false)).ConfigureAwait(true);

        if (!confirmed)
            return;

        DeleteOutcome outcome = _deleter.Delete(_repository.Root, file.Path);

        if (!outcome.Succeeded)
        {
            //A null message means the shell already said why, in its own words.
            if (outcome.Message is { Length: > 0 } message)
                RaiseError(Strings.Get("delete.title"), message);

            return;
        }

        //Keyed by path alone, so the cached diff of a file that no longer exists would be rendered by
        //the next click on whatever takes its place in the list.
        _diffs.Invalidate(file.Path);

        await RefreshAsync().ConfigureAwait(true);

        //After the refresh: Adopt does not clear this, but a status line set before it would be
        //reporting on a list that had not been rebuilt yet.
        StatusText = Strings.Get("delete.done", file.Path);
    }

    /// <summary>
    /// Puts the selected file back the way HEAD has it, sending the copy on disk to the Recycle Bin
    /// on the way.
    ///
    /// <b>The Recycle Bin is what earns this a single question.</b> <c>git restore</c> discards
    /// uncommitted work outright -- the working-tree version is in no object Git holds, so nothing in
    /// the repository can bring it back.
    ///
    /// <b>Bin first, restore second, and the order is not arbitrary.</b> A locked or protected file
    /// fails the bin, and failing there means nothing has happened yet.
    /// </summary>
    private async Task RevertSelectedFileAsync()
    {
        if (_selectedFile is not { } file || !CanRevertFile)
            return;

        string body = Strings.Get("revert.body");

        //An unsaved edit never reached the disk, so it is not in the copy about to be binned. The
        //dialog says so rather than implying the bin covers it.
        if (IsEditorDirty?.Invoke() == true)
            body += "\n\n" + Strings.Get("revert.dirty");

        bool confirmed = await (ConfirmAsync?.Invoke(
            Strings.Get("revert.title"),
            Strings.Get("revert.question", file.Path) + "\n\n" + body,
            Strings.Get("revert.yes"),
            Strings.Get("revert.no")) ?? Task.FromResult(false)).ConfigureAwait(true);

        if (!confirmed)
            return;

        //Nothing on disk to preserve when the change *is* a deletion -- the row's D means the file is
        //already gone, and the revert is what brings it back.
        bool binned = false;

        if (file.IsOnDisk)
        {
            DeleteOutcome outcome = _deleter.Delete(_repository.Root, file.Path);

            if (!outcome.Succeeded)
            {
                //A null message means the shell already said why, in its own words.
                if (outcome.Message is { Length: > 0 } message)
                    RaiseError(Strings.Get("revert.title"), message);

                return;
            }

            binned = true;
        }

        RestoreResult result = await _restore
            .RevertAsync(_repository, file.Path, CancellationToken.None)
            .ConfigureAwait(true);

        if (!result.Succeeded)
        {
            //Halfway: the file has been binned and not replaced. Say what happened rather than leaving the
            //user to find out, and the Recycle Bin is the next action.
            RaiseError(
                Strings.Get("revert.title"),
                Strings.Get("revert.failed", file.Path)
                    + "\n\n"
                    + (result.Error ?? string.Empty)
                    + (binned ? "\n\n" + Strings.Get("revert.binned") : string.Empty));

            await RefreshAsync().ConfigureAwait(true);
            return;
        }

        //Keyed by path alone, so the cached diff of the pre-revert content would be rendered by the
        //next click on this row.
        _diffs.Invalidate(file.Path);

        await RefreshAsync().ConfigureAwait(true);

        StatusText = Strings.Get("revert.done", file.Path);
    }

    /// <summary>
    /// Every guard lives in <see cref="WorkingTreeWriter"/>; this only decides what to do with a
    /// refusal. An externally-modified file comes back as one the user has to answer.
    /// </summary>
    public async Task<SaveOutcome> SaveCurrentFileAsync(string newText, bool force)
    {
        if (_currentDiff is null || _selectedFile is null)
            return SaveOutcome.Refused(SaveRefusal.Missing, "No file is open.");

        SaveOutcome outcome = await _writer.SaveAsync(
            _repository.Root,
            _currentDiff.Path,
            _currentDiff.Right,
            newText,
            force,
            CancellationToken.None).ConfigureAwait(true);

        if (!outcome.Succeeded)
            return outcome;

        _log.Info($"Saved {_currentDiff.Path}.");

        //The cached diff is stale the moment the file changes, and the cache is keyed by path alone --
        //so it has to be dropped here or the next click would render the pre-save text.
        _diffs.Invalidate(_currentDiff.Path);

        //Only this file's counts, not the whole list. Re-run the diff here rather than only in the
        //pane, because Rows is read by StageHunkAsync: it would otherwise still describe the pre-edit
        //alignment, and staging -- allowed again the moment the pane is clean -- would build a patch out
        //of those rows against the file now on disk, which `git apply` refuses whole.
        SideBySideDiff current = _currentDiff;
        FileText saved = outcome.Saved!;
        bool wordLevel = current.RenderMode == DiffRenderMode.SideBySideWithWordDiff;

        IReadOnlyList<DiffRow> rows = await Task
            .Run(() => DiffService.Rediff(current.Left.Text, saved.Text, wordLevel))
            .ConfigureAwait(true);

        //Another file was selected while that was computing, so writing it back would hand
        //StageHunkAsync one file's path with another's rows.
        if (!ReferenceEquals(_currentDiff, current))
            return outcome;

        //The field, not the property, and that is deliberate here and nowhere else. Raising
        //PropertyChanged sends the window into DiffPane.Show, which rebuilds both documents from
        //scratch -- throwing away the caret, the scroll position and the undo history of a pane already
        //showing exactly this text. MarkSaved is the path written for a save.
        _currentDiff = current with { Right = saved, Rows = rows };
        StatusText = Strings.Get("edit.saved");

        _ = RefreshSelectedFileCountsAsync();

        return outcome;
    }

    /// <summary>
    /// Re-runs status for the whole repository but applies only the selected file's row. A full
    /// <see cref="Adopt"/> would rebuild the list and lose the diff pane's scroll position mid-edit.
    /// </summary>
    private async Task RefreshSelectedFileCountsAsync()
    {
        if (_selectedFile is null)
            return;

        try
        {
            RepositoryStatus refreshed = await _status
                .GetStatusAsync(_repository, CancellationToken.None)
                .ConfigureAwait(true);

            var byPath = refreshed.Files.ToDictionary(f => f.Path, StringComparer.Ordinal);

            //A file appeared or disappeared while this one was being edited, so only a full adopt can put
            //it right. It rebuilds the rows, which is what this method otherwise exists to avoid -- but a
            //list missing a row is worse than a lost scroll position.
            if (byPath.Count != Files.Count || !Files.All(f => byPath.ContainsKey(f.Path)))
            {
                Adopt(refreshed);
                return;
            }

            //A chosen-hunks flag is only true while the index still holds part of this file, so it drops
            //here rather than needing to be cleared by whoever committed.
            foreach (FileChangeItem item in Files)
            {
                if (item.Change.HasChosenHunks && byPath[item.Path].IndexStatus == GitChangeType.None)
                    item.Change.HasChosenHunks = false;
            }

            //Every row, not only the edited one.
            //
            //The tick state lives on the status's own change objects and the commit is built from the
            //status, so a row left pointing at the previous status's object would take the user's ticks into
            //an object nothing reads: the window would show one selection and commit another.
            //
            //Updated in place rather than replaced in the collection: replacing the selected item makes the
            //list's two-way SelectedItem binding push a null back through SelectedFile, which closes the
            //diff the user is editing.
            foreach (FileChangeItem item in Files)
                item.Update(byPath[item.Path]);

            //The counts only. A full Adopt here is what this method exists to avoid.
            _currentStatus = refreshed;

            Raise(nameof(CurrentStatus));
            Raise(nameof(SummaryText));
            RaiseCommandStates();
        }
        catch (Exception ex)
        {
            _log.Debug($"Post-save refresh failed: {ex.Message}");
        }
    }

    public async Task RestageCurrentFileAsync()
    {
        if (_selectedFile is null)
            return;

        await _commits.StageAsync(_repository, [_selectedFile.Path], CancellationToken.None).ConfigureAwait(true);

        StatusText = Strings.Get("edit.restaged");
        await RefreshSelectedFileCountsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Stages or unstages part of the selected file, with <c>git apply --cached</c> -- which never
    /// touches the working tree, so the file on disk and the editor holding it are unchanged.
    ///
    /// On success the file is marked <see cref="GitFileChange.HasChosenHunks"/>, which is what stops
    /// the commit sequence running <c>git add</c> over it and swallowing the hunks the user left out.
    /// Without that this feature would appear to work and then quietly commit the whole file.
    /// </summary>
    /// <returns>A sentence for the footer, or null when there was nothing to do.</returns>
    public async Task<string?> StageHunkAsync(IReadOnlySet<int> rows, bool unstage)
    {
        if (_selectedFile is null || _currentDiff is null)
            return null;

        //Each line re-terminated from the file it came from -- see Hunks, where the line-ending rule is
        //the whole difficulty.
        string? patch = Hunks.ToPatch(
            _currentDiff.Path,
            _currentDiff.Rows,
            rows,
            _currentDiff.Left,
            _currentDiff.Right);

        if (patch is null)
            return null;

        PatchResult result = unstage
            ? await _patches.UnstageAsync(_repository, patch, CancellationToken.None).ConfigureAwait(true)
            : await _patches.StageAsync(_repository, patch, CancellationToken.None).ConfigureAwait(true);

        if (!result.Succeeded)
        {
            //Git's own words. The usual cause is an index that moved since the diff was computed, and git
            //apply refuses the whole patch rather than applying half of it.
            RaiseError(Strings.Get("hunk.failed"), result.Error ?? string.Empty);
            return null;
        }

        //Only meaningful while something of this file is still staged; a later refresh drops the flag
        //when the index no longer holds anything, which makes it self-correcting after a commit.
        _selectedFile.Change.HasChosenHunks = !unstage;

        int changed = rows.Count(row => row >= 0 && row < _currentDiff.Rows.Count);
        StatusText = Strings.Get(unstage ? "hunk.unstaged" : "hunk.staged", changed);

        //Only this file's row, not the whole list: the diff pane is showing it and a rebuild would lose
        //the caret.
        await RefreshSelectedFileCountsAsync().ConfigureAwait(true);

        return StatusText;
    }

    public async Task<SideBySideDiff?> ReloadCurrentFileAsync()
    {
        if (_selectedFile is null)
            return null;

        _diffs.Invalidate(_selectedFile.Path);
        await LoadDiffAsync(_selectedFile).ConfigureAwait(true);
        return _currentDiff;
    }

    /// <summary>
    /// Hands the whole sequence to <see cref="CommitFlow"/> and turns its outcome into words. The
    /// order -- stage, switch, verify, commit, push -- lives in Core so it can be tested without a
    /// message pump.
    /// </summary>
    private async Task CommitAsync(bool push)
    {
        if (!CanCommit || _currentStatus is null)
            return;

        IsBusy = true;
        StatusText = null;

        try
        {
            //Both path lists are derived in Core, so nothing here decides what an unticked-but-staged file
            //means. TargetBranch is null when the ComboBox names the branch already checked out.
            CommitRequest request = CommitRequest.From(
                _repository,
                _currentStatus,
                _message,
                _branchResolution.RequiresBranchChange ? _branchResolution.Branch : null,
                _branchResolution.Intent == BranchIntent.NewBranch,
                push,
                AskAsync);

            CommitFlowResult result = await _flow.RunAsync(request, CancellationToken.None).ConfigureAwait(true);

            //The commit exists whatever came after it, so the message box is cleared as soon as there is a
            //hash -- even when the push that followed it failed.
            if (result.Commit is not null)
                Message = string.Empty;

            StatusText = CommitOutcomeReporter.SuccessText(result) ?? StatusText;

            await ApplyAsync(result).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyAsync(CommitFlowResult result)
    {
        //The commit moved HEAD, so every cached diff was computed against the wrong base.
        if (result.Commit is not null)
            _diffs.Clear();

        if (result.Outcome == CommitFlowOutcome.Committed)
        {
            Committed?.Invoke(result.Commit!);
        }
        else if (CommitOutcomeReporter.FailureText(result) is { } failure)
        {
            //Adopted before the message is shown for an aborted switch, so the user is looking at the state
            //the message describes.
            if (result.Outcome == CommitFlowOutcome.AbortedSelectionChanged && result.RefreshedStatus is { } refreshed)
                Adopt(refreshed);

            RaiseError(failure.Title, failure.Message);
        }

        //Always, including after a failure: the repository moved and the list on screen is stale. The
        //aborted-switch case is excluded because it has just adopted the refreshed status above.
        if (result.Outcome != CommitFlowOutcome.AbortedSelectionChanged)
            await RefreshAsync().ConfigureAwait(true);
    }

    private void UpdateNotice()
    {
        if (_currentStatus?.HasConflicts == true)
        {
            Notice = Strings.Get("commit.warn.conflict");
            return;
        }

        //The warning is about the branch being committed *to*, which with the ComboBox is not
        //necessarily the one checked out.
        string? target = _branchResolution.Intent switch
        {
            BranchIntent.Current or BranchIntent.Empty => _currentStatus?.Branch,
            BranchIntent.ExistingBranch => _branchResolution.Branch,
            _ => null,
        };

        Notice = _settings.WarnWhenCommittingToPrimaryBranch
                 && _primaryBranch is not null
                 && target is not null
                 && string.Equals(target, _primaryBranch, StringComparison.Ordinal)
            ? Strings.Get("commit.warn.primary", target)
            : null;
    }

    private void RaiseCommandStates()
    {
        Raise(nameof(CanCommit));
        Raise(nameof(CanGenerate));
        Raise(nameof(CanDeleteFile));
        DeleteFileCommand.RaiseCanExecuteChanged();
        Raise(nameof(CanRevertFile));
        RevertFileCommand.RaiseCanExecuteChanged();
        Raise(nameof(IsAiConfigured));
        CommitCommand.RaiseCanExecuteChanged();
        CommitAndPushCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        GenerateCommand.RaiseCanExecuteChanged();
    }

    private void RaiseError(string title, string message) => ErrorRaised?.Invoke(title, message);

    private void ReportError(Exception exception)
    {
        _log.Error($"Commit window operation failed: {exception}");

        //Git's own words, the repository path and a next action -- never paraphrased.
        if (exception is GitOperationException git)
        {
            RaiseError(
                git.Operation,
                $"{git.GitError}\n\n{Strings.Get("error.repositorypath", git.RepositoryPath)}"
                + (git.Suggestion is { Length: > 0 } ? $"\n\n{git.Suggestion}" : string.Empty));

            return;
        }

        RaiseError(Strings.Get("error.title"), exception.Message);
    }

    private async Task LoadBranchesAsync()
    {
        try
        {
            IReadOnlyList<string> branches = await _branches
                .ListLocalBranchesAsync(_repository, _currentStatus?.Branch, CancellationToken.None)
                .ConfigureAwait(true);

            Branches.Clear();
            foreach (string branch in branches)
                Branches.Add(branch);

            //Re-resolved now the list is known: until it arrived, an existing branch would have been
            //reported as a new one.
            BranchResolution = BranchResolution.Resolve(_branchInput, CurrentBranch, Branches);
        }
        catch (Exception ex)
        {
            //The ComboBox still works as a free-text field without its drop-down.
            _log.Debug($"Branch listing failed: {ex.Message}");
        }
    }

    private async Task ResolvePrimaryBranchAsync()
    {
        try
        {
            _primaryBranch = await _branches
                .ResolvePrimaryBranchAsync(_repository, _settings.PrimaryBranch, CancellationToken.None)
                .ConfigureAwait(true);

            UpdateNotice();
        }
        catch (Exception ex)
        {
            //A missing warning strip is a cosmetic loss. Failing the window over it is not.
            _log.Debug($"Primary branch resolution failed: {ex.Message}");
        }
    }

    private Task<bool> AskAsync(CommitFlowQuestion question, CancellationToken cancellationToken)
    {
        //The token is unused on purpose: a guardrail question has to be answered before the flow
        //continues, and cancelling it out from under the user would answer it for them.
        _ = cancellationToken;

        return _consent.AnswerAsync(
            _repository,
            question,
            (title, body, yes, no) => ConfirmAsync?.Invoke(title, body, yes, no) ?? Task.FromResult(false));
    }
}
