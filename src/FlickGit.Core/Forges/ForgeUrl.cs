using System.Text.RegularExpressions;

namespace FlickGit.Forges;

/// <summary>
/// Turns a remote URL into the repository an API can be asked about.
///
/// <b>The one parser in this feature where a wrong answer is expensive.</b> Every other mistake
/// shows up as an error message; this one would send a pull request to a real repository that is
/// not the user's. Pure -- no Git, no network -- so it can be tested exhaustively against the URL
/// shapes three services and their self-hosted variants actually emit.
///
/// Deliberately <i>not</i> <see cref="Clone.CloneUrl"/>. That one guards a clipboard prefill and
/// may refuse a valid URL; this one answers "which project, on which API", where refusing is the
/// safe failure and guessing is not.
/// </summary>
public static partial class ForgeUrl
{
    /// <summary>
    /// scp-style: <c>git@github.com:owner/repo.git</c>. The path must not look like a port, or
    /// <c>host:8080</c> would parse as a repository called 8080.
    /// </summary>
    [GeneratedRegex(
        @"^(?:(?<user>[A-Za-z0-9._-]+)@)?(?<host>[A-Za-z0-9.-]+):(?<path>(?!\d+$)[^\s]+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ScpStyle();

    /// <summary>
    /// The value of <c>flickgit.forge</c>, or <see cref="ForgeKind.Unknown"/>. Several spellings per
    /// service, because this is typed into a config file by hand; a typo lands on Unknown and gets
    /// the same actionable message as an unrecognised host.
    /// </summary>
    public static ForgeKind ParseKind(string? name) =>
        name?.Trim().ToLowerInvariant() switch
        {
            "github" or "gh" or "github-enterprise" => ForgeKind.GitHub,

            "azure" or "azuredevops" or "azure-devops" or "ado" or "vsts" or "tfs" => ForgeKind.AzureDevOps,
            _ => ForgeKind.Unknown,
        };

    /// <param name="hint">
    /// <c>flickgit.forge</c> from the repository's own config. It overrides host detection outright
    /// rather than merely breaking ties: a company that runs GitLab at <c>github.acme.io</c> has said
    /// so, and second-guessing them would be the one failure that cannot be worked around.
    /// </param>
    public static ForgeRepository? TryParse(string? remoteUrl, ForgeKind hint = ForgeKind.Unknown)
    {
        if (Split(remoteUrl) is not { } remote)
            return null;

        string[] segments = remote.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0)
            return null;

        ForgeKind kind = hint != ForgeKind.Unknown ? hint : Detect(remote.Host, segments);

        return kind switch
        {
            ForgeKind.AzureDevOps => Azure(remote, segments),
            ForgeKind.GitHub => GitHub(remote, segments),

            _ => null,
        };
    }

    /// <summary>
    /// The host and the path, out of any of the four spellings a Git remote comes in.
    ///
    /// The userinfo is dropped rather than read: Azure DevOps puts the organization there and it is
    /// also in the path, so reading it would be a second source for one value -- and for GitHub over
    /// SSH it is the literal word "git".
    /// </summary>
    private static RemoteParts? Split(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return null;

        string candidate = remoteUrl.Trim();

        if (candidate.Length > 2048 || candidate.AsSpan().ContainsAny(" \t\r\n"))
            return null;

        if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            //A Windows path parses as a file:// URI, and a local clone is not a forge. Refused here rather
            //than reaching the detector, which would find no host and say nothing useful.
            if (uri.Scheme is not ("https" or "http" or "ssh" or "git"))
                return null;

            string host = uri.Host.ToLowerInvariant();
            bool web = uri.Scheme is "https" or "http";

            //The port belongs to the API base and only Uri.Authority carries it -- Uri.Host does not.
            //Dropped, an on-prem `http://tfs.acme.local:8080/...` was asked for on 443 instead, which
            //is not a server that exists. Taken only from a web URL: an ssh remote's port is the ssh
            //port, and putting 7999 on an https API base would be the same bug the other way round.
            string authority = web && !uri.IsDefaultPort ? $"{host}:{uri.Port}" : host;

            //The scheme too, for the same on-prem case: a server reachable only over plain http is not
            //made reachable by asking it over https, and the remote is where the user already said
            //which one it speaks. ssh and git have no API, so those become https.
            string scheme = web ? uri.Scheme : "https";

            //Unescaped, because every client escapes what it puts in a URL. Left as AbsolutePath gives
            //it, an Azure project named `My Project` arrives here as `My%20Project` and is escaped a
            //second time into `My%2520Project`, which 404s every call -- while the same repository over
            //SSH works, because that branch never escaped it in the first place.
            return new RemoteParts(scheme, host, authority, Uri.UnescapeDataString(uri.AbsolutePath));
        }

        Match scp = ScpStyle().Match(candidate);

        if (!scp.Success)
            return null;

        string scpHost = scp.Groups["host"].Value.ToLowerInvariant();

        return new RemoteParts("https", scpHost, scpHost, scp.Groups["path"].Value);
    }

    /// <summary>
    /// A remote URL, split into the four things building an API base needs.
    ///
    /// <paramref name="Host"/> and <paramref name="Authority"/> are separate on purpose: the host is
    /// what detection matches and what a stored token is keyed by, and it must not carry a port; the
    /// authority is what goes into a URL, and it must.
    /// </summary>
    private readonly record struct RemoteParts(string Scheme, string Host, string Authority, string Path);

