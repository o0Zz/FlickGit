namespace FlickGit.Ai;

/// <summary>Which service writes the commit message.</summary>
public enum AiProvider
{
    /// <summary>No message is generated. The box is an ordinary editable field.</summary>
    Disabled,

    /// <summary>Anthropic's Messages API. CLAUDE.md's default.</summary>
    Anthropic,

    /// <summary>OpenAI's Responses API.</summary>
    OpenAi,

    /// <summary>
    /// GitHub Copilot's chat endpoint, on the user's existing Copilot subscription.
    ///
    /// Third rather than second because it is the only one that cannot be reached with the stored
    /// credential alone -- see <see cref="CopilotToken"/>.
    /// </summary>
    Copilot,
}

/// <summary>
/// Everything a generator needs that is not the diff itself.
///
/// A value object rather than a settings reference, so <c>FlickGit.Core</c> keeps knowing nothing
/// about where settings live — and so a test can set a 200-byte cap without a settings file.
/// </summary>
/// <param name="Provider">Which service to ask.</param>
/// <param name="Model">The model id. Empty means the provider's default.</param>
/// <param name="ReasoningEffort">OpenAI only. <c>none</c> is the latency baseline.</param>
/// <param name="MaxDiffBytes">
/// The cap CLAUDE.md exposes as "Max diff size", defaulted low. Above it the payload becomes a
/// summary plus the first lines of each file's hunks.
/// </param>
/// <param name="ConventionalCommits">
/// True to require Conventional Commits rather than leaving it to the model's judgement.
/// </param>
public sealed record AiOptions(
    AiProvider Provider,
    string Model,
    string ReasoningEffort,
    int MaxDiffBytes,
    bool ConventionalCommits)
{
    /// <summary>
    /// CLAUDE.md's hard timeout. Enforced inside the generator rather than by the caller, so no
    /// surface can forget it.
    /// </summary>
    public static readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The runaway guard on the answer. The real control over length is the prompt; this only stops
    /// a model that has decided to write an essay from being paid for by the second.
    /// </summary>
    public const int MaxOutputTokens = 150;

    /// <summary>
    /// The model used when <see cref="Model"/> is empty.
    ///
    /// Haiku 4.5 for Anthropic: CLAUDE.md picks the fastest tier because this is short-output
    /// summarisation that does not benefit from reasoning.
    /// </summary>
    public string ResolvedModel => Model.Length > 0
        ? Model
        : Provider switch
        {
            AiProvider.Anthropic => "claude-haiku-4-5-20251001",
            AiProvider.OpenAi => "gpt-5.6-luna",

            //Copilot's base model, which every plan includes and which spends no premium request.
            //A faster tier exists, but a default that 404s on some subscriptions is worse than a
            //slower one that works on all of them -- and `aiModel` overrides this either way.
            AiProvider.Copilot => "gpt-4.1",

            _ => string.Empty,
        };
}
