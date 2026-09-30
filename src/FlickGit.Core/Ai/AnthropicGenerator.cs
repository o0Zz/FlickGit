using System.Text.Json;
using FlickGit.Logging;
using FlickGit.Secrets;

namespace FlickGit.Ai;

/// <summary>
/// Anthropic's Messages API, streamed.
///
/// The request and the frames; everything else about making the call is
/// <see cref="AiEndpoint.StreamAsync"/>. <b>There is no <c>thinking</c> field at all</b>, and what that
/// means depends on the model. On Haiku 4.5 omitting it disables thinking. On the default,
/// Claude Opus 5.5, thinking cannot be disabled -- <c>{type: "disabled"}</c> is a 400 -- so
/// <c>output_config.effort</c> is the only control, and it is set to <see cref="Effort"/>.
/// </summary>
public sealed class AnthropicGenerator(
    HttpClient http,
    AiOptions options,
    Func<string?> apiKey,
    ILog log) : IAiGenerator
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";

    /// <summary>The API version header Anthropic requires on every request.</summary>
    private const string Version = "2023-06-01";

    /// <summary>
    /// The shortest thinking the model allows. The task is summarisation, where deliberation buys
    /// nothing and costs time to first token.
    /// </summary>
    private const string Effort = "low";

    /// <summary>
    /// Room for the thinking on top of the task's own guard. Thinking counts toward <c>max_tokens</c>
    /// even though its text is never returned, so a commit's 150 alone would cut the message off --
    /// or leave nothing of it, which is a failed generation rather than a short one.
    /// </summary>
    private const int ThinkingAllowance = 2048;

    /// <summary>The beta header the <c>"default"</c> form of <c>fallbacks</c> requires.</summary>
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    /// <summary>
    /// Whether this model thinks, and so takes an effort and a refusal fallback. Every current Claude
    /// model does except the Haiku line, which rejects <c>output_config.effort</c> with a 400.
    /// </summary>
    internal static bool Thinks(string model) =>
        !model.StartsWith("claude-haiku-", StringComparison.Ordinal);

    public IAsyncEnumerable<string> GenerateAsync(AiPrompt prompt, CancellationToken cancellationToken)
    {
        string key = apiKey() ?? throw new AiUnavailableException("No Anthropic API key is stored.");

        string model = options.ResolvedModel;
        bool thinks = Thinks(model);

        var payload = new AnthropicRequest(
            model,
            thinks ? prompt.MaxTokens + ThinkingAllowance : prompt.MaxTokens,
            prompt.System,
            [new AnthropicMessage("user", prompt.User)],
            thinks ? new AnthropicOutputConfig(Effort) : null,
            thinks ? "default" : null);

        return AiEndpoint.StreamAsync(
            http,
            "Anthropic",
            Endpoint,
            JsonSerializer.Serialize(payload, AiJson.Default.AnthropicRequest),
            request =>
            {
                request.Headers.Add("x-api-key", key);
                request.Headers.Add("anthropic-version", Version);

                if (thinks)
                    request.Headers.Add("anthropic-beta", FallbackBeta);
            },
            AiFraming.ServerSentEvents,
            options.Silence,
            Read,
            cancellationToken);
    }

    /// <summary>
    /// The one text-bearing frame type, and the one error frame. Everything else — <c>ping</c>,
    /// <c>message_start</c>, <c>content_block_start</c>, <c>message_delta</c>, <c>message_stop</c>,
    /// and the <c>thinking_delta</c> and <c>signature_delta</c> of a thinking block — is ignored, which
    /// is what makes a newly added frame type a non-event. A refusal that no fallback rescued ends with
    /// no text at all, which <c>AiTextService</c> already reports as a failed generation.
    /// </summary>
    private string? Read(string frame)
    {
        try
        {
            AnthropicEvent? parsed = JsonSerializer.Deserialize(frame, AiJson.Default.AnthropicEvent);

            if (parsed?.Error is { } error)
                throw new AiUnavailableException(SecretDetector.Redact(error.Message ?? error.Type ?? "Anthropic returned an error."));

            return parsed is { Type: "content_block_delta", Delta.Type: "text_delta" } ? parsed.Delta.Text : null;
        }
        catch (JsonException ex)
        {
            //A frame this build does not understand is not a reason to fail a commit message that is
            //otherwise arriving fine.
            log.Debug($"Unparseable Anthropic frame ignored: {ex.Message}");
            return null;
        }
    }

    public Task<AiProbe> ProbeAsync(CancellationToken cancellationToken) =>
        AiEndpoint.ProbeAsync(http, Endpoint, cancellationToken);
}
