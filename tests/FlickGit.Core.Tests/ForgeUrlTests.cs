using FlickGit.Forges;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Turning a remote URL into the repository an API can be asked about.
///
/// In scope under Hard Requirement 4 as a <b>parser</b>, and it is the one in this feature where a
/// wrong answer is expensive: every other mistake shows up as an error message, and this one would
/// open a pull request against a real repository that is not the user's. The shapes below are the
/// ones three services and their self-hosted variants actually emit, which is exactly what a test
/// can cover and clicking cannot.
/// </summary>
public class ForgeUrlTests
{
    [Theory]
    //Cloud, in all three spellings a remote comes in.
    [InlineData("https://github.com/o0Zz/FlickGit.git", "o0Zz", "FlickGit", "https://api.github.com/")]
    [InlineData("git@github.com:o0Zz/FlickGit.git", "o0Zz", "FlickGit", "https://api.github.com/")]
    [InlineData("ssh://git@github.com/o0Zz/FlickGit.git", "o0Zz", "FlickGit", "https://api.github.com/")]

    //No .git suffix, which is what the browser's address bar gives.
    [InlineData("https://github.com/o0Zz/FlickGit", "o0Zz", "FlickGit", "https://api.github.com/")]

    //Enterprise: the API moves to /api/v3/ on the same host, which is the whole of the difference.
    [InlineData("https://github.acme.io/team/tools.git", "team", "tools", "https://github.acme.io/api/v3/")]
    public void GitHub_urls_resolve_to_owner_repository_and_api(string url, string owner, string name, string api)
    {
        ForgeRepository forge = Assert.IsType<ForgeRepository>(ForgeUrl.TryParse(url));

        Assert.Equal(ForgeKind.GitHub, forge.Kind);
        Assert.Equal(owner, forge.Owner);
        Assert.Equal(name, forge.Name);
        Assert.Equal(api, forge.ApiBase.ToString());
    }

    /// <summary>
    /// Azure DevOps' four shapes, and the collection URL each of them implies.
    ///
    /// The collection is what the REST API hangs off, so getting it wrong is not a cosmetic error —
    /// it is every request going to a URL that does not exist. The <c>_git</c> segment anchors the
    /// three web forms; the SSH one has no marker at all and is positional.
    /// </summary>
    [Theory]
    [InlineData("https://dev.azure.com/contoso/portal/_git/gateway",
        "contoso", "portal", "gateway", "https://dev.azure.com/contoso/")]

    //The organization appears in the userinfo as well as the path. The path is what is read.
    [InlineData("https://contoso@dev.azure.com/contoso/portal/_git/gateway",
        "contoso", "portal", "gateway", "https://dev.azure.com/contoso/")]

    //visualstudio.com: the organization is the host label and the collection is the host root.
    [InlineData("https://contoso.visualstudio.com/portal/_git/gateway",
        "contoso", "portal", "gateway", "https://contoso.visualstudio.com/")]

    //SSH: no _git, a leading v3 that is the protocol version, and the API on the web host.
    [InlineData("git@ssh.dev.azure.com:v3/contoso/portal/gateway",
        "contoso", "portal", "gateway", "https://dev.azure.com/contoso/")]

    //Server, on a hostname that says nothing: the collection is any path in front of the project.
    [InlineData("https://tfs.acme.io/tfs/DefaultCollection/portal/_git/gateway",
        "DefaultCollection", "portal", "gateway", "https://tfs.acme.io/tfs/DefaultCollection/")]
    public void Azure_urls_resolve_to_organisation_project_repository_and_collection(
        string url,
        string organisation,
        string project,
        string name,
        string api)
    {
        ForgeRepository forge = Assert.IsType<ForgeRepository>(ForgeUrl.TryParse(url));

        Assert.Equal(ForgeKind.AzureDevOps, forge.Kind);
        Assert.Equal(organisation, forge.Owner);
        Assert.Equal(project, forge.Project);
        Assert.Equal(name, forge.Name);
        Assert.Equal(api, forge.ApiBase.ToString());
    }

    /// <summary>
    /// An unrecognised host is refused rather than guessed at.
    ///
    /// <c>git.acme.io</c> is a GitLab or a GitHub Enterprise with equal probability, and posting a
    /// request shaped for the wrong API at whichever is listening is the one mistake that cannot be
    /// worked around. The window then names <c>flickgit.forge</c> in its message.
    /// </summary>
    [Theory]
    [InlineData("https://git.acme.io/team/tools.git")]
    [InlineData("git@git.acme.io:team/tools.git")]

