using System.Text.Json.Serialization;

namespace FlickGit.Ai;

// The request and response shapes for both providers.
//
// Source-generated, and not optional: FlickGit.Core sets IsAotCompatible, so a reflection-based
// JsonSerializer call is an IL2026/IL3050 warning and the repository treats warnings as errors.
//
// Every property carries an explicit [JsonPropertyName]. The naming policy would get most of them
// right, but "max_tokens" and "max_output_tokens" are not camelCase of anything, and a silently
// misnamed field is a 400 from the provider that reads like a bad key.

/// <param name="Model">The model id.</param>
/// <param name="MaxTokens">The runaway guard, not the length control.</param>
/// <param name="System">The system prompt.</param>
/// <param name="Messages">One user message: the payload.</param>
internal sealed record AnthropicRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("max_tokens")] int MaxTokens,
    [property: JsonPropertyName("system")] string System,
    [property: JsonPropertyName("messages")] AnthropicMessage[] Messages)
{
    [JsonPropertyName("stream")]
    public bool Stream => true;
}

internal sealed record AnthropicMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

/// <summary>
/// One SSE frame from Anthropic. Only the fields that matter are declared; the rest of each frame
/// is ignored, which is what makes an added field a non-event.
/// </summary>
internal sealed class AnthropicEvent
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("delta")]
    public AnthropicDelta? Delta { get; set; }

    [JsonPropertyName("error")]
    public AnthropicError? Error { get; set; }
}

internal sealed class AnthropicDelta
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

internal sealed class AnthropicError
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <param name="Model">The model id.</param>
/// <param name="Instructions">The Responses API's name for a system prompt.</param>
/// <param name="Input">The payload.</param>
/// <param name="MaxOutputTokens">The runaway guard.</param>
/// <param name="Reasoning">Effort "none" is the latency baseline. CLAUDE.md.</param>
internal sealed record OpenAiRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("instructions")] string Instructions,
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("max_output_tokens")] int MaxOutputTokens,
    [property: JsonPropertyName("reasoning")] OpenAiReasoning Reasoning)
{
    [JsonPropertyName("stream")]
    public bool Stream => true;

    /// <summary>
    /// Do not retain the diff.
    ///
    /// Not in CLAUDE.md, and it should be: the whole privacy section is about source code leaving
    /// the machine, and asking the provider not to keep it costs one field.
    /// </summary>
    [JsonPropertyName("store")]
    public bool Store => false;
}

internal sealed record OpenAiReasoning([property: JsonPropertyName("effort")] string Effort);

internal sealed class OpenAiEvent
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("delta")]
    public string? Delta { get; set; }

    [JsonPropertyName("error")]
    public OpenAiError? Error { get; set; }
}

internal sealed class OpenAiError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AnthropicRequest))]
[JsonSerializable(typeof(AnthropicEvent))]
[JsonSerializable(typeof(OpenAiRequest))]
[JsonSerializable(typeof(OpenAiEvent))]
internal sealed partial class AiJson : JsonSerializerContext;
