using System.Collections.ObjectModel;
using FlickGit.App.CommandLine;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.App.Settings;
using FlickGit.Branches;
using FlickGit.Commits;
using FlickGit.Git;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Status;

namespace FlickGit.App.ViewModels;

/// <summary>
/// Everything the two commit surfaces do the same way.
///
/// There are two of them — the commit window and the quick-commit popup — and they differ in what
/// they <i>show</i>: one has a file list and a live-editable diff, the other a summary line and a
/// streamed AI message. They do not differ in what they <i>do</i>. The same branch resolution, the
/// same warning rule, the same guardrail consent, the same call into <see cref="CommitFlow"/>.
/// Before this class existed all of that was written twice, byte for byte, including the method that
/// builds the commit request — so a fix applied to one surface would have quietly left the other
/// wrong.
///
/// A base class rather than a shared collaborator, for one concrete reason: WPF binds inherited
/// members exactly as it binds declared ones, so this costs no change to either window's XAML.
/// Routing the same members through a collaborator property would have re-pathed every binding in
/// both files to buy nothing.
///
/// <b>Reuse is the correctness risk here.</b> The resident service keeps one instance of each
/// surface alive for the whole session, so <see cref="Reset"/> must leave nothing behind from the
/// previous repository. Every field declared in this class is cleared in this class; a derived
/// surface clears its own and calls <c>base.Reset</c>. One place per layer, instead of two whole
/// methods that have to be kept in step by hand.
/// </summary>
public abstract class CommitSurface : ObservableObject
{
    private readonly StatusService _status;
    private readonly BranchService _branches;
    private readonly CommitFlow _flow;
    private readonly UpstreamConsent _consent;

    private RepositoryInfo _repository;
    private RepositoryStatus? _currentStatus;
    private string _message = string.Empty;
    private string _branchInput = string.Empty;
    private BranchResolution _branchResolution = new(BranchIntent.Empty, string.Empty);
    private string? _primaryBranch;
    private bool _isBusy;
    private string? _notice;
    private string? _statusText;

    protected CommitSurface(
        RepositoryInfo repository,
        StatusService status,
        BranchService branches,
        CommitFlow flow,
        UpstreamConsent consent,
        FlickSettings settings,
        ILog log)
    {
        _repository = repository;
        _status = status;
        _branches = branches;
        _flow = flow;
        _consent = consent;

        Settings = settings;
        Log = log;

        CommitCommand = new AsyncCommand(() => CommitAsync(push: false), () => CanCommit, ReportError);
        CommitAndPushCommand = new AsyncCommand(() => CommitAsync(push: true), () => CanCommit, ReportError);
    }

    protected FlickSettings Settings { get; }

    protected ILog Log { get; }

    public AsyncCommand CommitCommand { get; }

    public AsyncCommand CommitAndPushCommand { get; }

    /// <summary>Local branches, current first, for the ComboBox drop-down.</summary>
    public ObservableCollection<string> Branches { get; } = [];

    /// <summary>Raised when the commit succeeded, so the surface can report and close.</summary>
    public event Action<CommitResult>? Committed;

    /// <summary>Raised for anything the user has to be told, with Git's own words in it.</summary>
    public event Action<string, string>? ErrorRaised;

    /// <summary>
    /// Asks the user a yes/no question and waits for the answer.
    ///
    /// A callback rather than a dialog call, because a view model must not construct windows — and
    /// because the questions it asks are guardrail consent, which CLAUDE.md requires to be answered
    /// before anything executes. Each surface owns a different window, so each supplies its own.
    /// </summary>
    public Func<string, string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    public RepositoryInfo Repository
    {
        get => _repository;
        private set => Set(ref _repository, value);
    }

    public string RepositoryName => _repository.Name;

    /// <summary>
    /// The status behind everything on screen. Null until the first refresh lands.
    ///
    /// Public because the popup hands it to the commit window for the <c>Details…</c> handoff, which
    /// is what keeps that inside CLAUDE.md's 60 ms budget instead of paying for three more Git
    /// processes.
    /// </summary>
    public RepositoryStatus? CurrentStatus => _currentStatus;

    public string CurrentBranch => _currentStatus?.Branch ?? string.Empty;

    public virtual string SummaryText =>
        _currentStatus is null
            ? string.Empty
            : Strings.Get("commit.summary.counts", _currentStatus.TrackedChangeCount, _currentStatus.UntrackedCount);

