using System.Text.Json;
using FlickGit.Logging;

namespace FlickGit.Forges;

/// <summary>
/// Azure DevOps' pull-request API, Services and Server.
///
/// The odd one of the three, in four ways that each cost a line:
///
/// <list type="bullet">
/// <item><description><b>Basic auth, not Bearer.</b> The token goes in as the password with an empty
/// user name. A personal access token sent as a Bearer token is answered with an HTML sign-in page
/// and a 203, which is why <see cref="ForgeApi.Basic"/> exists.</description></item>
/// <item><description><b>Branches are full ref names.</b> <c>refs/heads/feature/x</c>, never the
/// short name — a short name is accepted and then matches nothing, producing a request between two
/// branches that do not exist.</description></item>
/// <item><description><b>Every URL carries an <c>api-version</c>.</b> Without it the service answers
/// 400. <see cref="ApiVersion"/> is pinned low deliberately — see there.</description></item>
/// <item><description><b>The answer has no web URL in it.</b> <c>_links.web.href</c> is absent on
/// create, so the address is built from the collection, the project and the id — which is the one
/// place in this feature that assembles a URL rather than being told one.</description></item>
/// </list>
/// </summary>
public sealed class AzureDevOpsClient(HttpClient http, ILog log) : IPullRequestClient
{
    /// <summary>
    /// Pinned to 6.0 rather than the current 7.1.
    ///
    /// Everything this client sends has existed since 5.1 — <c>isDraft</c> included — so the newer
    /// version buys nothing, and Azure DevOps <i>Server</i> installs that are a few years old answer
    /// 400 to a version they predate. The lowest version that carries the fields is the one that
    /// works in the most places, and this is a per-user tool that has to work against whatever a
    /// company happens to run.
    /// </summary>
    private const string ApiVersion = "6.0";

    public ForgeKind Kind => ForgeKind.AzureDevOps;

    public async Task<PullRequestOutcome> CreateAsync(
        ForgeRepository repository,
        PullRequestDraft draft,
        string token,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(
            new AzureCreateRequest(
                Ref(draft.SourceBranch),
                Ref(draft.TargetBranch),
                draft.Title,
                draft.Description,
                draft.IsDraft,

                //Omitted entirely rather than sent as false: completion options are applied when the
                //request completes, and an explicit `deleteSourceBranch: false` overrides a policy
                //the project may have set for itself.
                draft.DeleteSourceBranch ? new AzureCompletionOptions(true) : null),
            ForgeJson.Default.AzureCreateRequest);

        ForgeResponse response = await ForgeApi.SendAsync(
            http,
            HttpMethod.Post,
            Endpoint(repository, null),
            json,
            request => ForgeApi.Basic(request, token),
            cancellationToken).ConfigureAwait(false);

        if (!response.Succeeded)
        {
            string message = ForgeApi.Describe(
                "Azure DevOps",
                repository.Host,
                response,
                ForgeApi.MessageFrom(response.Body));

            return response.Unauthorised
                ? PullRequestOutcome.Rejected(message)
                : PullRequestOutcome.Failed(message);
        }

        return Read(repository, response.Body) is { } created
            ? PullRequestOutcome.Ok(created)
            : PullRequestOutcome.Failed("Azure DevOps accepted the request but its answer could not be read.");
    }

    public async Task<PullRequestRef?> FindOpenAsync(
        ForgeRepository repository,
        string sourceBranch,
        string targetBranch,
        string token,
        CancellationToken cancellationToken)
    {
        string query =
            $"searchCriteria.sourceRefName={Uri.EscapeDataString(Ref(sourceBranch))}"
            + $"&searchCriteria.targetRefName={Uri.EscapeDataString(Ref(targetBranch))}"
            + "&searchCriteria.status=active";

        ForgeResponse response = await ForgeApi.SendAsync(
            http,
            HttpMethod.Get,
            Endpoint(repository, query),
            null,
            request => ForgeApi.Basic(request, token),
            cancellationToken).ConfigureAwait(false);

        if (!response.Succeeded)
        {
            log.Debug($"Looking for an open Azure DevOps pull request failed: {response.Status}");
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(response.Body);

            //A list answer is wrapped: { "count": 1, "value": [ … ] }, unlike the other two, which
            //return a bare array.
            if (!document.RootElement.TryGetProperty("value", out JsonElement values)
                || values.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            return values.EnumerateArray().Select(e => Read(repository, e)).FirstOrDefault(r => r is not null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>{collection}/{project}/_apis/git/repositories/{repo}/pullrequests?api-version=…</c>.
    ///
    /// The collection is <see cref="ForgeRepository.ApiBase"/>, which is what makes Services and
    /// Server the same code: one is <c>https://dev.azure.com/org/</c> and the other is
    /// <c>https://tfs.acme.io/tfs/DefaultCollection/</c>, and neither is special-cased here.
    /// </summary>
    private static Uri Endpoint(ForgeRepository repository, string? query)
    {
        string path =
            $"{Uri.EscapeDataString(repository.Project)}/_apis/git/repositories/"
            + $"{Uri.EscapeDataString(repository.Name)}/pullrequests?api-version={ApiVersion}";

        return new Uri(repository.ApiBase, query is null ? path : path + "&" + query);
    }

    /// <summary>The short branch name as a full ref. Azure DevOps accepts nothing else.</summary>
    private static string Ref(string branch) =>
        branch.StartsWith("refs/", StringComparison.Ordinal) ? branch : "refs/heads/" + branch;

    private static PullRequestRef? Read(ForgeRepository repository, string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return Read(repository, document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PullRequestRef? Read(ForgeRepository repository, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty("pullRequestId", out JsonElement id) || id.ValueKind != JsonValueKind.Number)
            return null;

        string title = element.TryGetProperty("title", out JsonElement name) ? name.GetString() ?? string.Empty : string.Empty;

        return new PullRequestRef(id.GetInt32(), WebUrl(repository, id.GetInt32()), title);
    }

    /// <summary>
    /// Where a human reads the request.
    ///
    /// Built rather than read back, because the create response does not carry it. The shape is the
    /// same one the remote URL was parsed out of, which is why it can be reassembled reliably:
    /// collection, project, <c>_git</c>, repository.
    /// </summary>
    private static string WebUrl(ForgeRepository repository, int id) =>
        $"{repository.ApiBase}{Uri.EscapeDataString(repository.Project)}/_git/"
        + $"{Uri.EscapeDataString(repository.Name)}/pullrequest/{id}";
}
