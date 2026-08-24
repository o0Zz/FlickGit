namespace FlickGit.Forges;

/// <summary>
/// Which service hosts the repository, and so which API creates a pull request on it.
///
/// Three, because three are supported. There is no <c>Bitbucket</c> or <c>Gitea</c> member waiting
/// for an implementation: a value nothing can act on is a value the UI has to have a branch for
/// anyway — Hard Requirement 2.
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
    GitLab,
    AzureDevOps,
}

/// <summary>
/// A remote URL, resolved to the thing an API can be asked about.
///
/// One record for three services whose identifiers do not line up: GitHub names a repository with
/// two segments, GitLab with a namespace path of any depth, and Azure DevOps with three levels that
/// are genuinely three levels — an organization holds projects and a project holds repositories.
/// Rather than three records with one client each, the fields are named for the widest of them and
/// <see cref="Project"/> is empty for the two that have no such thing.
/// </summary>
/// <param name="Kind">Which API to speak.</param>
/// <param name="Host">The host as the remote spells it, lower-cased. What a stored token is keyed by.</param>
/// <param name="ApiBase">
/// Everything before the per-request path, with a trailing slash: <c>https://api.github.com/</c>,
/// <c>https://gitlab.example.com/api/v4/</c>, or for Azure DevOps the <i>collection</i> URL
/// <c>https://dev.azure.com/org/</c>. Derived rather than hard-coded, which is what makes GitHub
/// Enterprise and a self-managed GitLab work without a second code path.
/// </param>
/// <param name="Owner">The GitHub owner, the GitLab namespace path, or the Azure DevOps organization.</param>
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

    /// <summary>
    /// The GitLab project id: the full namespace path, URL-encoded whole.
    ///
    /// Encoded whole rather than segment by segment, because that is what GitLab's API wants —
    /// <c>group%2Fsub%2Fproject</c> is one path parameter, not three.
    /// </summary>
    public string EncodedPath => Uri.EscapeDataString($"{Owner}/{Name}");

    public override string ToString() => $"{Kind} {Display}";
}