    //Not a remote at all: a local clone, and a URL with no repository in it.
    [InlineData(@"C:\dev\mirror\tools.git")]
    [InlineData("https://github.com/")]
    [InlineData("")]
    public void An_unrecognised_remote_resolves_to_nothing(string url) =>
        Assert.Null(ForgeUrl.TryParse(url));

    /// <summary>
    /// <c>flickgit.forge</c> overrides host detection outright, which is what makes a self-hosted
    /// instance work at all — including one whose hostname actively misleads.
    /// </summary>
    [Fact]
    public void The_configured_kind_beats_the_hostname()
    {
        //An Azure DevOps Server whose hostname begins `github`, reached by the collection form that
        //carries no `_git` to anchor on. Detection has nothing to go on but the host, and the host
        //lies.
        const string Url = "https://github.acme.io/DefaultCollection/portal/gateway";

        ForgeRepository configured = Assert.IsType<ForgeRepository>(
            ForgeUrl.TryParse(Url, ForgeKind.AzureDevOps));

        Assert.Equal(ForgeKind.AzureDevOps, configured.Kind);
        Assert.Equal("https://github.acme.io/DefaultCollection/", configured.ApiBase.ToString());

        //Without the setting the hostname wins, and the answer is the wrong service entirely -- which
        //is what makes this an override rather than a tie-break.
        Assert.Equal(
            ForgeKind.GitHub,
            Assert.IsType<ForgeRepository>(ForgeUrl.TryParse(Url)).Kind);
    }

    /// <summary>Several spellings, because this is typed into a config file by hand.</summary>
    [Theory]
    [InlineData("github", ForgeKind.GitHub)]
    [InlineData(" azure ", ForgeKind.AzureDevOps)]
    [InlineData("tfs", ForgeKind.AzureDevOps)]
    [InlineData("bitbucket", ForgeKind.Unknown)]
    //A forge this build does not speak, named outright. Unknown rather than a near-miss is what makes
    //PullRequestService refuse by name instead of posting a request shaped for one API at another.
    [InlineData("gitlab", ForgeKind.Unknown)]
    [InlineData(null, ForgeKind.Unknown)]
    public void The_configured_kind_is_read_leniently(string? configured, ForgeKind expected) =>
        Assert.Equal(expected, ForgeUrl.ParseKind(configured));

    /// <summary>
    /// An Azure DevOps Server URL is recognised on any hostname, because <c>_git</c> is a literal in
    /// its URL grammar and in nobody else's.
    /// </summary>
    [Fact]
    public void An_underscore_git_segment_identifies_Azure_DevOps_on_any_host()
    {
        ForgeRepository forge = Assert.IsType<ForgeRepository>(
            ForgeUrl.TryParse("https://vsts.internal/Collection/portal/_git/gateway"));

        Assert.Equal(ForgeKind.AzureDevOps, forge.Kind);
    }

    /// <summary>
    /// An on-prem remote keeps its port and its scheme.
    ///
    /// <c>ForgeUrl</c> is named in Hard Requirement 4 outright, and this is the failure it is named
    /// for: <c>Uri.Host</c> excludes the port -- only <c>Authority</c> carries it -- so a collection
    /// rebuilt as <c>https://{host}/</c> was asked for on 443, over TLS, on a server that speaks
    /// plain HTTP on 8080. Every call fails, and the URL in the error names a host that looks right.
    /// </summary>
    [Fact]
    public void An_on_premises_remote_keeps_its_port_and_scheme()
    {
        ForgeRepository forge = Assert.IsType<ForgeRepository>(
            ForgeUrl.TryParse("http://tfs.acme.local:8080/tfs/DefaultCollection/portal/_git/gateway"));

        Assert.Equal(ForgeKind.AzureDevOps, forge.Kind);
        Assert.Equal("http://tfs.acme.local:8080/tfs/DefaultCollection/", forge.ApiBase.ToString());

        //The host stays portless: it is what detection matches on and what a stored token is keyed by.
        Assert.Equal("tfs.acme.local", forge.Host);
    }

    /// <summary>
    /// A path segment reaches the clients unescaped.
    ///
    /// <c>Uri.AbsolutePath</c> keeps percent-encoding, and every client escapes what it puts in a
    /// URL -- so an Azure project named <c>My Project</c> arrived as <c>My%20Project</c> and went out
    /// as <c>My%2520Project</c>, 404ing every call. The same repository cloned over SSH worked, which
    /// is what made it look like an auth problem.
    /// </summary>
    [Fact]
    public void A_percent_encoded_path_segment_is_decoded_once_and_only_once()
    {
        ForgeRepository forge = Assert.IsType<ForgeRepository>(
            ForgeUrl.TryParse("https://dev.azure.com/acme/My%20Project/_git/gateway"));

        Assert.Equal("My Project", forge.Project);
    }
}
