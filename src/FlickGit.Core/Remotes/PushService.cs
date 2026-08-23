using FlickGit.Git;
using FlickGit.Models;
using FlickGit.Repositories;

namespace FlickGit.Remotes;

/// <summary>
/// Pushing, and refusing to push.
///
/// Split deliberately into <see cref="PlanAsync"/> and <see cref="ExecuteAsync"/>, because
/// CLAUDE.md requires the guardrails to be "checked <b>before</b> executing":
///
/// <list type="bullet">
/// <item><description><b>No upstream</b> — ask once, remember the answer per repository. The
/// asking belongs to the UI, so the plan reports it rather than deciding.</description></item>
/// <item><description><b>Behind the remote</b> — offer `pull --rebase --autostash` then push as
/// one button. "Do not push and let it fail."</description></item>
/// <item><description><b>Diverged</b> — stop. Never offer force-push, from any
/// surface.</description></item>
/// </list>
///
/// The plan is a value the UI can render and a test can assert on, which is the point: a
/// guardrail buried inside the method that also does the pushing is a guardrail nobody can
/// verify.
/// </summary>
public sealed class PushService(IGitProcessRunner git, RepositoryService repositories)
{
    /// <summary>
    /// Decides what pushing this branch would mean, without touching the network.
    ///
    /// Everything here is local: the ahead/behind counts came from the porcelain status that
    /// the commit window already ran, and the remote list is a config read. CLAUDE.md:
    /// "Explorer integration must never block on network operations."
    /// </summary>
    public async Task<PushPlan> PlanAsync(
        RepositoryInfo repository,
        RepositoryStatus status,
        CancellationToken cancellationToken)
    {
        if (status.IsDetachedHead || status.Branch is null)
        {
            return new PushPlan(
                PushAction.Refuse,
                "HEAD is detached, so there is no branch to push.\n\n" +
                "Switch to a branch first.");
        }

        if (status.IsUnborn)
        {
            return new PushPlan(
                PushAction.Refuse,
                "This branch has no commits yet, so there is nothing to push.");
        }

        GitResult remotes = await git.ReadAsync(
            repository.Root,
            ["remote"],
            cancellationToken).ConfigureAwait(false);

        string[] remoteNames = remotes.Succeeded
            ? remotes.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(r => r.Trim()).ToArray()
            : [];

        if (remoteNames.Length == 0)
        {
            return new PushPlan(
                PushAction.Refuse,
                $"{repository.Name} has no remote, so there is nowhere to push.\n\n" +
                "Add one with:\n\ngit remote add origin <url>");
        }

        if (status.Upstream is null)
        {
            //A new branch. The push itself is safe; what needs consent is creating the upstream,
            //because that publishes a branch name to a remote other people read.
            string remote = remoteNames.Contains("origin", StringComparer.Ordinal) ? "origin" : remoteNames[0];

            return new PushPlan(PushAction.SetUpstream, null)
            {
                Branch = status.Branch,
                Remote = remote,
            };
        }

        if (status.HasDiverged)
        {
            //The one case that stops outright. Reconciling means either a rebase or a
            //force-push, and force-push is never offered from here -- CLAUDE.md, "Safety Rules".
            return new PushPlan(
                PushAction.Refuse,
                $"{status.Branch} and {status.Upstream} have diverged: " +
                $"{status.Ahead} local commit(s) and {status.Behind} remote commit(s) differ.\n\n" +
                "Nothing has been pushed. Reconcile them first, for example with:\n\n" +
                "git pull --rebase")
            {
                Branch = status.Branch,
                Upstream = status.Upstream,
                HasDiverged = true,
            };
        }

        if (status.Behind > 0)
        {
            //Behind but not ahead-and-behind: a plain fast-forward pull fixes it, so the UI
            //offers pull-then-push as a single action rather than letting the push fail.
            return new PushPlan(PushAction.PullThenPush, null)
            {
                Branch = status.Branch,
                Upstream = status.Upstream,
            };
        }

        if (status.Ahead == 0)
        {
            return new PushPlan(
                PushAction.NothingToPush,
                $"{status.Branch} is already up to date with {status.Upstream}.")
            {
                Branch = status.Branch,
                Upstream = status.Upstream,
            };
        }

        return new PushPlan(PushAction.Push, null)
        {
            Branch = status.Branch,
            Upstream = status.Upstream,
        };
    }

    /// <summary>
    /// Runs the push the plan describes.
    ///
    /// Refuses to execute a plan that said no. That check is not paranoia about callers: it is
    /// what makes "Push is refused, with no state change, when the branch has diverged"
    /// testable at this layer instead of only through the UI.
    /// </summary>
    public async Task<PushOutcome> ExecuteAsync(
        RepositoryInfo repository,
        PushPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.Action is PushAction.Refuse or PushAction.NothingToPush)
            return new PushOutcome(false, plan.Reason, Refused: true);

        if (plan.Action == PushAction.PullThenPush)
        {
            //The caller is expected to have run the pull already, through PullService, and to
            //have re-planned afterwards. Executing a pull from inside the push would hide a
            //network operation and a possible rebase conflict behind a button labelled Push.
            return new PushOutcome(
                false,
                $"{plan.Branch} is behind {plan.Upstream}. Pull first, then push.",
                Refused: true);
        }

        List<string> args = plan.Action == PushAction.SetUpstream

            //`-u origin HEAD` rather than naming the branch: HEAD resolves to whatever is
            //checked out, so a branch created moments ago by the commit surface needs no second
            //lookup, and a branch name that looks like a path cannot be misread as one.
            ? ["push", "-u", plan.Remote ?? "origin", "HEAD"]
            : ["push"];

        GitResult result = await git.RunAsync(repository.Root, args, cancellationToken).ConfigureAwait(false);
        repositories.Invalidate(repository.Root);

        if (result.Succeeded)
        {
            return new PushOutcome(true, null, Refused: false)
            {
                CreatedUpstream = plan.Action == PushAction.SetUpstream,
            };
        }

        //A push that failed on the wire is reported with Git's own words. Authentication is the
        //common case and Git's credential-helper message is more useful than anything this
        //tool could write about it.
        return new PushOutcome(false, result.ErrorText, Refused: false);
    }
}

public enum PushAction
{
    /// <summary>Ordinary push to an existing upstream.</summary>
    Push,

    /// <summary>`push -u`. Needs consent once per repository.</summary>
    SetUpstream,

    /// <summary>Behind the remote. The UI offers pull --rebase --autostash then push.</summary>
    PullThenPush,

    /// <summary>Already up to date.</summary>
    NothingToPush,

    /// <summary>Stopped for safety, or impossible. <see cref="PushPlan.Reason"/> says why.</summary>
    Refuse,
}

/// <param name="Action">What pushing would do.</param>
/// <param name="Reason">Why, when the answer is no or nothing. Shown verbatim.</param>
public sealed record PushPlan(PushAction Action, string? Reason)
{
    public string? Branch { get; init; }
    public string? Upstream { get; init; }
    public string? Remote { get; init; }
    public bool HasDiverged { get; init; }
}

/// <param name="Succeeded">The push reached the remote.</param>
/// <param name="Error">Git's stderr, or the refusal reason.</param>
/// <param name="Refused">
/// True when FlickGit declined rather than Git failing. The distinction matters to the user:
/// refused means nothing was attempted and nothing changed.
/// </param>
public sealed record PushOutcome(bool Succeeded, string? Error, bool Refused)
{
    public bool CreatedUpstream { get; init; }
}
