using System.Text.Json.Serialization;

namespace FlickGit.Forges;

// The three request bodies.
//
// Requests are source-generated because FlickGit.Core sets IsAotCompatible, so a reflection-based
// JsonSerializer.Serialize is an IL2026/IL3050 warning and this repository treats warnings as
// errors. *Responses* are read with JsonDocument instead, deliberately: the three services disagree
// about the shape of an error — GitLab's `message` is a string, an array of strings or an object of
// arrays depending on what went wrong — and a DTO per variant would be a dozen types to express
// "find me a sentence to show the user".
//
// Every property carries an explicit [JsonPropertyName]. The naming policy would get most of them
// right, and "source_branch" and "sourceRefName" are not camelCase of anything.

/// <param name="Head">The branch being proposed. Short, unless it is on a fork.</param>
/// <param name="Base">The branch it is proposed into.</param>
/// <param name="Draft">GitHub is the only one of the three with a boolean for this on create.</param>
internal sealed record GitHubCreateRequest(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("head")] string Head,
    [property: JsonPropertyName("base")] string Base,
    [property: JsonPropertyName("draft")] bool Draft);

/// <param name="RemoveSourceBranch">GitLab's spelling of "delete the branch when this merges".</param>
internal sealed record GitLabCreateRequest(
    [property: JsonPropertyName("source_branch")] string SourceBranch,
    [property: JsonPropertyName("target_branch")] string TargetBranch,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("remove_source_branch")] bool RemoveSourceBranch);

/// <param name="SourceRefName">Fully qualified: <c>refs/heads/feature/x</c>, never the short name.</param>
/// <param name="CompletionOptions">
/// Null unless the source branch is to be deleted. Azure DevOps accepts completion options on create
/// and applies them when the request completes, so this needs no second call.
/// </param>
internal sealed record AzureCreateRequest(
    [property: JsonPropertyName("sourceRefName")] string SourceRefName,
    [property: JsonPropertyName("targetRefName")] string TargetRefName,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("isDraft")] bool IsDraft,
    [property: JsonPropertyName("completionOptions")] AzureCompletionOptions? CompletionOptions);

internal sealed record AzureCompletionOptions(
    [property: JsonPropertyName("deleteSourceBranch")] bool DeleteSourceBranch);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GitHubCreateRequest))]
[JsonSerializable(typeof(GitLabCreateRequest))]
[JsonSerializable(typeof(AzureCreateRequest))]
internal sealed partial class ForgeJson : JsonSerializerContext;
