using System.Text.Json;
using FlickGit.Logging;

namespace FlickGit.Forges;

/// <summary>
/// GitLab's merge-request API, gitlab.com and self-managed alike.
///
/// Two things about it are not obvious from the other two clients:
///
/// <list type="bullet">
/// <item><description><b>Draft is a title prefix, not a field.</b> There is no <c>draft</c> property
/// on create — GitLab decides from the title, and <c>Draft:</c> is the spelling its own UI writes.
/// So the checkbox becomes four characters in front of what the user typed, and the prefix is not
/// added twice if they typed it themselves.</description></item>
/// <item><description><b>The number the user sees is <c>iid</c>, not <c>id</c>.</b> The <c>id</c> is
/// globally unique across the instance and appears nowhere in the interface; <c>iid</c> is the
/// <c>!42</c> people quote to each other. Reading the wrong one would produce a plausible number
/// that matches nothing.</description></item>
/// </list>
///
/// The credential is a Bearer token, which covers both what Git Credential Manager stores (an OAuth
/// token) and what a user pastes from the UI (a personal access token) — GitLab accepts either in
/// that header, so there is no need to know which one is in hand.
/// </summary>
public sealed class GitLabClient(HttpClient http, ILog log) : IPullRequestClient
{
    private const string DraftPrefix = "Draft: ";

    public ForgeKind Kind => ForgeKind.GitLab;

    public async Task<PullRequestOutcome> CreateAsync(
        ForgeRepository repository,
        PullRequestDraft draft,
        string token,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(
            new GitLabCreateRequest(
                draft.SourceBranch,
                draft.TargetBranch,
                Title(draft),
                draft.Description,
                draft.DeleteSourceBranch),
            ForgeJson.Default.GitLabCreateRequest);

        ForgeResponse response = await ForgeApi.SendAsync(
            http,
            HttpMethod.Post,
            Endpoint(repository),
            json,
            request => ForgeApi.Bearer(request, token),
            cancellationToken).ConfigureAwait(false);

        if (!response.Succeeded)
        {
            string message = ForgeApi.Describe("GitLab", repository.Host, response, ForgeApi.MessageFrom(response.Body));

            return response.Unauthorised
                ? PullRequestOutcome.Rejected(message)
                : PullRequestOutcome.Failed(message);
        }

        return Read(response.Body) is { } created
            ? PullRequestOutcome.Ok(created)
            : PullRequestOutcome.Failed("GitLab accepted the request but its answer could not be read.");
    }

    public async Task<PullRequestRef?> FindOpenAsync(
        ForgeRepository repository,
        string sourceBranch,
        string targetBranch,
        string token,
        CancellationToken cancellationToken)
    {
        var url = new Uri(
            Endpoint(repository),
            $"?state=opened&source_branch={Uri.EscapeDataString(sourceBranch)}"
                + $"&target_branch={Uri.EscapeDataString(targetBranch)}");

        ForgeResponse response = await ForgeApi.SendAsync(
            http,
            HttpMethod.Get,
            url,
            null,
            request => ForgeApi.Bearer(request, token),
            cancellationToken).ConfigureAwait(false);

        if (!response.Succeeded)
        {
            log.Debug($"Looking for an open GitLab merge request failed: {response.Status}");
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
    /// <c>Draft:</c> in front of the title, which is how GitLab marks one.
    ///
    /// Not applied when the user has already typed it, in either the current spelling or the older
    /// <c>WIP:</c> one GitLab still recognises — a title reading "Draft: Draft: fix the pool" is the
    /// kind of thing nobody notices until it is in a review.
    /// </summary>
    private static string Title(PullRequestDraft draft)
    {
        string title = draft.Title.Trim();

        if (!draft.IsDraft)
            return title;

        bool already = title.StartsWith("draft:", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith("wip:", StringComparison.OrdinalIgnoreCase);

        return already ? title : DraftPrefix + title;
    }

    /// <summary>
    /// <c>{api}/projects/{namespace%2Fproject}/merge_requests</c>.
    ///
    /// The project path is escaped whole — one path parameter with encoded slashes inside it, which
    /// is what lets a nested subgroup be addressed at all.
    /// </summary>
    private static Uri Endpoint(ForgeRepository repository) =>
        new(repository.ApiBase, $"projects/{repository.EncodedPath}/merge_requests");

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

        if (!element.TryGetProperty("iid", out JsonElement iid) || iid.ValueKind != JsonValueKind.Number)
            return null;

        string url = element.TryGetProperty("web_url", out JsonElement web) ? web.GetString() ?? string.Empty : string.Empty;
        string title = element.TryGetProperty("title", out JsonElement name) ? name.GetString() ?? string.Empty : string.Empty;

        return new PullRequestRef(iid.GetInt32(), url, title);
    }
}
