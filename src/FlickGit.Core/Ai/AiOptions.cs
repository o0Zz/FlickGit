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

    /// <summary>
    /// Ollama, on this machine.
    ///
    /// <b>The only local provider, and the only one that differs in kind rather than in wire
    /// format.</b> It needs no credential, because there is nobody to authenticate to; it has no
    /// default model, because which models exist is a fact about the user's disk; and nothing it is
    /// sent leaves the machine, which is the whole reason to want it — a policy that forbids source
    /// code reaching a third party forbids the other three outright.
    /// </summary>
    Ollama,
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
    /// Where Ollama is listening. An init property rather than a positional parameter, because it
    /// means nothing to the other three.
    /// </summary>
    public string OllamaUrl { get; init; } = DefaultOllamaUrl;

    /// <summary>Ollama's own default port, on this machine.</summary>
    public const string DefaultOllamaUrl = "http://localhost:11434";

    /// <summary>
    /// CLAUDE.md's hard timeout, for a provider reached over the internet. Enforced inside the
    /// generator rather than by the caller, so no surface can forget it.
    /// </summary>
    public static readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The same guard for a local model, and it has to be far larger.
    ///
    /// The budget measures <b>silence</b>, not total duration — and a cold Ollama spends its first
    /// silence reading several gigabytes of weights off disk before it can emit a token. Eight
    /// seconds would guillotine every first generation after a reboot, report it as "the provider
    /// stopped answering", and count it towards the tray warning. Two minutes is still a guard: a
    /// local server that has said nothing for that long is not loading, it is wedged.
    /// </summary>
    public static readonly TimeSpan LocalTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long this provider may go quiet before it is treated as gone.</summary>
    public TimeSpan Silence => Provider == AiProvider.Ollama ? LocalTimeout : HardTimeout;

    /// <summary>
    /// How long the service start's warm-up may take.
    ///
    /// For the hosted three it is a TLS and HTTP/2 handshake, which is done in well under a second
    /// and is capped low so a provider that is down cannot hold a startup task open. For Ollama the
    /// warm-up <i>is</i> the model load — see <c>OllamaGenerator.ProbeAsync</c> — which is the whole
    /// value of doing it at logon rather than on the first commit of the day.
    /// </summary>
    public TimeSpan WarmUpBudget => Provider == AiProvider.Ollama
        ? TimeSpan.FromSeconds(60)
        : TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether this provider needs a stored credential.
    ///
    /// A static function of the provider rather than a property, because the settings window and the
    /// command line both have to answer it about a provider they are only <i>considering</i> — one
    /// the user has selected in a ComboBox and not yet saved.
    /// </summary>
    public static bool RequiresKey(AiProvider provider) =>
        provider is AiProvider.Anthropic or AiProvider.OpenAi or AiProvider.Copilot;

    /// <summary>
    /// The runaway guard on a commit message. The real control over length is the prompt; this only
    /// stops a model that has decided to write an essay from being paid for by the second.
    /// </summary>
    public const int CommitMaxTokens = 150;

    /// <summary>
    /// The same guard for a pull-request description, which is a different shape of answer: a title
    /// plus a few paragraphs of Markdown, where 150 tokens would cut the body off mid-list.
    ///
    /// Still a guard rather than a target — the prompt asks for something short, and a description
    /// that reaches this ceiling is a model that ignored it.
    /// </summary>
    public const int PullRequestMaxTokens = 700;

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

            //Ollama has no default, deliberately. The other three offer a fixed catalogue, so naming
            //the fastest tier is a safe guess; here the set of models is whatever the user has pulled
            //onto their own disk, so *any* guess 404s for most people -- with an error about a model
            //they never asked for. `OllamaGenerator` refuses an empty model by name instead, and the
            //message says to run `ollama list`.
            _ => string.Empty,
        };
}
