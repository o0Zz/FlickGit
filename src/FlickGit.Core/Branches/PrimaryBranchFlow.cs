using FlickGit.Logging;
using FlickGit.Merges;
using FlickGit.Models;
using FlickGit.Pulls;
using FlickGit.Status;

namespace FlickGit.Branches;

/// <summary>
/// Back to the branch the day starts on, and up to date on it. One gesture, and <b>the order is the
/// substance</b>:
///
/// <code>
/// 1. read the status once     branch, detached-ness and the operation in progress arrive together
/// 2. refuse mid-operation     a switch during a rebase is not a switch, it is a second problem
/// 3. resolve the primary      BranchService's chain, and never a second rule here
/// 4. switch, or do not        already there is success with no Git command at all
/// 5. pull --rebase            the only step that touches the network, and the last one
/// </code>
///
/// Three of those five are refusals, which is the shape of the thing. What it never does is the list
/// worth reading twice: <b>it never forces, it never aborts a rebase, and it never stashes without
/// having been asked</b> — <see cref="PrimaryBranchRequest.Confirm"/> has a null default answered
/// "no", so a headless run leaves a dirty working tree exactly as it found it.
///
/// This is in Core with tests rather than in the window for the reason <c>CommitFlow</c> is: every
/// bug it can have is an ordering bug. Pulling before the switch pulls onto the branch the user is
/// leaving, and pulling after a restore that conflicted rebases onto a tree they have not got back —
/// both of which look identical from a click.
/// </summary>
public sealed class PrimaryBranchFlow(
    StatusService status,
    BranchService branches,
    SwitchService switches,
    PullService pulls,
    ILog log)
{
    public async Task<PrimaryBranchResult> RunAsync(
        PrimaryBranchRequest request,
        CancellationToken cancellationToken)
    {
        RepositoryInfo repository = request.Repository;

        //1. One status read. Everything the refusals below need arrives together -- the branch,
        //whether HEAD is detached, and the operation in progress, which StatusService folds in from
        //file probes over the Git directory at no process cost. Reading it first is what puts every
        //refusal before any command that could change something.
        RepositoryStatus state = await status
            .GetStatusAsync(repository, cancellationToken)
            .ConfigureAwait(false);

        //2. An operation in progress is refused before Git is asked anything at all. One check covers
        //the merge, the rebase, the cherry-pick and the revert, which is what the enum exists for.
        //
        //Refused here rather than left to `git switch`, because Git's own wording is about HEAD and
        //the index while ours can name the operation and its own --continue spelling. And refusing
        //*here* means nothing in this file can be read as an invitation to abort one.
        if (state.Merge.InProgress)
        {
            log.Info($"Back to the primary branch refused: a {MergeState.Verb(state.Merge.Operation)} is in progress.");

            return new PrimaryBranchResult(PrimaryBranchOutcome.OperationInProgress)
            {
                InProgress = state.Merge.Operation,
            };
        }

        //3. Resolve, after the refusal so that a resolution about to be thrown away costs no
        //`config --get`. The chain never returns null -- it ends at the literal "main" -- so there is
        //no null arm here, and above all no second rule: a repository with two default branches would
        //show the user whichever of them was wrong.
        string primary = await branches
            .ResolvePrimaryBranchAsync(repository, request.ConfiguredPrimaryBranch, cancellationToken)
            .ConfigureAwait(false);

        //4. Switch, or do not.
        //
        //Already there is success with no Git command at all, exactly as naming the current branch in
        //the commit window's ComboBox is. Ordinal, because Git ref names are case-sensitive.
        //
        //A detached HEAD makes state.Branch null, so this comparison is false and the switch runs --
        //deliberately. This is a state the entry *leaves* rather than one it refuses: the tag window's
        //`switch --detach` is the only thing in the product that produces it, and refusing here would
        //leave it with no one-click way out. Nothing is discarded by leaving, and the commit is
        //carried out on the result so the surface can say which one it was.
        bool alreadyThere = !state.IsDetachedHead
                            && string.Equals(state.Branch, primary, StringComparison.Ordinal);

        bool switched = false;
        bool stashed = false;

        if (!alreadyThere)
        {
            //A bare `switch <branch>`, and no `--track` fallback beside it. With no local branch of
            //that name and exactly one remote carrying it, Git's own DWIM creates a local branch
            //tracking the remote one; when that fails -- several remotes have it, or the user turned
            //checkout.guess off -- Git says so and names --track, and that lands in SwitchFailed with
            //Git's own words. Choosing a remote on the user's behalf would be a second resolution
            //rule, and the wrong guess switches to somebody else's branch of that name.
            SwitchOutcome attempt = await switches
                .SwitchAsync(repository, primary, cancellationToken)
                .ConfigureAwait(false);

            if (!attempt.Succeeded)
            {
                //A refusal with no named files is a different failure and must not lead the user to
                //the stash button -- SwitchOutcome.RefusedByLocalChanges says so itself.
                if (!attempt.RefusedByLocalChanges)
                {
                    return new PrimaryBranchResult(PrimaryBranchOutcome.SwitchFailed)
                    {
                        Branch = primary,
                        Detail = attempt.GitError,
                    };
                }

                //The one question this flow asks. No answer means no.
                bool consented = await Ask(
                    request,
                    new PrimaryBranchQuestion(primary, attempt.BlockingFiles),
                    cancellationToken).ConfigureAwait(false);

                if (!consented)
                {
                    return new PrimaryBranchResult(PrimaryBranchOutcome.SwitchRefused)
                    {
                        Branch = primary,
                        Files = attempt.BlockingFiles,
                        Detail = attempt.GitError,
                    };
                }

                SwitchOutcome viaStash = await switches
                    .StashSwitchRestoreAsync(repository, primary, cancellationToken)
                    .ConfigureAwait(false);

                if (!viaStash.Succeeded)
                {
                    //Stop before the pull, and this is the ordering decision that matters most here.
                    //When the failed step is the restore, the user is standing on the primary branch
                    //with their work in a stash -- and `pull --rebase` then rebases onto a tree they
                    //have not got back, with --autostash finding nothing dirty to protect. When it is
                    //the stash or the switch they are still where they started, and there is nothing
                    //to pull to.
                    return new PrimaryBranchResult(PrimaryBranchOutcome.StashSwitchFailed)
                    {
                        Branch = primary,
                        Detail = viaStash.GitError,
                        StashRef = viaStash.StashRef,
                        RestoreConflicted = viaStash.RestoreConflicted,
                        FailedStep = viaStash.FailedStep,
                    };
                }

                stashed = true;
            }

            switched = true;
        }

        //5. Pull. The caller's own IProgress is forwarded rather than intercepted, so PullService's
        //pull and its submodule update stay the two distinct steps the window draws.
        PullOutcome pulled = await pulls
            .PullRebaseAsync(repository, request.Progress, cancellationToken)
            .ConfigureAwait(false);

        //The one failure that arrives after the repository has already moved, which is what Branch,
        //Switched and Stashed are carried out for: "you are on develop now, and a rebase is waiting".
        if (!pulled.Succeeded)
        {
            return new PrimaryBranchResult(PrimaryBranchOutcome.PullFailed)
            {
                Branch = primary,
                Switched = switched,
                Stashed = stashed,
                LeftDetachedAt = switched && state.IsDetachedHead ? state.HeadCommit : null,
                StoppedOnConflict = pulled.StoppedOnConflict,
                Detail = pulled.GitError,
                Suggestion = pulled.Suggestion,
            };
        }

        //A submodule failure is a warning on a successful outcome, never a failure. Reporting it as
        //one would invite the user to try to undo a pull that worked.
        return new PrimaryBranchResult(PrimaryBranchOutcome.Done)
        {
            Branch = primary,
            Switched = switched,
            Stashed = stashed,
            LeftDetachedAt = switched && state.IsDetachedHead ? state.HeadCommit : null,
            SubmoduleError = pulled.SubmoduleError,
        };
    }

    /// <summary>
    /// Puts the stash question to the caller. No answer means no: nothing here may stash a working
    /// tree without having been told to, which is what makes a headless run safe.
    /// </summary>
    private static Task<bool> Ask(
        PrimaryBranchRequest request,
        PrimaryBranchQuestion question,
        CancellationToken cancellationToken) =>
        request.Confirm?.Invoke(question, cancellationToken) ?? Task.FromResult(false);
}

