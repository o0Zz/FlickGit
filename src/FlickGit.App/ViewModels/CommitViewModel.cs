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
/// <b>This was two classes and a shared base until the quick-commit popup was removed.</b> The base
/// existed because the popup and the window did the same things — the same branch resolution, the
/// same warning rule, the same guardrail consent, the same call into <see cref="CommitFlow"/> — and
/// having that written twice meant a fix applied to one surface quietly left the other wrong. With
/// the popup gone it was an abstract class with exactly one subclass, which CLAUDE.md's "Coding
/// Guidelines" lists under Avoid, so it was folded back in.
///
/// The AI generation and the queued Enter came with it. They were the popup's two unique behaviours
/// and they are the whole of what made the fast path fast, so they belong to the surface that
/// replaced it — otherwise removing the popup would have removed the only place in the product that
/// can write a commit message.
///
/// <b>Reuse is the correctness risk here.</b> The resident service keeps one instance alive for the
/// whole session, so <see cref="Reset"/> must leave nothing behind from the previous repository.
/// Every mutable field declared below is assigned there — one place to look when adding a field. Not
/// a test: Hard Requirement 4 puts everything in <c>FlickGit.App</c> out of scope. Verified by
/// running it.
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
    private readonly CommitMessageService _messages;
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
    /// No repository parameter: the one it was given was always <see cref="RepositoryInfo.None"/>,
    /// because the window is pre-warmed long before anybody right-clicks and
    /// <see cref="Reset(RepositoryInfo)"/> is what points it at a folder. Per Hard Requirement 3 the
    /// repository is per-invocation state, so it arrives per invocation.
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
        CommitMessageService messages,
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
        //pressed a button labelled "generate", so overwriting their text is what they asked for.
        GenerateCommand = new RelayCommand(() => BeginGeneration(force: true), () => CanGenerate);
    }

    public AsyncCommand CommitCommand { get; }
    public AsyncCommand CommitAndPushCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand GenerateCommand { get; }

    /// <summary>The file list's context menu. Acts on the row the right-click selected.</summary>
    public AsyncCommand DeleteFileCommand { get; }

    /// <summary>The other item on that menu. Puts the row's file back the way HEAD has it.</summary>
    public AsyncCommand RevertFileCommand { get; }

    public ObservableCollection<FileChangeItem> Files { get; } = [];

    /// <summary>Local branches, current first, for the ComboBox drop-down.</summary>
    public ObservableCollection<string> Branches { get; } = [];

    /// <summary>Raised when the commit succeeded, so the window can report and close.</summary>
    public event Action<CommitResult>? Committed;

    /// <summary>Raised for anything the user has to be told, with Git's own words in it.</summary>
    public event Action<string, string>? ErrorRaised;

    /// <summary>
    /// Raised when the caret belongs back in the message box: a generation that failed with a commit
    /// queued, or one that landed and is now waiting for Enter.
    /// </summary>
    public event Action? FocusMessageRequested;

    /// <summary>
    /// Asks the user a yes/no question and waits for the answer.
    ///
    /// A callback rather than a dialog call, because a view model must not construct windows — and
    /// because the questions it asks are guardrail consent, which CLAUDE.md requires to be answered
    /// before anything executes.
    /// </summary>
    public Func<string, string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>
    /// Whether the diff pane holds an unsaved edit, asked of the window because the pane is the
    /// window's.
    ///
    /// Only the revert confirmation reads it, and only to add a sentence. An edit that was never
    /// saved is not on disk, so it is not what goes to the Recycle Bin — and a dialog promising
    /// recoverability has to be right about what it is promising.
    /// </summary>
    public Func<bool>? IsEditorDirty { get; set; }

    public RepositoryInfo Repository
    {
        get => _repository;
        private set => Set(ref _repository, value);
    }

    public string RepositoryName => _repository.Name;

    public string Title => Strings.Get("commit.title", _repository.Name);

    /// <summary>The status behind everything on screen. Null until the first refresh lands.</summary>
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

            //A keystroke in the box means the user is taking over from the stream. Their text wins: a
            //stream that kept overwriting what they were typing would be unusable.
            if (!_applyingStream && _stage is CommitStage.Generating or CommitStage.Queued)
            {
                CancelGeneration();
                Stage = CommitStage.Idle;
                StatusText = null;
            }

            RaiseCommandStates();
        }
    }

    /// <summary>
    /// The branch ComboBox's text. Free text: anything that is not an existing branch is a new
    /// branch name.
    /// </summary>
    public string BranchInput
    {
        get => _branchInput;
        set
        {
            if (!Set(ref _branchInput, value))
                return;

            //Resolved on every keystroke with no Git call: the branch list is already in memory and
            //name validity is an offline check.
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

    /// <summary>The warning strip: committing to the primary branch, or an unresolved conflict.</summary>
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

    /// <summary>The last outcome line. Cleared at the start of the next action.</summary>
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

    /// <summary>Where the window is in the type-Enter-done sequence.</summary>
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
    /// Whether committing is possible at all.
    ///
    /// The tick state lives on the status's own <c>GitFileChange</c> instances — which is what
    /// <c>FileChangeItem</c> writes — so this is the same question wherever it is asked from. A
    /// message is required: CLAUDE.md, "Never commit an empty or placeholder message."
    /// </summary>
    public bool CanCommit =>
        _stage is not (CommitStage.Queued or CommitStage.Committing)
        && !_isBusy
        && _currentStatus is not null
        && _currentStatus.Files.Any(f => f.IsSelected)
        && !string.IsNullOrWhiteSpace(_message)
        && _branchResolution.IsCommittable
        && !_currentStatus.HasConflicts;

    /// <summary>
    /// Whether the Generate button does anything: a provider, a key, consent, and something to
    /// describe.
    /// </summary>
    public bool CanGenerate =>
        _messages.IsUsable
        && _stage is not (CommitStage.Queued or CommitStage.Committing)
        && _currentStatus is not null
        && _currentStatus.Files.Any(f => f.IsSelected);

    /// <summary>
    /// Whether there is a file to delete: one selected, still on disk, and nothing else running.
    ///
    /// A row whose file is already gone — deleted from the working tree, or removed with
    /// <c>git rm</c> — is greyed out rather than offered and then refused. It is the one state where
    /// the letter on the row (<c>D</c>) already says the answer.
    /// </summary>
    public bool CanDeleteFile => !_isBusy && _selectedFile is { IsOnDisk: true };

    /// <summary>
    /// Whether there is a file to revert: one selected, present in HEAD, and nothing else running.
    ///
    /// The reasons a file is not revertable are <see cref="RestoreService.CanRevert"/>'s, and they
    /// are all one reason — HEAD does not have this path. Greyed out rather than offered and then
    /// refused, the same rule Delete follows for a row whose file is already gone.
    /// </summary>
    public bool CanRevertFile =>
        !_isBusy && _selectedFile is { } file && RestoreService.CanRevert(file.Change);

    /// <summary>
    /// Whether the AI is configured at all. False hides the button rather than showing a permanently
    /// dead one — with no key stored there is nothing the user can do with it here, and Settings is
    /// where that is fixed.
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

            //The context menu acts on the selection, so it has to re-evaluate with it.
            RaiseCommandStates();

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

    public string DiffFontFamily => _settings.DiffFontFamily;

    public double DiffFontSize => _settings.DiffFontSize;

    // ---- lifecycle ----------------------------------------------------------------

    /// <summary>
    /// Clears everything, so the reused window shows nothing of the previous repository.
    ///
    /// A field added above and not cleared here is exactly the leak CLAUDE.md calls "the main
    /// correctness risk of reuse" — it shows up as the previous repository's message in a window now
    /// pointed somewhere else.
    /// </summary>
    public void Reset(RepositoryInfo repository)
    {
        //The AI's state is as much a leak risk as anything else: a generation left running would
        //stream the previous repository's message into this one.
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
    /// Reads the status and adopts it.
    ///
    /// Deliberately not called from <see cref="Reset"/>: the window is shown between the two.
    /// CLAUDE.md budgets the window appearing separately from its contents arriving — two budgets, so
    /// two steps. Populating first means paying both before anything is on screen, and three Git
    /// processes is most of that.
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
    /// Takes a status, from <see cref="RefreshAsync"/> or from whoever already fetched one.
    ///
    /// The branch list and the primary-branch warning are started and not awaited: both are worth
    /// having, and worth nothing if waiting for them delays the window.
    /// </summary>
    public void Adopt(RepositoryStatus status)
    {
        //The tick boxes the user already set, kept across a refresh. Losing them would make Refresh
        //actively hostile.
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

            //Kept only while the index still holds something of the file: after a commit it does not,
            //and the choice is spent.
            change.HasChosenHunks = previousHunks.Contains(change.Path) && change.IndexStatus != GitChangeType.None;

            var item = new FileChangeItem(change);
            item.SelectionChanged += OnFileSelectionChanged;
            Files.Add(item);
        }

        _currentStatus = status;

        //The ComboBox opens on the current branch, so committing without touching it involves no
        //switch at all.
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

    /// <summary>
    /// Called by the window when it closes, so neither a running diff nor a running generation
    /// outlives it.
    /// </summary>
    public void Cancel()
    {
        CancelGeneration();
        _diffs.Cancel();
    }

    // ---- the AI message ------------------------------------------------------------

    /// <summary>
    /// Starts writing a message, when there is something to write about and a provider to ask.
    ///
    /// Fire-and-forget by design: the window is already on screen, and the message arrives into it.
    /// </summary>
    /// <param name="force">
    /// True for the Generate button, which replaces whatever is in the box. False for the automatic
    /// pass when the window opens, where text already in the box means the user got there first —
    /// overwriting it would be the rudest thing this feature could do.
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
    /// Enter, and what it means right now.
    ///
    /// <b>During generation it queues rather than refusing.</b> CLAUDE.md: "do not block and do not
    /// refuse... This is what makes the true one-key path work — trigger, Enter, done, without
    /// waiting to read anything."
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
    /// Esc. <b>Closes the window</b>, whatever else is in flight.
    ///
    /// It briefly did not: a generation in progress ate the first Esc and only the second one closed.
    /// That reads as a stuck window, and it is the common case rather than the rare one — generation
    /// starts on every open, so for the first half-second Esc would appear to do nothing. One key,
    /// one outcome, always.
    ///
    /// Closing is safe with a generation or a queued commit outstanding: the window's
    /// <c>OnClosed</c> calls <see cref="Cancel"/>, which cancels the token, and
    /// <see cref="RunGenerationAsync"/> then finds its own <c>CancellationTokenSource</c> replaced and
    /// returns without committing. A queued Enter cannot fire into a window that is gone.
    /// </summary>
    /// <returns>
    /// False only while a commit is actually executing. There is nothing to take back at that point
    /// that would not leave the repository half-changed, and the window has to stay to report the
    /// outcome.
    /// </returns>
    public bool EscapePressed() => _stage != CommitStage.Committing;

    private async Task RunGenerationAsync(CancellationTokenSource generation)
    {
        GenerationOutcome outcome = await _messages
            .StreamAsync(
                _repository,
                _currentStatus!,
                ApplyStreamedText,
                generation.Token)
            .ConfigureAwait(true);

        //A newer generation started, or the window moved on. Nothing here is still wanted.
        if (!ReferenceEquals(_generation, generation))
            return;

        _generation = null;
        generation.Dispose();

        if (!outcome.Succeeded)
        {
            bool wasQueued = _stage == CommitStage.Queued;

            Stage = CommitStage.Idle;
            StatusText = outcome.FailureReason;

            //CLAUDE.md: "If generation fails while a commit is queued: cancel the queue, focus the
            //message box, keep it open. Never commit an empty or placeholder message."
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
            //The caret belongs at the end of what just arrived, so Enter commits it and typing
            //appends rather than replacing.
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

    /// <summary>Puts streamed text in the box without it reading as the user typing.</summary>
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

    // ---- the file list -------------------------------------------------------------

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

    // ---- deleting -----------------------------------------------------------------

    /// <summary>
    /// Deletes the selected file from the working tree, to the Recycle Bin.
    ///
    /// <b>The only destructive thing this window does, so it is the only thing here that asks
    /// first.</b> CLAUDE.md's Safety Rules allow a destructive operation on "explicit user intent,
    /// expressed in the moment" and require a second confirmation regardless of surface — which is
    /// what a right-click, a menu item and this question are. The Recycle Bin is what keeps the
    /// answer recoverable if it was the wrong one; see <see cref="WorkingTreeDeleter"/>.
    ///
    /// No Git command runs. Deleting a tracked file leaves an ordinary <c>D</c> row the user can
    /// commit or put back with <c>git restore</c>; deleting an untracked one simply removes it. The
    /// warning that distinguishes those two is the whole reason the question has a second line.
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

        //Keyed by path alone, so the cached diff of a file that no longer exists would be rendered
        //by the next click on whatever takes its place in the list.
        _diffs.Invalidate(file.Path);

        await RefreshAsync().ConfigureAwait(true);

        //After the refresh: Adopt does not clear this, but a status line set before it would be
        //reporting on a list that had not been rebuilt yet.
        StatusText = Strings.Get("delete.done", file.Path);
    }

    // ---- reverting ----------------------------------------------------------------

    /// <summary>
    /// Puts the selected file back the way HEAD has it, sending the copy on disk to the Recycle Bin
    /// on the way.
    ///
    /// <b>The Recycle Bin is what earns this a single question, exactly as it does for Delete.</b>
    /// CLAUDE.md's Safety Rules say uncommitted work is never discarded, and <c>git restore</c>
    /// discards it outright — the working-tree version is not in any object Git holds, so nothing in
    /// the repository can bring it back. Binning it first turns "gone" into "somewhere the user
    /// already knows how to look", which is the same trade the file list's Delete makes and the same
    /// reason neither needs a warning nobody could act on.
    ///
    /// <b>Bin first, restore second, and the order is not arbitrary.</b> A locked or protected file
    /// fails the bin, and failing there means nothing has happened yet. The reverse order would
    /// overwrite the file and then discover it cannot be preserved.
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

        //Nothing on disk to preserve when the change *is* a deletion -- the row's D means the file
        //is already gone, and the revert is what brings it back.
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
            //Halfway: the file has been binned and not replaced. CLAUDE.md, "Error Handling" --
            //explain what happened rather than leaving the user to find out, and the Recycle Bin is
            //the next action.
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
        //next click on this row -- which, after a full revert, is a row that no longer exists.
        _diffs.Invalidate(file.Path);

        await RefreshAsync().ConfigureAwait(true);

        //After the refresh: Adopt does not clear this, but a status line set before it would be
        //reporting on a list that had not been rebuilt yet.
        StatusText = Strings.Get("revert.done", file.Path);
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
            _repository.Root,
            _currentDiff.Path,
            _currentDiff.Right,
            newText,
            force,
            CancellationToken.None).ConfigureAwait(true);

        if (!outcome.Succeeded)
            return outcome;

        _log.Info($"Saved {_currentDiff.Path}.");

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
                .GetStatusAsync(_repository, CancellationToken.None)
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

        await _commits.StageAsync(_repository, [_selectedFile.Path], CancellationToken.None).ConfigureAwait(true);

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
            ? await _patches.UnstageAsync(_repository, patch, CancellationToken.None).ConfigureAwait(true)
            : await _patches.StageAsync(_repository, patch, CancellationToken.None).ConfigureAwait(true);

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

    /// <summary>
    /// Hands the whole sequence to <see cref="CommitFlow"/> and turns its outcome into words.
    ///
    /// The sequence itself — stage, switch, verify, commit, push — lives in Core so it can be tested
    /// without a message pump. What is left here is what only a surface can do: ask the guardrail
    /// questions, and phrase the result in the user's language.
    /// </summary>
    private async Task CommitAsync(bool push)
    {
        if (!CanCommit || _currentStatus is null)
            return;

        IsBusy = true;
        StatusText = null;

        try
        {
            //Both path lists are derived in Core, so nothing here decides what an unticked-but-staged
            //file means. TargetBranch is null when the ComboBox names the branch already checked out,
            //which is the normal case and costs no Git call.
            CommitRequest request = CommitRequest.From(
                _repository,
                _currentStatus,
                _message,
                _branchResolution.RequiresBranchChange ? _branchResolution.Branch : null,
                _branchResolution.Intent == BranchIntent.NewBranch,
                push,
                AskAsync);

            CommitFlowResult result = await _flow.RunAsync(request, CancellationToken.None).ConfigureAwait(true);

            //The commit exists whatever came after it, so the message box is cleared as soon as there
            //is a hash -- even when the push that followed it failed.
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

    /// <summary>Recomputes the warning strip. Called whenever the branch or the status changes.</summary>
    private void UpdateNotice()
    {
        if (_currentStatus?.HasConflicts == true)
        {
            Notice = Strings.Get("commit.warn.conflict");
            return;
        }

        //The warning is about the branch being committed *to*, which with the ComboBox is not
        //necessarily the one checked out. Committing to main by typing it deserves the same friction
        //as committing to main while standing on it.
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

        //A Git failure is reported with Git's own words, the repository path and a next action --
        //never paraphrased. CLAUDE.md, "Error Handling".
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
