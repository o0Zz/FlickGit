using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Remotes;
using FlickGit.Repositories;

namespace FlickGit.Forges;

/// <summary>Which client speaks to a given service.</summary>
/// <remarks>
/// It takes the clients as an <see cref="IEnumerable{T}"/> and matches on
/// <see cref="IPullRequestClient.Kind"/> rather than naming three concrete types, which is what
/// lets <see cref="PullRequestFlow"/> be tested at all: asserting "push, then create" needs a
/// client that records what it was asked rather than one that opens a socket.
/// </remarks>
public sealed class PullRequestClients(IEnumerable<IPullRequestClient> clients)
{
    private readonly IReadOnlyList<IPullRequestClient> _clients = [.. clients];

    public IPullRequestClient? For(ForgeKind kind) => _clients.FirstOrDefault(client => client.Kind == kind);
}

public enum PullRequestFlowResult
{
    Created,

    /// <summary>One was already open for this source and target. Nothing was created.</summary>
    AlreadyOpen,

    Refused,

    /// <summary>Attempted and failed. The push may have happened; <see cref="PullRequestFlowOutcome.Pushed"/> says.</summary>
    Failed,

    Cancelled,
}

/// <param name="Pushed">
/// True when this flow pushed the branch. Reported separately from the result, because a failure
/// after a successful push leaves the branch published and the request not open, which the user
/// needs to know.
/// </param>
public sealed record PullRequestFlowOutcome(
    PullRequestFlowResult Result,
    PullRequestRef? Request,
    string? Message,
    bool Pushed)
{
    public static PullRequestFlowOutcome Refused(string reason) => new(PullRequestFlowResult.Refused, null, reason, false);

    public static PullRequestFlowOutcome Cancelled() => new(PullRequestFlowResult.Cancelled, null, null, false);
}