    /// <summary>
    /// Which service a host and path belong to, when the repository has not said.
    ///
    /// The cloud hosts are exact. Everything below is a heuristic, and each is here because it is
    /// unambiguous rather than likely: <c>_git</c> is Azure DevOps' own URL grammar and nothing else
    /// produces it. Anything else is <see cref="ForgeKind.Unknown"/>, and the caller then names
    /// <c>flickgit.forge</c> rather than posting to whichever API it guessed.
    /// </summary>
    private static ForgeKind Detect(string host, string[] segments)
    {
        if (host == "github.com" || host.EndsWith(".github.com", StringComparison.Ordinal))
            return ForgeKind.GitHub;


        if (host == "dev.azure.com"
            || host.EndsWith(".dev.azure.com", StringComparison.Ordinal)
            || host.EndsWith(".visualstudio.com", StringComparison.Ordinal))
        {
            return ForgeKind.AzureDevOps;
        }

        //Azure DevOps Server, on any hostname at all. `_git` is a literal in its URL grammar and in
        //nobody else's, so this is a fact rather than a guess.
        if (segments.Contains("_git", StringComparer.Ordinal))
            return ForgeKind.AzureDevOps;

        string firstLabel = host.Split('.')[0];

        if (firstLabel.StartsWith("github", StringComparison.Ordinal))
            return ForgeKind.GitHub;


        return ForgeKind.Unknown;
    }

    private static ForgeRepository? GitHub(RemoteParts remote, string[] segments)
    {
        string host = remote.Host;

        if (segments.Length < 2)
            return null;

        //The last two segments, not the first two: an Enterprise install behind a path prefix still ends
        //in owner/repo, and nothing else in a GitHub URL follows the repository.
        string owner = segments[^2];
        string name = Strip(segments[^1]);

        if (owner.Length == 0 || name.Length == 0)
            return null;

        Uri api = host is "github.com" or "www.github.com"
            ? new Uri("https://api.github.com/")
            : new Uri($"{remote.Scheme}://{remote.Authority}/api/v3/");

        return new ForgeRepository(ForgeKind.GitHub, host, api, owner, string.Empty, name);
    }


    /// <summary>
    /// The three-level one, and the only one whose API base is not a fixed string.
    ///
    /// Azure DevOps' REST API hangs off the <i>collection</i>: <c>https://dev.azure.com/{org}/</c> in
    /// the cloud and any path at all on Server. So the collection is "everything in front of the
    /// project" rather than a known prefix, which is what makes Server work with no second code path.
    ///
    /// Four shapes reach here: <c>dev.azure.com/org/project/_git/repo</c>;
    /// <c>org.visualstudio.com/project/_git/repo</c>, where the organization is the host's first
    /// label; <c>ssh.dev.azure.com:v3/org/project/repo</c>, with no <c>_git</c> and a leading
    /// <c>v3</c> that is the SSH protocol version; and Server, with a collection of any depth.
    /// </summary>
    private static ForgeRepository? Azure(RemoteParts remote, string[] segments)
    {
        string host = remote.Host;
        bool visualStudio = host.EndsWith(".visualstudio.com", StringComparison.Ordinal);

        //ssh.dev.azure.com and vs-ssh.visualstudio.com are the SSH endpoints of the same service. The
        //API lives on the web host, so the name is normalised before anything is built from it. The
        //authority gets the same treatment and keeps its port, which an on-prem collection needs.
        string webHost = StripSshPrefix(host);
        string webAuthority = StripSshPrefix(remote.Authority);

        int gitIndex = Array.IndexOf(segments, "_git");

        string[] before;
        string project;
        string name;

        if (gitIndex >= 1 && gitIndex + 1 < segments.Length)
        {
            //`_git` is immediately preceded by the project and followed by the repository, in every Azure
            //DevOps clone URL. Anchoring on the marker rather than counting forward is what lets the
            //collection in front of it be any depth at all.
            project = segments[gitIndex - 1];
            name = Strip(segments[gitIndex + 1]);
            before = segments[..(gitIndex - 1)];
        }
        else
        {
            //The SSH form: v3/org/project/repo, with no marker to anchor on.
            string[] parts = segments[0] == "v3" ? segments[1..] : segments;

            if (parts.Length < 3)
                return null;

            project = parts[^2];
            name = Strip(parts[^1]);
            before = parts[..^2];
        }

        if (project.Length == 0 || name.Length == 0)
            return null;

        //On visualstudio.com the organization is the host label and the path holds only the project, so
        //`before` is legitimately empty and the collection is the host root.
        string organisation = before.Length > 0 ? before[^1]
            : visualStudio ? webHost.Split('.')[0]
            : string.Empty;

        if (organisation.Length == 0)
            return null;

        string collection = before.Length > 0 ? string.Join('/', before) + "/" : string.Empty;

        return new ForgeRepository(
            ForgeKind.AzureDevOps,
            webHost,
            new Uri($"{remote.Scheme}://{webAuthority}/{collection}"),
            organisation,
            project,
            name);
    }

    private static string StripSshPrefix(string value) =>
        value.StartsWith("ssh.", StringComparison.Ordinal) ? value[4..]
        : value.StartsWith("vs-ssh.", StringComparison.Ordinal) ? value[7..]
        : value;

    private static string Strip(string segment) =>
        segment.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segment[..^4] : segment;
}
