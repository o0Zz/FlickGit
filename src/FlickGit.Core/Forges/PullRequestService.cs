using FlickGit.Branches;
using FlickGit.Config;
using FlickGit.Git;
using FlickGit.History;
using FlickGit.Models;

namespace FlickGit.Forges;

/// <summary>
/// What a pull request from this repository would be, before anybody has pressed anything.
///
/// <see cref="Refusal"/> and everything else are mutually exclusive: either there is something to
/// propose or there is a sentence saying why not. A window handed one of these has no decisions left
/// to make about whether the feature applies.
/// </summary>
/// <param name="Refusal">Why nothing can be proposed. Null when there is.</param>
/// <param name="Forge">The repository, on its API.</param>
/// <param name="Remote">The remote it was resolved from — <c>origin</c>, usually.</param>
/// <param name="SourceBranch">The current branch.</param>
/// <param name="TargetBranch">Where it would go, resolved but still editable.</param>
/// <param name="TargetCandidates">
/// The branches that exist on <paramref name="Remote"/>, short. What the target box offers — and
/// deliberately not the local branches: a target that is not on the server is a request no service
/// will accept.
/// </param>
public sealed record PullRequestPlan(
    string? Refusal,
    ForgeRepository? Forge,
    string Remote,
    string SourceBranch,
    string TargetBranch,
    IReadOnlyList<string> TargetCandidates)
{
    public static PullRequestPlan Refuse(string reason) =>
        new(reason, null, string.Empty, string.Empty, string.Empty, []);

    public bool CanPropose => Refusal is null && Forge is not null;
}

/// <summary>
/// What the request would contain: its commits, its files, and the point they diverged from.
/// </summary>
/// <param name="MergeBase">
/// Where the branches parted. The left side of the diff — which is what a forge shows, and what
/// <c>git diff target...HEAD</c> means. Using the target's tip instead would attribute every commit
/// made on the target since the branch started to this request.
/// </param>
public sealed record PullRequestSummary(
    string MergeBase,
    IReadOnlyList<LogCommit> Commits,
    IReadOnlyList<GitFileChange> Files)
{
    public static readonly PullRequestSummary Empty = new(string.Empty, [], []);

    public int Added => Files.Sum(f => f.AddedLines ?? 0);

    public int Removed => Files.Sum(f => f.RemovedLines ?? 0);
}

