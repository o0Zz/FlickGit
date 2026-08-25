namespace FlickGit.Ai;

public enum AiProvider
{
    Disabled,

    Anthropic,

    OpenAi,

    /// <summary>
    /// GitHub Copilot's chat endpoint. The only one that cannot be reached with the stored credential
    /// alone -- see <see cref="CopilotToken"/>.
    /// </summary>
    Copilot,

    /// <summary>
    /// Ollama, on this machine. <b>The only local provider, and the only one that differs in kind
    /// rather than in wire format:</b> no credential, because there is nobody to authenticate to; no
    /// default model, because which models exist is a fact about the user's disk; and nothing sent to
    /// it leaves the machine, which is the whole reason to want it.
    /// </summary>
    Ollama,
}

/// <summary>
/// Everything a generator needs that is not the diff itself. A value object rather than a settings
/// reference, so <c>FlickGit.Core</c> keeps knowing nothing about where settings live.
/// </summary>
/// <param name="Model">The model id. Empty means the provider's default.</param>
/// <param name="ReasoningEffort">OpenAI only. <c>none</c> is the latency baseline.</param>
/// <param name="MaxDiffBytes">
/// Above it the payload becomes a summary plus the first lines of each file's hunks.
/// </param>
public sealed record AiOptions(
    AiProvider Provider,
    string Model,
    string ReasoningEffort,
    int MaxDiffBytes,
    bool ConventionalCommits)
{
    /// <summary>
    /// Where Ollama is listening. An init property rather than a positional parameter, because it
    /// means nothing to the other three.
    /// </summary>
    public string OllamaUrl { get; init; } = DefaultOllamaUrl;

    public const string DefaultOllamaUrl = "http://localhost:11434";

    /// <summary>
    /// The silence budget for a provider reached over the internet. Enforced inside the generator
    /// rather than by the caller, so no surface can forget it.
    /// </summary>
    public static readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The same guard for a local model, and it has to be far larger.
    ///
    /// The budget measures <b>silence</b>, not total duration -- and a cold Ollama spends its first
    /// silence reading several gigabytes of weights off disk. Eight seconds would guillotine every
    /// first generation after a reboot and count it towards the tray warning. Two minutes is still a
    /// guard: a local server silent that long is not loading, it is wedged.
    /// </summary>
    public static readonly TimeSpan LocalTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long this provider may go quiet before it is treated as gone.</summary>
    public TimeSpan Silence => Provider == AiProvider.Ollama ? LocalTimeout : HardTimeout;

    /// <summary>
    /// How long the service start's warm-up may take. For the hosted three it is a TLS handshake,
    /// capped low so a provider that is down cannot hold a startup task open. For Ollama the warm-up
    /// <i>is</i> the model load, which is the whole value of doing it at logon.
    /// </summary>
    public TimeSpan WarmUpBudget => Provider == AiProvider.Ollama
        ? TimeSpan.FromSeconds(60)
        : TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether this provider needs a stored credential. A static function of the provider, because the
    /// settings window and the command line both have to answer it about one the user has only
    /// selected in a ComboBox and not yet saved.
    /// </summary>
    public static bool RequiresKey(AiProvider provider) =>
        provider is AiProvider.Anthropic or AiProvider.OpenAi or AiProvider.Copilot;

    /// <summary>
    /// The runaway guard on a commit message. The real control over length is the prompt.
    /// </summary>
    public const int CommitMaxTokens = 150;

    /// <summary>
    /// The same guard for a pull-request description, which is a different shape of answer: a title
    /// plus a few paragraphs of Markdown, where 150 tokens would cut the body off mid-list.
    /// </summary>
    public const int PullRequestMaxTokens = 700;

    /// <summary>
    /// And for a changelog, which is the longest answer the product ever asks for: a range of twenty
    /// commits is a page of entries. The ceiling has to sit above what was asked for, because a
    /// changelog cut off in the middle of a list reads exactly like a complete one -- there is no
    /// half-sentence to notice, only an entry that is not there.
    /// </summary>
    public const int ChangelogMaxTokens = 1200;

    /// <summary>The model used when <see cref="Model"/> is empty.</summary>
    public string ResolvedModel => Model.Length > 0
        ? Model
        : Provider switch
        {
            AiProvider.Anthropic => "claude-haiku-4-5-20251001",
            AiProvider.OpenAi => "gpt-5.6-luna",

            //Copilot's base model, which every plan includes and which spends no premium request. A faster
            //tier exists, but a default that 404s on some subscriptions is worse than a slower one that works
            //on all of them.
            AiProvider.Copilot => "gpt-4.1",

            //Ollama has no default, deliberately: the set of models is whatever the user has pulled onto
            //their own disk, so *any* guess 404s for most people -- with an error about a model they never
            //asked for. OllamaGenerator refuses an empty model by name instead.
            _ => string.Empty,
        };
}
