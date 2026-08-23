using FlickGit.Branches;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Pulls;
using FlickGit.Remotes;
using FlickGit.Status;

namespace FlickGit.Commits;

/// <summary>
/// The commit sequence: stage, switch or create the branch, verify, commit, push.
///
/// This lives in Core rather than in the commit window because it is the most safety-critical
/// sequence in the product and it has to be testable without a message pump. Its rules come from
/// CLAUDE.md, "Branch Selector → Resolution on commit", and the <b>order is the substance</b>:
///
/// <code>
/// 1. stage the selection      staging is index-based and survives a switch
/// 2. switch or create         so the commit lands on the intended branch
/// 3. refresh and compare      the reviewed diff was against the OLD branch's HEAD
/// 4. commit                   only if nothing the user selected moved under them
/// 5. push                     only after the guardrails have been checked
/// </code>
///
/// Getting step 2 wrong commits to the previous branch. Skipping step 3 commits content the user
/// never saw. Both are silent failures, which is why they are asserted by tests here rather than
/// left to a click-through.
///
/// Nothing in this class formats a message for a human: it returns what happened and lets the UI
/// say it, so the wording stays in the language file.
/// </summary>
public sealed class CommitFlow(
    StatusService status,
    CommitService commits,
    BranchService branches,
    SwitchService switches,
    PushService pushes,
    PullService pulls,
    ILog log)
{
    public async Task<CommitFlowResult> RunAsync(CommitRequest request, CancellationToken cancellationToken)
    {
        RepositoryInfo repository = request.Repository;

        //1. Stage the selection, and take back out of the index anything the user unticked.
        //   `git commit` commits the index, so an unticked-but-staged file would otherwise be
        //   committed anyway and the unticking would have done nothing.
        if (request.PathsToUnstage.Count > 0)
            await commits.UnstageAsync(repository, request.PathsToUnstage, cancellationToken).ConfigureAwait(false);

        await commits.StageAsync(repository, request.SelectedPaths, cancellationToken).ConfigureAwait(false);

        //2 and 3.
        CommitFlowResult? branchProblem = await ApplyBranchAsync(request, cancellationToken).ConfigureAwait(false);
        if (branchProblem is not null)
            return branchProblem;

        //Asked after staging, because a file whose only change was already staged and then
        //reverted on disk stages to nothing.
        if (!await commits.HasStagedChangesAsync(repository, cancellationToken).ConfigureAwait(false))
            return new CommitFlowResult(CommitFlowOutcome.NothingToCommit);

        //4.
        CommitResult commit = await commits
            .CommitAsync(repository, request.Message, cancellationToken)
            .ConfigureAwait(false);

        log.Info($"Committed {commit.ShortHash} in {repository.Root}.");

        if (!request.Push)
            return new CommitFlowResult(CommitFlowOutcome.Committed) { Commit = commit };

        //5. The commit stands whatever the push does. A refused push is not a failed commit, and
        //   the outcome carries both so the UI can say so.
        CommitFlowResult push = await PushAsync(request, cancellationToken).ConfigureAwait(false);
        return push with { Commit = commit };
    }

    /// <summary>
    /// Switches to or creates the target branch, then verifies that nothing the user selected
    /// moved as a result.
    /// </summary>
    /// <returns>Null when the commit may proceed; otherwise the reason it may not.</returns>
    private async Task<CommitFlowResult?> ApplyBranchAsync(
        CommitRequest request,
        CancellationToken cancellationToken)
    {
        //Committing to the branch already checked out performs no switch at all -- the normal
        //case, and it must cost nothing.
        if (request.TargetBranch is not { Length: > 0 } target)
            return null;

        RepositoryInfo repository = request.Repository;

        if (request.CreateBranch)
        {
            //Authoritative validation, once, before anything is created. The offline check that
            //drives the ComboBox's live hint is not the last word.
            BranchNameValidation validation = await branches
                .ValidateAsync(repository, target, cancellationToken)
                .ConfigureAwait(false);

            if (!validation.IsValid)
            {
                return new CommitFlowResult(CommitFlowOutcome.InvalidBranchName)
                {
                    Detail = validation.Error,
                    Branch = target,
                };
            }
        }

        //The baseline is taken *after* staging and *before* switching, so the comparison isolates
        //what the switch did. Comparing against the pre-staging snapshot would flag every file,
        //because staging is itself a status change.
        RepositoryStatus before = await status.GetStatusAsync(repository, cancellationToken).ConfigureAwait(false);
        MarkSelected(before, request.SelectedPaths);

        SwitchOutcome switched = request.CreateBranch
            ? await switches.CreateAsync(repository, target, cancellationToken).ConfigureAwait(false)
            : await switches.SwitchAsync(repository, target, cancellationToken).ConfigureAwait(false);

        if (!switched.Succeeded)
        {
            //Refused. Nothing was modified or discarded, and the stash path is a deliberate choice
            //the user makes in the Switch branch window -- never taken here on their behalf.
            return new CommitFlowResult(CommitFlowOutcome.SwitchRefused)
            {
                Files = switched.BlockingFiles,
                Detail = switched.GitError,
                Branch = target,
            };
        }

        RepositoryStatus after = await status.GetStatusAsync(repository, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> moved = StatusComparer.SelectedFilesThatChanged(before, after);

        if (moved.Count == 0)
            return null;

        //Abort and hand back the refreshed status rather than committing something the user has
        //not seen. The switch itself stands: it succeeded, and undoing it would be a second
        //surprise.
        log.Info($"Commit aborted: {moved.Count} selected file(s) changed when switching to {target}.");

        return new CommitFlowResult(CommitFlowOutcome.AbortedSelectionChanged)
        {
            Files = moved,
            RefreshedStatus = after,
            Branch = target,
        };
    }

    /// <summary>
    /// Pushes, after the guardrails. Every refusal returns without having run `git push`.
    /// </summary>
    private async Task<CommitFlowResult> PushAsync(CommitRequest request, CancellationToken cancellationToken)
    {
        RepositoryInfo repository = request.Repository;

        RepositoryStatus current = await status.GetStatusAsync(repository, cancellationToken).ConfigureAwait(false);
        PushPlan plan = await pushes.PlanAsync(repository, current, cancellationToken).ConfigureAwait(false);

        switch (plan.Action)
        {
            case PushAction.Refuse:
                //Diverged, detached, no remote. Nothing attempted.
                return new CommitFlowResult(CommitFlowOutcome.PushRefused)
                {
                    Detail = plan.Reason,
                    Branch = plan.Branch,
                };

            case PushAction.NothingToPush:
                return new CommitFlowResult(CommitFlowOutcome.Committed)
                {
                    Detail = plan.Reason,
                    Branch = plan.Branch,
                    Upstream = plan.Upstream,
                };

            case PushAction.PullThenPush:
            {
                //Offered as one action rather than letting the push fail, but the pull is explicit:
                //it hits the network and can stop on a rebase conflict.
                if (!await Ask(request, new CommitFlowQuestion(CommitFlowQuestionKind.PullBeforePush, plan.Branch, plan.Upstream, plan.Remote), cancellationToken).ConfigureAwait(false))
                    return new CommitFlowResult(CommitFlowOutcome.Cancelled) { Branch = plan.Branch };

                PullOutcome pulled = await pulls
                    .PullRebaseAsync(repository, progress: null, cancellationToken)
                    .ConfigureAwait(false);

                if (!pulled.Succeeded)
                {
                    return new CommitFlowResult(CommitFlowOutcome.PullFailed)
                    {
                        Detail = pulled.GitError,
                        Suggestion = pulled.Suggestion,
                        Branch = plan.Branch,
                    };
                }

                //Re-planned from a fresh status: the pull moved the branch, so the previous plan is
                //stale.
                return await PushAsync(request, cancellationToken).ConfigureAwait(false);
            }

            case PushAction.SetUpstream:
            {
                if (!await Ask(request, new CommitFlowQuestion(CommitFlowQuestionKind.CreateUpstream, plan.Branch, plan.Upstream, plan.Remote), cancellationToken).ConfigureAwait(false))
                    return new CommitFlowResult(CommitFlowOutcome.Cancelled) { Branch = plan.Branch };

                break;
            }
        }

        PushOutcome outcome = await pushes.ExecuteAsync(repository, plan, cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return new CommitFlowResult(outcome.Refused ? CommitFlowOutcome.PushRefused : CommitFlowOutcome.PushFailed)
            {
                Detail = outcome.Error,
                Branch = plan.Branch,
            };
        }

        return new CommitFlowResult(CommitFlowOutcome.Committed)
        {
            Pushed = true,
            Branch = plan.Branch,
            Upstream = plan.Upstream ?? plan.Remote,
        };
    }

    /// <summary>
    /// Puts a guardrail question to the caller. No answer means no: a guardrail that treated
    /// silence as consent would not be one.
    /// </summary>
    private static Task<bool> Ask(CommitRequest request, CommitFlowQuestion question, CancellationToken cancellationToken) =>
        request.Confirm?.Invoke(question, cancellationToken) ?? Task.FromResult(false);

    /// <summary>
    /// Copies the caller's selection onto a freshly fetched status, so
    /// <see cref="StatusComparer"/> knows which files matter.
    /// </summary>
    private static void MarkSelected(RepositoryStatus status, IReadOnlyList<string> selectedPaths)
    {
        var selected = selectedPaths.ToHashSet(StringComparer.Ordinal);

        foreach (GitFileChange file in status.Files)
            file.IsSelected = selected.Contains(file.Path);
    }
}

/// <summary>What the user asked the commit surface to do.</summary>
public sealed record CommitRequest
{
    public required RepositoryInfo Repository { get; init; }

    public required string Message { get; init; }

    /// <summary>The ticked files. These, and only these, are staged.</summary>
    public required IReadOnlyList<string> SelectedPaths { get; init; }

    /// <summary>Files already in the index that the user unticked, so they come back out of it.</summary>
    public IReadOnlyList<string> PathsToUnstage { get; init; } = [];

    /// <summary>
    /// The branch to commit on, or null to stay on the one already checked out. Null is the normal
    /// case and costs no Git call at all.
    /// </summary>
    public string? TargetBranch { get; init; }

    /// <summary>True when <see cref="TargetBranch"/> does not exist yet and must be created.</summary>
    public bool CreateBranch { get; init; }

    public bool Push { get; init; }

    /// <summary>
    /// Answers a guardrail question. Null means every question is answered "no", which is what
    /// makes a headless caller safe by default.
    /// </summary>
    public Func<CommitFlowQuestion, CancellationToken, Task<bool>>? Confirm { get; init; }

    /// <summary>
    /// Derives a request from a status and the ticks already on it.
    ///
    /// The two path lists are the dangerous part. Deriving them here rather than in the view model
    /// is what keeps <see cref="PathsToUnstage"/> out of reach of a surface that could get it wrong
    /// — and getting that wrong commits a file the user deliberately unticked, because `git commit`
    /// commits the index and not the selection.
    ///
    /// <paramref name="status"/> carries the selection: <see cref="GitFileChange.IsSelected"/> is
    /// set by <c>StatusService</c> to the safe defaults and then by the user's ticks.
    ///
    /// There are three states, not two. <see cref="GitFileChange.HasChosenHunks"/> means the index
    /// already holds precisely what the user picked, so the file belongs in neither list — staging it
    /// would swallow the hunks they left out, and unstaging it would discard the ones they kept.
    /// </summary>
    public static CommitRequest From(
        RepositoryInfo repository,
        RepositoryStatus status,
        string message,
        string? targetBranch,
        bool createBranch,
        bool push,
        Func<CommitFlowQuestion, CancellationToken, Task<bool>>? confirm) =>
        new()
        {
            Repository = repository,
            Message = message,
            //Ticked, minus two kinds that `git add` must not be run on:
            //
            //  - files whose index content the user chose hunk by hunk, because adding the whole file
            //    would swallow the hunks they left out;
            //  - files whose deletion is already staged, because there is nothing left for a pathspec
            //    to match and git fails the whole command -- see GitFileChange.IsDeletionStaged.
            //
            //Both are already in the index exactly as the user wants them, so both are simply left
            //alone rather than handled.
            SelectedPaths =
            [
                .. status.Files
                    .Where(f => f.IsSelected && !f.HasChosenHunks && !f.IsDeletionStaged)
                    .Select(f => f.Path)
            ],

            //Unticked but in the index. Without this the untick would do nothing at all -- `git
            //commit` commits the index, not the selection. Files staged hunk by hunk are excluded
            //here too: unstaging one would discard the very hunks the user picked.
            PathsToUnstage =
            [
                .. status.Files.Where(f => !f.IsSelected && f.IsStaged && !f.HasChosenHunks).Select(f => f.Path)
            ],

            TargetBranch = targetBranch,
            CreateBranch = createBranch,
            Push = push,
            Confirm = confirm,
        };
}

public enum CommitFlowQuestionKind
{
    /// <summary>The branch has no upstream. Creating one publishes it.</summary>
    CreateUpstream,

    /// <summary>The branch is behind. Pull with rebase first?</summary>
    PullBeforePush,
}

/// <param name="Kind">Which question.</param>
/// <param name="Branch">The local branch, for the wording.</param>
/// <param name="Upstream">Its upstream, when it has one.</param>
/// <param name="Remote">The remote an upstream would be created on.</param>
public sealed record CommitFlowQuestion(
    CommitFlowQuestionKind Kind,
    string? Branch,
    string? Upstream,
    string? Remote);

public enum CommitFlowOutcome
{
    /// <summary>The commit landed. It may or may not have been pushed — see <see cref="CommitFlowResult.Pushed"/>.</summary>
    Committed,

    /// <summary>Git would reject the branch name. Nothing was created.</summary>
    InvalidBranchName,

    /// <summary>Git refused the switch because of local changes. Nothing was modified or discarded.</summary>
    SwitchRefused,

    /// <summary>The switch moved files the user had selected, so the commit was abandoned.</summary>
    AbortedSelectionChanged,

    /// <summary>Nothing was staged once the selection was reconciled.</summary>
    NothingToCommit,

    /// <summary>Committed, then the push was declined for safety. Nothing was pushed.</summary>
    PushRefused,

    /// <summary>Committed, then the push reached the network and failed.</summary>
    PushFailed,

    /// <summary>Committed, then the pull that had to precede the push failed.</summary>
    PullFailed,

    /// <summary>The user answered no to a guardrail question.</summary>
    Cancelled,
}

/// <param name="Outcome">What happened.</param>
public sealed record CommitFlowResult(CommitFlowOutcome Outcome)
{
    /// <summary>Set once the commit exists, including when a later step failed.</summary>
    public CommitResult? Commit { get; init; }

    /// <summary>Git's own words, or a validation message. Never a paraphrase.</summary>
    public string? Detail { get; init; }

    /// <summary>A next action, when there is a specific one.</summary>
    public string? Suggestion { get; init; }

    /// <summary>Blocking files for a refused switch, or moved files for an abort.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>Set on an abort, so the caller can re-render the list the user must now look at.</summary>
    public RepositoryStatus? RefreshedStatus { get; init; }

    public string? Branch { get; init; }

    public string? Upstream { get; init; }

    public bool Pushed { get; init; }

    public bool Succeeded => Outcome == CommitFlowOutcome.Committed;
}