public sealed record PrimaryBranchRequest
{
    public required RepositoryInfo Repository { get; init; }

    /// <summary>
    /// The user's <c>primaryBranch</c> setting, or null. Passed in rather than read, because settings
    /// are an App concern and this assembly has no UI dependency.
    /// </summary>
    public string? ConfiguredPrimaryBranch { get; init; }

    /// <summary>Step labels, forwarded to <c>PullService</c> unchanged so its two steps stay two.</summary>
    public IProgress<string>? Progress { get; init; }

    /// <summary>
    /// Answers the stash question. <b>Null means "no"</b>, which is what makes a headless caller
    /// safe: nothing here stashes without having been told to.
    /// </summary>
    public Func<PrimaryBranchQuestion, CancellationToken, Task<bool>>? Confirm { get; init; }
}

/// <summary>
/// The one question this flow asks. <b>No <c>Kind</c> discriminator</b>, unlike the commit flow's:
/// there is exactly one question, and a one-member enum discriminates nothing. When a second arrives
/// the enum arrives with it.
/// </summary>
/// <param name="BlockingFiles">Git's own list. The question cannot honestly be asked without it.</param>
public sealed record PrimaryBranchQuestion(string Branch, IReadOnlyList<string> BlockingFiles);

public enum PrimaryBranchOutcome
{
    /// <summary>
    /// On the primary branch and up to date. <see cref="PrimaryBranchResult.SubmoduleError"/> may
    /// still be set: a stale submodule does not undo a pull that worked.
    /// </summary>
    Done,

