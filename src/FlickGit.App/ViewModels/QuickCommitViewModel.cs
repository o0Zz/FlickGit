using FlickGit.Ai;
using FlickGit.App.Ai;
using FlickGit.App.CommandLine;
using FlickGit.App.Localization;
using FlickGit.App.Settings;
using FlickGit.Branches;
using FlickGit.Commits;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Status;

namespace FlickGit.App.ViewModels;

/// <summary>
/// The quick-commit popup's state. Deliberately much smaller than <see cref="CommitViewModel"/>.
///
/// The popup is the fast path and the commit window is the escape hatch, so this surface shows a
/// summary rather than a file list, and has no diff at all. Everything it has in common with the
/// window — the branch ComboBox, the warning strip, the guardrail consent and the commit itself — is
/// <see cref="CommitSurface"/>. What is left here is the two things only the popup does: stream a
/// message in, and let Enter be pressed before it arrives.
/// </summary>
public sealed class QuickCommitViewModel : CommitSurface
{
    private readonly CommitMessageService _messages;

    private bool _isFallbackRepository;
    private QuickCommitStage _stage;
    private bool _queuedPush;
    private bool _applyingStream;
    private CancellationTokenSource? _generation;

    public QuickCommitViewModel(
        StatusService status,
        BranchService branches,
        CommitFlow flow,
        CommitMessageService messages,
        UpstreamConsent consent,
        FlickSettings settings,
        ILog log)
        : base(RepositoryInfo.None, status, branches, flow, consent, settings, log) =>
        _messages = messages;

    /// <summary>Raised when generation failed with a commit queued, so the window can focus the box.</summary>
    public event Action? FocusMessageRequested;

    /// <summary>
    /// Says so when the repository was not the one the user was looking at, but the most recently
    /// used one.
    ///
    /// CLAUDE.md is explicit that this must be visible: the user did not choose this repository, so
    /// committing to it without saying so is exactly the silent action the popup must not take. The
    /// sentence is what the popup binds; the flag behind it is nobody else's business.
    /// </summary>
    public string HeaderHint => _isFallbackRepository ? Strings.Get("quick.fallback") : string.Empty;

    private bool Fallback
    {
        set
        {
            if (Set(ref _isFallbackRepository, value))
                Raise(nameof(HeaderHint));
        }
    }

    /// <summary>
    /// The summary line, which says so while the status is still being read.
    ///
    /// The popup has no file list to look at in the meantime, so an empty line here would read as
    /// "nothing to commit" during the ~90 ms before the counts land.
    /// </summary>
    public override string SummaryText =>
        CurrentStatus is null ? Strings.Get("diff.loading") : base.SummaryText;

