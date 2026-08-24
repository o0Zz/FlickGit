using System.Net.Http.Headers;
using System.Text.Json;
using FlickGit.Logging;
using FlickGit.Secrets;

namespace FlickGit.Ai;

/// <summary>
/// OpenAI's Responses API, streamed.
///
/// The endpoint follows from CLAUDE.md's own parameter names: <c>reasoning: { effort }</c> and
/// <c>max_output_tokens</c> are Responses-API spellings, not Chat Completions ones.
///
/// Still <b>not</b> a base class shared with the Anthropic generator — but no longer a second copy of
/// the request either. What the two have in common is one function they both call,
/// <see cref="AiEndpoint.StreamAsync"/>; what differs is the URL, the header, the request shape and
/// which frame carries text, and those are the arguments. Half of each file used to be the same lines,
/// which meant the hard timeout and the streaming flag each existed twice.
/// </summary>
public sealed class OpenAiGenerator(
    HttpClient http,
    AiOptions options,
    Func<string?> apiKey,
    ILog log) : IAiGenerator
{
    private const string Endpoint = "https://api.openai.com/v1/responses";

    public IAsyncEnumerable<string> GenerateAsync(AiPrompt prompt, CancellationToken cancellationToken)
    {
        string key = apiKey() ?? throw new AiUnavailableException("No OpenAI API key is stored.");

        var payload = new OpenAiRequest(
            options.ResolvedModel,
            prompt.System,
            prompt.User,
            prompt.MaxTokens,
            new OpenAiReasoning(options.ReasoningEffort.Length > 0 ? options.ReasoningEffort : "none"));

        return AiEndpoint.StreamAsync(
            http,
            "OpenAI",
            Endpoint,
            JsonSerializer.Serialize(payload, AiJson.Default.OpenAiRequest),
            request => request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key),
            Read,
            cancellationToken);
    }

    /// <summary>
    /// Branches on the JSON <c>type</c>, not on the SSE <c>event:</c> line: the Responses API puts
    /// the event name in the body, and the two are not always the same.
    /// </summary>
    private string? Read(string frame)
    {
        try
        {
            OpenAiEvent? parsed = JsonSerializer.Deserialize(frame, AiJson.Default.OpenAiEvent);

            if (parsed?.Type is "response.failed" or "error")
                throw new AiUnavailableException(SecretDetector.Redact(parsed.Error?.Message ?? "OpenAI returned an error."));

            return parsed?.Type == "response.output_text.delta" ? parsed.Delta : null;
        }
        catch (JsonException ex)
        {
            log.Debug($"Unparseable OpenAI frame ignored: {ex.Message}");
            return null;
        }
    }

    public Task<AiProbe> ProbeAsync(CancellationToken cancellationToken) =>
        AiEndpoint.ProbeAsync(http, Endpoint, cancellationToken);
}