/// <summary>
/// Publish the branch, then open the request. In that order, and never the other way round: a
/// request opened before the push is a request against commits the server has never seen, which
/// every forge answers with a 404 about a branch the user is looking at.
///
/// Three rules it holds:
///
/// <list type="bullet">
/// <item><description><b>The push goes through <see cref="PushService"/>, not around it.</b> A
/// diverged branch is refused here exactly as from the commit window, and force-push is not
/// reachable -- there is no argument list built here at all.</description></item>
/// <item><description><b>Creating an upstream is consent</b>, asked through the same callback the
/// commit surface uses and remembered per repository.</description></item>
/// <item><description><b>Nothing is retried silently.</b> One re-authorisation, only when the
/// service said the credential was the problem.</description></item>
/// </list>
/// </summary>
public sealed class PullRequestFlow(
    PushService push,
    PullRequestClients clients,
    RepositoryService repositories,
    ILog log)
{
    /// <param name="token">
    /// Produces the credential. <c>true</c> asks for a fresh one. Null means the user declined, which
    /// ends the flow without an error.
    /// </param>
    /// <param name="consent">
    /// Answers <see cref="PushAction.SetUpstream"/>. The same shape <c>UpstreamConsent</c> already
    /// satisfies for the commit surface, so the answer is asked once per repository.
    /// </param>
    /// <param name="progress">
    /// Names the step about to run. A pull request is up to three network round trips, and silence
    /// for four seconds reads as a hang.
    /// </param>
    public async Task<PullRequestFlowOutcome> CreateAsync(
        RepositoryInfo repository,
        RepositoryStatus status,
        ForgeRepository forge,
        PullRequestDraft draft,
        Func<bool, Task<string?>> token,
        Func<PushPlan, Task<bool>> consent,
        IProgress<PullRequestStep>? progress,
        CancellationToken cancellationToken)
    {
        if (draft.Title.Trim().Length == 0)
            return PullRequestFlowOutcome.Refused("A pull request needs a title.");

        if (clients.For(forge.Kind) is not { } client)
            return PullRequestFlowOutcome.Refused($"FlickGit cannot open a pull request on {forge.Kind}.");

        //Step one, and first for a reason that is not style: everything below asks a server about a
        //branch, and until this has run the server does not have one.
        PublishResult publish = await PublishAsync(
            repository, status, consent, progress, cancellationToken).ConfigureAwait(false);

        //Declining to publish a branch is an answer, not a failure -- and it has to stop the flow. Its
        //own field rather than an error with a null message, because reading "no error" as "carry on" is
        //exactly how a declined consent ended up creating the request anyway.
        if (publish.Declined)
            return PullRequestFlowOutcome.Cancelled();

        if (publish.Error is { } pushError)
            return new PullRequestFlowOutcome(PullRequestFlowResult.Refused, null, pushError, false);

        bool pushed = publish.Pushed;

        progress?.Report(PullRequestStep.Authorising);

        if (await token(false).ConfigureAwait(false) is not { Length: > 0 } secret)
            return PullRequestFlowOutcome.Cancelled() with { Pushed = pushed };

        progress?.Report(PullRequestStep.Checking);

        //Before creating, because all three services refuse a duplicate with a status code and none of
        //them says where the existing one is.
        if (await client
                .FindOpenAsync(forge, draft.SourceBranch, draft.TargetBranch, secret, cancellationToken)
                .ConfigureAwait(false) is { } existing)
        {
            return new PullRequestFlowOutcome(PullRequestFlowResult.AlreadyOpen, existing, null, pushed);
        }

        progress?.Report(PullRequestStep.Creating);

        PullRequestOutcome outcome = await client
            .CreateAsync(forge, draft, secret, cancellationToken)
            .ConfigureAwait(false);

        if (outcome.Unauthorised)
        {
            //One retry, and only for this. A token from the credential helper can be stale in a way nothing
            //local can detect, and the remedy is what the flow would do next time anyway. Any other failure
            //is reported as it stands: retrying a rejected request would be guessing.
            log.Info($"{forge.Kind} refused the credential for {forge.Host}; asking for a new one.");

            if (await token(true).ConfigureAwait(false) is not { Length: > 0 } replacement)
                return new PullRequestFlowOutcome(PullRequestFlowResult.Failed, null, outcome.Error, pushed);

            outcome = await client.CreateAsync(forge, draft, replacement, cancellationToken).ConfigureAwait(false);
        }

        if (!outcome.Succeeded || outcome.Request is null)
            return new PullRequestFlowOutcome(PullRequestFlowResult.Failed, null, outcome.Error, pushed);

        return new PullRequestFlowOutcome(PullRequestFlowResult.Created, outcome.Request, null, pushed);
    }

    /// <summary>
    /// The request already open for this source and target, or null. Exposed here rather than leaving
    /// the window to reach a client directly, so the window keeps knowing only about a forge and a
    /// flow. The token is passed in because the caller has one that must not be asked for.
    /// </summary>
    public Task<PullRequestRef?> FindOpenAsync(
        ForgeRepository forge,
        string sourceBranch,
        string targetBranch,
        string token,
        CancellationToken cancellationToken) =>
        clients.For(forge.Kind) is { } client
            ? client.FindOpenAsync(forge, sourceBranch, targetBranch, token, cancellationToken)
            : Task.FromResult<PullRequestRef?>(null);

    /// <summary>
    /// Gets the branch onto the remote, or says why it cannot be. Every decision belongs to
    /// <see cref="PushService"/> -- this only translates its plan into "carry on" or "stop, and here
    /// is the reason", which is what keeps this surface from being a way around the push guardrails.
    /// </summary>
    private async Task<PublishResult> PublishAsync(
        RepositoryInfo repository,
        RepositoryStatus status,
        Func<PushPlan, Task<bool>> consent,
        IProgress<PullRequestStep>? progress,
        CancellationToken cancellationToken)
    {
        PushPlan plan = await push.PlanAsync(repository, status, cancellationToken).ConfigureAwait(false);

        switch (plan.Action)
        {
            case PushAction.NothingToPush:
                //Already published and level with its upstream. The ordinary case for a second attempt.
                return PublishResult.NothingToDo;

            case PushAction.Refuse:
                return PublishResult.Stop(plan.Reason);

            case PushAction.PullThenPush:
                //Behind its own upstream means somebody else has pushed to this branch. Proposing without those
                //commits would open a request missing work already published under the same branch name.
                return PublishResult.Stop(
                    $"{plan.Branch} is behind {plan.Upstream}, so it cannot be pushed as it stands.\n\n"
                    + "Pull first, then open the pull request.");

            case PushAction.SetUpstream when !await consent(plan).ConfigureAwait(false):
                return PublishResult.UserDeclined;
        }

        progress?.Report(PullRequestStep.Pushing);

        PushOutcome outcome = await push.ExecuteAsync(repository, plan, cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
            return PublishResult.Stop(outcome.Error);

        //The ahead/behind counts the window was drawn from are now wrong, and the next surface to ask
        //must not be told the branch is still unpushed.
        repositories.Invalidate(repository.Root);

        return new PublishResult(true, null, false);
    }

    /// <param name="Declined">
    /// The user said no to publishing. Distinct from an error with no message, which the flow read as
    /// "nothing went wrong, carry on" -- creating the request against a branch the user had just
    /// refused to push.
    /// </param>
    private readonly record struct PublishResult(bool Pushed, string? Error, bool Declined)
    {
        public static readonly PublishResult NothingToDo = new(false, null, false);

        public static readonly PublishResult UserDeclined = new(false, null, true);

        public static PublishResult Stop(string? reason) => new(false, reason ?? string.Empty, false);
    }
}

/// <summary>
/// Which round trip is in flight, for the status line. An enum rather than a string, so the words
/// live in the language files -- <c>FlickGit.Core</c> has no string table and should not grow one.
/// </summary>
public enum PullRequestStep
{
    Pushing,
    Authorising,
    Checking,
    Creating,
}