    /// <summary>A merge, rebase, cherry-pick or revert is part-way through. Nothing was asked of Git.</summary>
    OperationInProgress,

    /// <summary>
    /// Git refused the switch over local changes, and the stash was declined or there was nobody to
    /// ask. Both collapse here on purpose: from the repository's point of view they are the same
    /// event, and both need the blocking-file list.
    /// </summary>
    SwitchRefused,

    /// <summary>The switch failed for a reason a stash cannot fix. Git's own words are in <c>Detail</c>.</summary>
    SwitchFailed,

    /// <summary>The stash, switch and restore was consented to and did not complete.</summary>
    StashSwitchFailed,

    /// <summary>On the primary branch; the pull did not complete. The switch stands.</summary>
    PullFailed,
}

public sealed record PrimaryBranchResult(PrimaryBranchOutcome Outcome)
{
    /// <summary>The branch the chain resolved. Empty only on <c>OperationInProgress</c>.</summary>
    public string Branch { get; init; } = string.Empty;

    /// <summary>False when the repository was already there — no <c>git switch</c> ran at all.</summary>
    public bool Switched { get; init; }

    /// <summary>True when the stash path was consented to and round-tripped.</summary>
    public bool Stashed { get; init; }

    /// <summary>
    /// The commit HEAD was detached at, when the switch left one behind. The only handle on anything
    /// committed there, so it is reported rather than swallowed — Git prints this warning itself, and
    /// a window that ate it would be the quieter of the two.
    /// </summary>
    public string? LeftDetachedAt { get; init; }

    /// <summary>The operation in progress, on that refusal only. Named by the surface, not here.</summary>
    public MergeOperation InProgress { get; init; }

    /// <summary>Git's own stderr. Never a paraphrase.</summary>
    public string? Detail { get; init; }

    /// <summary>The next-command sentence, when the step that failed produced one.</summary>
    public string? Suggestion { get; init; }

    /// <summary>The files Git named as blocking the switch. Empty otherwise.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>A stash of ours that still exists. Always shown: it is where the user's work is.</summary>
    public string? StashRef { get; init; }

    /// <summary>The switch happened and the stash could not be reapplied.</summary>
    public bool RestoreConflicted { get; init; }

    /// <summary>
    /// Which step of the stash path failed — the difference between "still where you were" and "on the
    /// branch, with your work in a stash".
    /// </summary>
    public SwitchStep FailedStep { get; init; }

    /// <summary>The pull stopped on conflicts rather than failing to start.</summary>
    public bool StoppedOnConflict { get; init; }

    /// <summary>Set on a successful pull whose submodule update failed. A warning, never a failure.</summary>
    public string? SubmoduleError { get; init; }

    public bool Succeeded => Outcome == PrimaryBranchOutcome.Done;
}
