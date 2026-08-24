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

/// <param name="Model">The model id. Copilot's own list, not OpenAI's.</param>
/// <param name="Messages">The system prompt and the payload, Chat Completions style.</param>
/// <param name="MaxTokens">The runaway guard.</param>
internal sealed record CopilotRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] CopilotMessage[] Messages,
    [property: JsonPropertyName("max_tokens")] int MaxTokens)
{
    [JsonPropertyName("stream")]
    public bool Stream => true;
}

/// <summary>
/// Chat Completions puts the system prompt in the message list rather than in a field of its own,
/// which is the one structural difference from the other two requests.
/// </summary>
internal sealed record CopilotMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed class CopilotEvent
{
    [JsonPropertyName("choices")]
    public CopilotChoice[]? Choices { get; set; }

    [JsonPropertyName("error")]
    public CopilotError? Error { get; set; }
}

internal sealed class CopilotChoice
{
    [JsonPropertyName("delta")]
    public CopilotDelta? Delta { get; set; }
}

internal sealed class CopilotDelta
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

internal sealed class CopilotError
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <param name="Messages">
/// The system prompt and the payload. <b>Empty is meaningful</b>: Ollama reads a chat request with
/// no messages as "load this model and stop", which is what the warm-up sends.
/// </param>
/// <param name="Options">
/// Ollama's per-request generation settings. Null on the warm-up, where there is nothing to
/// generate.
/// </param>
internal sealed record OllamaRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] OllamaMessage[] Messages,
    [property: JsonPropertyName("options")] OllamaGenerationOptions? Options)
{
    /// <summary>
    /// An init property rather than the computed <c>=&gt; true</c> the other three requests use,
    /// because the warm-up wants a single non-streamed answer.
    /// </summary>
    [JsonPropertyName("stream")]
    public bool Stream { get; init; } = true;

    /// <summary>
    /// How long Ollama keeps the model in memory after this request. Sent only by the warm-up: a
    /// preload that is evicted before the user's first commit has bought nothing. Left off every
    /// real request, so the user's own <c>OLLAMA_KEEP_ALIVE</c> stays in charge from then on.
    /// </summary>
    [JsonPropertyName("keep_alive")]
    public string? KeepAlive { get; init; }
}

/// <summary>
/// <c>num_predict</c> is Ollama's spelling of the runaway guard the other three call
/// <c>max_tokens</c> or <c>max_output_tokens</c>. Same job, third name.
/// </summary>
internal sealed record OllamaGenerationOptions(
    [property: JsonPropertyName("num_predict")] int NumPredict);

internal sealed record OllamaMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

/// <summary>
/// One line of an Ollama stream.
///
/// <c>error</c> is a bare string here, where the other three wrap it in an object — so it cannot
/// share their shape, and reading it as one would silently ignore every Ollama error.
/// </summary>
internal sealed class OllamaEvent
{
    [JsonPropertyName("message")]
    public OllamaMessage? Message { get; set; }

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// The answer from <c>/copilot_internal/v2/token</c>.
///
/// <c>expires_at</c> is epoch seconds. The token string itself also carries an <c>exp=</c> field, and
/// this reads the sibling rather than parsing the token: a credential should be sent, not picked apart.
/// </summary>
internal sealed class CopilotTokenResponse
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; set; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AnthropicRequest))]
[JsonSerializable(typeof(AnthropicEvent))]
[JsonSerializable(typeof(OpenAiRequest))]
[JsonSerializable(typeof(OpenAiEvent))]
[JsonSerializable(typeof(CopilotRequest))]
[JsonSerializable(typeof(CopilotEvent))]
[JsonSerializable(typeof(CopilotTokenResponse))]
[JsonSerializable(typeof(OllamaRequest))]
[JsonSerializable(typeof(OllamaEvent))]
internal sealed partial class AiJson : JsonSerializerContext;