    /// <summary>
    /// Where the popup is in the trigger-Enter-done sequence.
    ///
    /// A state rather than a pair of bools because Esc means something different in each one: cancel
    /// the queue, dismiss the popup, or nothing at all.
    /// </summary>
    public QuickCommitStage Stage
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
        QuickCommitStage.Queued => Strings.Get("quick.queued"),
        QuickCommitStage.Committing => Strings.Get("quick.committing"),
        _ => Strings.Get("commit.button.commitpush"),
    };

    /// <summary>A queued or running commit is past the point where a second one makes sense.</summary>
    public override bool CanCommit =>
        _stage is not (QuickCommitStage.Queued or QuickCommitStage.Committing) && base.CanCommit;

    /// <summary>
    /// Enter, and what it means right now.
    ///
    /// <b>During generation it queues rather than refusing.</b> CLAUDE.md: "do not block and do not
    /// refuse... This is what makes the true one-key path work - trigger, Enter, done, without
    /// waiting to read anything."
    /// </summary>
    public void EnterPressed(bool push)
    {
        switch (_stage)
        {
            case QuickCommitStage.Generating:
                _queuedPush = push;
                Stage = QuickCommitStage.Queued;
                break;

            case QuickCommitStage.Queued:
            case QuickCommitStage.Committing:
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
    /// Esc. Cancels a queued commit, or says the popup may close.
    /// </summary>
    /// <returns>False while a commit is running: the point of no return has passed.</returns>
    public bool EscapePressed()
    {
        if (_stage == QuickCommitStage.Committing)
            return false;

        CancelGeneration();
        Stage = QuickCommitStage.Idle;
        return true;
    }

    /// <summary>
    /// Starts writing a message, when there is something to write about and a provider to ask.
    ///
    /// Fire-and-forget by design: the popup is already on screen, and the message arrives into it.
    /// </summary>
    public void BeginGeneration()
    {
        if (!_messages.IsUsable || CurrentStatus is null || !CurrentStatus.Files.Any(f => f.IsSelected))
            return;

        //The user has already typed something. Overwriting it would be the rudest thing this feature
        //could do.
        if (Message.Length > 0)
            return;

        CancelGeneration();

        var generation = new CancellationTokenSource();
        _generation = generation;

        Stage = QuickCommitStage.Generating;
        StatusText = Strings.Get("ai.generating");

        _ = RunGenerationAsync(generation);
    }

    /// <summary>
    /// Re-points the popup at a repository, and says whether it is the one the user was looking at.
    /// </summary>
    public void Reset(RepositoryInfo repository, bool isFallback)
    {
        Reset(repository);
        Fallback = isFallback;
    }

    public override void Reset(RepositoryInfo repository)
    {
        //The AI's state is as much a leak risk as anything in the base: a generation left running
        //would stream the previous repository's message into this one.
        CancelGeneration();
        Stage = QuickCommitStage.Idle;
        _queuedPush = false;
        _applyingStream = false;
        Fallback = false;

        base.Reset(repository);
    }

    protected override async Task ApplyAsync(CommitFlowResult result)
    {
        if (result.Outcome == CommitFlowOutcome.Committed)
        {
            RaiseCommitted(result.Commit!);
            return;
        }

        if (CommitOutcomeReporter.FailureText(result) is { } failure)
            RaiseError(failure.Title, failure.Message);

        //The popup stays open on a failure, so the summary it shows has to describe the repository as
        //it is now rather than as it was before the attempt.
        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// A keystroke in the message box means the user is taking over from the stream. Their text
    /// wins: a stream that kept overwriting what they were typing would be unusable.
    /// </summary>
    protected override void OnMessageChanged()
    {
        if (!_applyingStream && _stage is QuickCommitStage.Generating or QuickCommitStage.Queued)
        {
            CancelGeneration();
            Stage = QuickCommitStage.Idle;
            StatusText = null;
        }

        base.OnMessageChanged();
    }

    private async Task RunGenerationAsync(CancellationTokenSource generation)
    {
        GenerationOutcome outcome = await _messages
            .StreamAsync(
                Repository,
                CurrentStatus!,
                ApplyStreamedText,
                (title, body, yes, no) => ConfirmAsync?.Invoke(title, body, yes, no) ?? Task.FromResult(false),
                generation.Token)
            .ConfigureAwait(true);

        //A newer generation started, or the popup moved on. Nothing here is still wanted.
        if (!ReferenceEquals(_generation, generation))
            return;

        _generation = null;
        generation.Dispose();

        if (!outcome.Succeeded)
        {
            bool wasQueued = _stage == QuickCommitStage.Queued;

            Stage = QuickCommitStage.Idle;
            StatusText = outcome.FailureReason;

            //CLAUDE.md: "If generation fails while a commit is queued: cancel the queue, focus the
            //message box, keep the popup open. Never commit an empty or placeholder message."
            if (wasQueued)
                FocusMessageRequested?.Invoke();

            return;
        }

        ApplyStreamedText(outcome.Message);
        StatusText = null;

        bool commitNow = _stage == QuickCommitStage.Queued;
        Stage = QuickCommitStage.Idle;

        if (!commitNow)
            return;

        //The queued Enter, cashed in. CanCommit is re-checked inside CommitAsync, so a message that
        //arrived blank cannot reach a commit from here.
        Stage = QuickCommitStage.Committing;

        try
        {
            await CommitAsync(_queuedPush).ConfigureAwait(true);
        }
        finally
        {
            Stage = QuickCommitStage.Idle;
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
}