    public string Message
    {
        get => _message;
        set
        {
            if (Set(ref _message, value))
                OnMessageChanged();
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
        protected set
        {
            if (Set(ref _statusText, value))
                Raise(nameof(HasStatusText));
        }
    }

    public bool HasStatusText => _statusText is not null;

    /// <summary>
    /// Whether committing is possible at all.
    ///
    /// The tick state lives on the status's own <c>GitFileChange</c> instances — which is what both
    /// surfaces write, the commit window through <c>FileChangeItem</c> — so this is the same question
    /// wherever it is asked from. A message is required: CLAUDE.md, "Never commit an empty or
    /// placeholder message."
    /// </summary>
    public virtual bool CanCommit =>
        !_isBusy
        && _currentStatus is not null
        && _currentStatus.Files.Any(f => f.IsSelected)
        && !string.IsNullOrWhiteSpace(_message)
        && _branchResolution.IsCommittable
        && !_currentStatus.HasConflicts;

    public bool CloseAfterCommit => Settings.CloseCommitWindowAfterSuccess;

    /// <summary>
    /// Reads the status and adopts it.
    ///
    /// Deliberately not called from <see cref="Reset"/>: the surface is shown between the two.
    /// CLAUDE.md budgets the window or popup appearing separately from its contents arriving — two
    /// budgets, so two steps. Populating first means paying both before anything is on screen, and
    /// three Git processes is most of that.
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
    /// having, and worth nothing if waiting for them delays the surface.
    /// </summary>
    public virtual void Adopt(RepositoryStatus status)
    {
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
        UpdateNotice();
        RaiseCommandStates();

        _ = ResolvePrimaryBranchAsync();
        _ = LoadBranchesAsync();
    }

    /// <summary>
    /// Swaps in a freshly-read status without re-adopting it.
    ///
    /// The commit window needs this after a save, where only the edited file's row is updated: a full
    /// <see cref="Adopt"/> would rebuild the list and lose the diff pane's scroll position mid-edit.
    /// The caller is responsible for carrying the user's tick state onto the new status, because the
    /// commit is built from it.
    /// </summary>
    protected void AdoptCounts(RepositoryStatus status)
    {
        _currentStatus = status;

        Raise(nameof(CurrentStatus));
        Raise(nameof(SummaryText));
        RaiseCommandStates();
    }

    /// <summary>
    /// Clears everything this class owns.
    ///
    /// A derived surface overrides, clears its own fields, and calls this. A field added above and
    /// not cleared here is exactly the leak CLAUDE.md calls "the main correctness risk of reuse" —
    /// it shows up as the previous repository's message in a surface now pointed somewhere else.
    /// </summary>
    public virtual void Reset(RepositoryInfo repository)
    {
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
        Raise(nameof(CurrentStatus));
        Raise(nameof(CurrentBranch));
        Raise(nameof(SummaryText));
        RaiseCommandStates();
    }

    /// <summary>
    /// Hands the whole sequence to <see cref="CommitFlow"/> and turns its outcome into words.
    ///
    /// The sequence itself — stage, switch, verify, commit, push — lives in Core so it can be tested
    /// without a message pump. What is left here is what only a surface can do: ask the guardrail
    /// questions, and phrase the result in the user's language.
    /// </summary>
    protected async Task CommitAsync(bool push)
    {
        if (!CanCommit || _currentStatus is null)
            return;

        IsBusy = true;
        StatusText = null;

        try
        {
            //Both path lists are derived in Core, so the two surfaces cannot disagree about what an
            //unticked-but-staged file means. TargetBranch is null when the ComboBox names the branch
            //already checked out, which is the normal case and costs no Git call.
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
            //is a hash — even when the push that followed it failed.
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

    /// <summary>
    /// What this surface does with an outcome beyond reporting it: drop a cache, refresh a list,
    /// close, hand off.
    /// </summary>
    protected abstract Task ApplyAsync(CommitFlowResult result);

    protected void RaiseCommitted(CommitResult result) => Committed?.Invoke(result);

    protected void RaiseError(string title, string message) => ErrorRaised?.Invoke(title, message);

    /// <summary>Recomputes the warning strip. Called whenever the branch or the status changes.</summary>
    protected void UpdateNotice()
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

        Notice = Settings.WarnWhenCommittingToPrimaryBranch
                 && _primaryBranch is not null
                 && target is not null
                 && string.Equals(target, _primaryBranch, StringComparison.Ordinal)
            ? Strings.Get("commit.warn.primary", target)
            : null;
    }

    protected virtual void RaiseCommandStates()
    {
        Raise(nameof(CanCommit));
        CommitCommand.RaiseCanExecuteChanged();
        CommitAndPushCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Hook for a surface where a typed message means something beyond enabling the button — the
    /// popup treats it as the user taking over from a stream.
    /// </summary>
    protected virtual void OnMessageChanged() => RaiseCommandStates();

    protected void ReportError(Exception exception)
    {
        Log.Error($"{GetType().Name} operation failed: {exception}");

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
            Log.Debug($"Branch listing failed: {ex.Message}");
        }
    }

    private async Task ResolvePrimaryBranchAsync()
    {
        try
        {
            _primaryBranch = await _branches
                .ResolvePrimaryBranchAsync(_repository, Settings.PrimaryBranch, CancellationToken.None)
                .ConfigureAwait(true);

            UpdateNotice();
        }
        catch (Exception ex)
        {
            //A missing warning strip is a cosmetic loss. Failing the surface over it is not.
            Log.Debug($"Primary branch resolution failed: {ex.Message}");
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