/// <summary>
/// Everything a pull request needs to know that is not the API call itself: which forge, which
/// branches, and what is in it.
///
/// <b>Local only.</b> Nothing here touches the network — the remote list is a config read, the
/// branch list is <c>for-each-ref</c> over refs already fetched, and the merge base is a walk of the
/// object database. That is the same rule <c>PushService.PlanAsync</c> follows and for the same
/// reason: the window has to paint before anything slow happens, and the one thing that must never
/// gate it is a round trip.
/// </summary>
public sealed class PullRequestService(
    IGitProcessRunner git,
    RepositoryConfigService config,
    BranchService branches,
    HistoryService history)
{
    /// <summary>
    /// How many commits are read for the summary and the description.
    ///
    /// A branch with more than this is not one whose description is improved by reading the rest —
    /// and the AI payload is capped by bytes long before it is capped by commits.
    /// </summary>
    private const int MaxCommits = 100;

    /// <summary>
    /// Resolves the forge, the source and the target, or the reason there is nothing to propose.
    /// </summary>
    /// <param name="configuredPrimaryBranch">
    /// The user's global setting, passed through to <see cref="BranchService"/> — this class does not
    /// read settings, because <c>FlickGit.Core</c> does not know where they live.
    /// </param>
    public async Task<PullRequestPlan> PlanAsync(
        RepositoryInfo repository,
        RepositoryStatus status,
        string? configuredPrimaryBranch,
        CancellationToken cancellationToken)
    {
        if (status.IsUnborn)
            return PullRequestPlan.Refuse("This branch has no commits yet, so there is nothing to propose.");

        if (status.IsDetachedHead || status.Branch is not { Length: > 0 } source)
        {
            return PullRequestPlan.Refuse(
                "HEAD is detached, so there is no branch to propose.\n\nSwitch to a branch first.");
        }

        RepositoryConfig local = await config.ReadAsync(repository, cancellationToken).ConfigureAwait(false);

        if (ResolveRemote(local) is not { } remote)
        {
            return PullRequestPlan.Refuse(
                $"{repository.Name} has no remote, so there is nowhere to open a pull request.\n\n"
                + "Add one with:\n\ngit remote add origin <url>");
        }

        ForgeKind hint = ForgeUrl.ParseKind(
            await config.ReadForgeKindAsync(repository, cancellationToken).ConfigureAwait(false));

        //The *push* URL when the remote has a separate one, and the fetch URL otherwise.
        //
        //That is not a preference, it is the only answer that can be right: `git push` sends the
        //branch to `remote.<name>.pushurl` when one is set, so the request has to be opened on
        //whatever project that names. Reading the fetch URL instead breaks the one workflow where
        //the two differ -- fetch from upstream, push to a fork -- by pushing the branch to the fork
        //and then asking upstream to review a branch it has never heard of.
        string url = remote.PushUrl ?? remote.FetchUrl;

        if (ForgeUrl.TryParse(url, hint) is not { } forge)
        {
            //Named rather than guessed, and the message carries the fix. A self-hosted instance is
            //the ordinary case for this, not an exotic one.
            return PullRequestPlan.Refuse(
                $"FlickGit does not recognise {remote.Name} as GitHub, GitLab or Azure DevOps:\n\n"
                + $"{url}\n\n"
                + "If it is a self-hosted instance of one of them, say so once:\n\n"
                + $"git config --local {RepositoryConfigService.ForgeKindKey} gitlab");
        }

        string target = await ResolveTargetAsync(repository, configuredPrimaryBranch, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<string> candidates = await RemoteBranchesAsync(repository, remote.Name, cancellationToken)
            .ConfigureAwait(false);

        if (string.Equals(source, target, StringComparison.Ordinal))
        {
            //Not a Git error and not a forge error -- a service would accept it and then refuse at
            //merge time. Caught here, where it can say what to do instead.
            return PullRequestPlan.Refuse(
                $"You are on {source}, which is where a pull request would go.\n\n"
                + "Switch to the branch you want to propose first.");
        }

        return new PullRequestPlan(null, forge, remote.Name, source, target, candidates);
    }

    /// <summary>
    /// The commits and files the request would carry.
    ///
    /// Separate from <see cref="PlanAsync"/> because the target is editable: changing it in the
    /// window changes this and nothing else, so re-running the whole plan would re-resolve a forge
    /// that cannot have changed.
    /// </summary>
    public async Task<PullRequestSummary> SummariseAsync(
        RepositoryInfo repository,
        string remote,
        string targetBranch,
        CancellationToken cancellationToken)
    {
        string remoteRef = $"{remote}/{targetBranch}";

        //Two dots for the log, three-dots' base for the diff. `<target>..HEAD` is exactly the
        //commits this branch adds; the diff wants the merge base, or every commit made on the target
        //since the branch started would show up as this request's work.
        Task<IReadOnlyList<LogCommit>> commits =
            history.GetRangeAsync(repository, $"{remoteRef}..HEAD", MaxCommits, cancellationToken);

        string mergeBase = await MergeBaseAsync(repository, remoteRef, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<GitFileChange> files = mergeBase.Length > 0
            ? await history.GetFilesAsync(repository, mergeBase, "HEAD", cancellationToken).ConfigureAwait(false)
            : [];

        return new PullRequestSummary(mergeBase, await commits.ConfigureAwait(false), files);
    }

    /// <summary>
    /// Where the two branches parted, as a bare object id.
    ///
    /// Empty when the target is not known locally, which is what a branch the user has never fetched
    /// looks like. The window then shows no summary rather than a wrong one, and the create still
    /// works — the server computes its own base from refs it certainly has.
    /// </summary>
    private async Task<string> MergeBaseAsync(
        RepositoryInfo repository,
        string remoteRef,
        CancellationToken cancellationToken)
    {
        GitResult result = await git
            .ReadAsync(repository.Root, ["merge-base", remoteRef, "HEAD"], cancellationToken)
            .ConfigureAwait(false);

        return result.Succeeded ? result.StdOut.Trim() : string.Empty;
    }

    /// <summary>
    /// Which remote to open against.
    ///
    /// The branch's own tracked remote first — a branch pushed to a fork should propose from the
    /// fork — then <c>origin</c>, then whatever single remote exists. With several unnamed remotes
    /// and no tracking, the first is taken rather than refusing: the window names it, and the user
    /// can see it is wrong.
    /// </summary>
    private static GitRemote? ResolveRemote(RepositoryConfig config)
    {
        if (config.Remotes.Count == 0)
            return null;

        if (config.TrackedRemote is { Length: > 0 } tracked
            && config.Remotes.FirstOrDefault(r => r.Name == tracked) is { } byTracking)
        {
            return byTracking;
        }

        return config.Remotes.FirstOrDefault(r => r.Name == "origin") ?? config.Remotes[0];
    }

    /// <summary>
    /// The default target: this repository's own <c>flickgit.pullRequestTarget</c>, and otherwise the
    /// primary branch the rest of the product already resolves.
    ///
    /// One resolution rule rather than two. <c>BranchService</c> already walks
    /// <c>flickgit.primaryBranch</c>, the user's setting, <c>origin/HEAD</c>, <c>main</c> and
    /// <c>master</c>, and a second walk here would be a second answer to "what is this repository's
    /// trunk" — free to disagree with the warning strip the commit window draws.
    /// </summary>
    private async Task<string> ResolveTargetAsync(
        RepositoryInfo repository,
        string? configuredPrimaryBranch,
        CancellationToken cancellationToken)
    {
        if (await config.ReadPullRequestTargetAsync(repository, cancellationToken).ConfigureAwait(false) is { } chosen)
            return chosen;

        return await branches
            .ResolvePrimaryBranchAsync(repository, configuredPrimaryBranch, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The branches on <paramref name="remote"/>, with the remote's own prefix taken off.
    ///
    /// <c>for-each-ref</c> rather than <c>git branch -r</c>, per CLAUDE.md: the porcelain command's
    /// output is column-padded for a terminal. <c>{remote}/HEAD</c> is dropped — it is a symbolic ref
    /// to another entry in the same list, so leaving it in would offer the same branch twice under
    /// two names.
    /// </summary>
    private async Task<IReadOnlyList<string>> RemoteBranchesAsync(
        RepositoryInfo repository,
        string remote,
        CancellationToken cancellationToken)
    {
        GitResult result = await git.ReadAsync(
            repository.Root,
            ["for-each-ref", "--format=%(refname:short)", $"refs/remotes/{remote}"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
            return [];

        string prefix = remote + "/";

        return
        [
            .. result.StdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
                .Select(line => line[prefix.Length..])
                .Where(name => name.Length > 0 && name != "HEAD")
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
        ];
    }
}
