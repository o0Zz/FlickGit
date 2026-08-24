using System.Text.Json;
using FlickGit.Logging;
using FlickGit.Secrets;

namespace FlickGit.Ai;

/// <summary>
/// Anthropic's Messages API, streamed.
///
/// The request and the frames; everything else about making the call is
/// <see cref="AiEndpoint.StreamAsync"/>. One detail here is load-bearing and not obvious:
/// <b>there is no <c>thinking</c> field at all</b>. On Haiku 4.5 extended thinking is off unless
/// explicitly enabled, so omitting it <i>is</i> disabling it — CLAUDE.md: "Extended thinking exists on
/// the Haiku line — do not enable it here."
/// </summary>
public sealed class AnthropicCommitMessageGenerator(
    HttpClient http,
    AiOptions options,
    Func<string?> apiKey,
    ILog log) : ICommitMessageGenerator
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";

    /// <summary>The API version header Anthropic requires on every request.</summary>
    private const string Version = "2023-06-01";

    public IAsyncEnumerable<string> GenerateAsync(CommitContext context, CancellationToken cancellationToken)
    {
        string key = apiKey() ?? throw new AiUnavailableException("No Anthropic API key is stored.");

        var payload = new AnthropicRequest(
            options.ResolvedModel,
            AiOptions.MaxOutputTokens,
            CommitPrompt.For(options.ConventionalCommits),
            [new AnthropicMessage("user", context.ToPromptText())]);

        return AiEndpoint.StreamAsync(
            http,
            "Anthropic",
            Endpoint,
            JsonSerializer.Serialize(payload, AiJson.Default.AnthropicRequest),
            request =>
            {
                request.Headers.Add("x-api-key", key);
                request.Headers.Add("anthropic-version", Version);
            },
            Read,
            cancellationToken);
    }

    /// <summary>
    /// The one text-bearing frame type, and the one error frame. Everything else — <c>ping</c>,
    /// <c>message_start</c>, <c>content_block_start</c>, <c>message_delta</c>, <c>message_stop</c> —
    /// is ignored, which is what makes a newly added frame type a non-event.
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
