namespace FlickGit.Forges;

/// <summary>
/// Which service hosts the repository, and so which API creates a pull request on it.
///
/// Two, because two are supported. There is no <c>GitLab</c>, <c>Bitbucket</c> or <c>Gitea</c> member
/// waiting for an implementation: a value nothing can act on is a value the UI has to have a branch
/// for anyway — Hard Requirement 2.
/// </summary>
public enum ForgeKind
{
    /// <summary>
    /// The host is not one this build recognises, so nothing is guessed.
    ///
    /// A self-hosted instance can be named outright with <c>flickgit.forge</c> in the repository's
    /// own config, which is the cost the "cloud and self-hosted" answer accepted: <c>git.acme.io</c>
    /// is a GitLab or a GitHub Enterprise with equal probability, and picking one would post a
    /// request shaped for the wrong API at whatever is listening.
    /// </summary>
    Unknown,

    GitHub,
    AzureDevOps,
}

/// <summary>
/// A remote URL, resolved to the thing an API can be asked about.
///
/// One record for two services whose identifiers do not line up: GitHub names a repository with two
/// segments, and Azure DevOps with three levels that are genuinely three levels — an organization
/// holds projects and a project holds repositories. Rather than two records with one client each, the
/// fields are named for the wider of them and <see cref="Project"/> is empty for the other.
/// </summary>
/// <param name="Kind">Which API to speak.</param>
/// <param name="Host">The host as the remote spells it, lower-cased. What a stored token is keyed by.</param>
/// <param name="ApiBase">
/// Everything before the per-request path, with a trailing slash: <c>https://api.github.com/</c>,
/// or for Azure DevOps the <i>collection</i> URL <c>https://dev.azure.com/org/</c>. Derived rather
/// than hard-coded, which is what makes GitHub Enterprise and Azure DevOps Server work without a
/// second code path.
/// </param>
/// <param name="Owner">The GitHub owner, or the Azure DevOps organization.</param>
/// <param name="Project">The Azure DevOps project. Empty for the other two.</param>
/// <param name="Name">The repository.</param>
public sealed record ForgeRepository(
    ForgeKind Kind,
    string Host,
    Uri ApiBase,
    string Owner,
    string Project,
    string Name)
{
    /// <summary>What the window's header shows: enough to tell two repositories apart at a glance.</summary>
    public string Display => Kind == ForgeKind.AzureDevOps
        ? $"{Owner}/{Project}/{Name}"
        : $"{Owner}/{Name}";


    public override string ToString() => $"{Kind} {Display}";
}
