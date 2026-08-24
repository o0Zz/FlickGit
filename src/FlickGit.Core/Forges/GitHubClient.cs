using System.Text.Json;
using FlickGit.Logging;

namespace FlickGit.Forges;

/// <summary>
/// GitHub's pull-request API, cloud and Enterprise.
///
/// The plainest of the three: one POST, and the answer carries both the number and the URL. The two
/// things worth knowing are that the credential is a Bearer token — the OAuth token Git Credential
/// Manager already holds for <c>github.com</c> works unchanged, which is what makes the common case
/// need no setup — and that <b>there is no per-request "delete the branch on merge"</b>. That is a
/// repository setting on GitHub, so the flag is not sent and the window hides the checkbox rather
/// than offering one that would silently do nothing.
/// </summary>
public sealed class GitHubClient(HttpClient http, ILog log) : IPullRequestClient
{
    public ForgeKind Kind => ForgeKind.GitHub;

    public async Task<PullRequestOutcome> CreateAsync(
        ForgeRepository repository,
        PullRequestDraft draft,
        string token,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(
            new GitHubCreateRequest(draft.Title, draft.Description, draft.SourceBranch, draft.TargetBranch, draft.IsDraft),
            ForgeJson.Default.GitHubCreateRequest);

        ForgeResponse response = await ForgeApi.SendAsync(
            http,
            HttpMethod.Post,
            Endpoint(repository),
            json,
            request => Authorise(request, token),
            cancellationToken).ConfigureAwait(false);

        if (!response.Succeeded)
            return Refused("GitHub", repository, response);

        return Read(response.Body) is { } created
            ? PullRequestOutcome.Ok(created)
            : PullRequestOutcome.Failed("GitHub accepted the request but its answer could not be read.");
    }

    public async Task<PullRequestRef?> FindOpenAsync(
        ForgeRepository repository,
        string sourceBranch,
        string targetBranch,
        string token,
        CancellationToken cancellationToken)
    {
        //`head` is qualified with the owner, which is what makes this work on a fork as well as on
        //the repository itself -- GitHub reads an unqualified branch name as belonging to the base.
        var url = new Uri(
            Endpoint(repository),
            $"?state=open&head={Uri.EscapeDataString($"{repository.Owner}:{sourceBranch}")}"
                + $"&base={Uri.EscapeDataString(targetBranch)}");

        ForgeResponse response = await ForgeApi.SendAsync(
            http,
            HttpMethod.Get,
            url,
            null,
            request => Authorise(request, token),
            cancellationToken).ConfigureAwait(false);

        if (!response.Succeeded)
        {
            //Logged and swallowed. This call only improves a message; a failure here must never be
            //the reason a pull request cannot be opened.
            log.Debug($"Looking for an open GitHub pull request failed: {response.Status}");
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Body);

            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Select(Read).FirstOrDefault(r => r is not null)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>https://api.github.com/repos/{owner}/{repo}/pulls</c>, or the Enterprise equivalent under
    /// <c>/api/v3/</c>. The base carries the difference, so this string is the same either way.
    /// </summary>
    private static Uri Endpoint(ForgeRepository repository) =>
        new(repository.ApiBase,
            $"repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/pulls");

    /// <summary>
    /// The two headers GitHub wants beside the token.
    ///
    /// The API version is pinned rather than left to the default, because the default is "whatever is
    /// current" and a future breaking change would arrive as an inexplicable failure on a machine
    /// nobody touched.
    /// </summary>
    private static void Authorise(HttpRequestMessage request, string token)
    {
        ForgeApi.Bearer(request, token);
        request.Headers.Add("Accept", "application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    private static PullRequestOutcome Refused(string forge, ForgeRepository repository, ForgeResponse response)
    {
        string message = ForgeApi.Describe(forge, repository.Host, response, ForgeApi.MessageFrom(response.Body));

        return response.Unauthorised
            ? PullRequestOutcome.Rejected(message)
            : PullRequestOutcome.Failed(message);
    }

    private static PullRequestRef? Read(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return Read(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PullRequestRef? Read(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty("number", out JsonElement number) || number.ValueKind != JsonValueKind.Number)
            return null;

        string url = element.TryGetProperty("html_url", out JsonElement web) ? web.GetString() ?? string.Empty : string.Empty;
        string title = element.TryGetProperty("title", out JsonElement name) ? name.GetString() ?? string.Empty : string.Empty;

        return new PullRequestRef(number.GetInt32(), url, title);
    }
}
