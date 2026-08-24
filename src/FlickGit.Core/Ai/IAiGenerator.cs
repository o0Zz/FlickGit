using System.Runtime.CompilerServices;

namespace FlickGit.Ai;

/// <summary>
/// One request to a provider: what it is being told to do, and what it is being shown.
///
/// A value rather than two string parameters, so the two halves cannot be passed the wrong way
/// round — and so the token ceiling travels with the request that needs it. A commit subject and a
/// pull-request description are the same call with very different budgets, and a single constant on
/// <see cref="AiOptions"/> would either truncate the description or stop guarding the subject.
/// </summary>
/// <param name="System">The system prompt: the rules, and nothing about this change.</param>
/// <param name="User">The payload. Already capped, filtered and redacted.</param>
/// <param name="MaxTokens">The runaway guard on the answer, not the length control.</param>
public readonly record struct AiPrompt(string System, string User, int MaxTokens);

/// <summary>
/// Writes text, a token at a time.
///
/// The return type is a stream and not a <c>Task&lt;string&gt;</c> on purpose — CLAUDE.md:
/// "streaming is a requirement, not an option". The user perceives time to first token, and with a
/// capped diff the first words land in roughly 300–500 ms even though the whole message takes twice
/// that.
///
/// <b>It generates text, not commit messages.</b> It was <c>ICommitMessageGenerator</c> and took a
/// <c>CommitContext</c>, which made the one thing every provider does identically — post a system
/// prompt and a payload, read the deltas — reachable only by the commit surface. The pull-request
/// description needs exactly that and a different prompt, so the prompt became an argument. Per Hard
/// Requirement 1 the signature changed rather than gaining an overload beside it.
/// </summary>
public interface IAiGenerator
{
    /// <summary>
    /// Yields fragments of the answer as they arrive. Throws
    /// <see cref="AiUnavailableException"/> when the provider will not answer.
    /// </summary>
    IAsyncEnumerable<string> GenerateAsync(AiPrompt prompt, CancellationToken cancellationToken);

    /// <summary>
    /// One cheap request that establishes the pooled TLS/HTTP2 connection, and reports whether the
    /// provider is reachable.
    ///
    /// A second member on an interface CLAUDE.md pins as having one, and it earns its place: the
    /// alternative is the composition root knowing each provider's hostname in order to satisfy the
    /// warm-connection requirement two sections earlier. Sends no diff and needs no key.
    /// </summary>
    Task<AiProbe> ProbeAsync(CancellationToken cancellationToken);
}

/// <param name="Reachable">False when the provider could not be contacted at all.</param>
/// <param name="Elapsed">How long it took, for `flick diag doctor`.</param>
/// <param name="Error">Why not. Already redacted.</param>
public readonly record struct AiProbe(bool Reachable, TimeSpan Elapsed, string? Error);

/// <summary>
/// The provider said no, or said nothing.
///
/// Never carries a key: the provider's own error text goes through
/// <see cref="Secrets.SecretDetector.Redact"/> before it reaches this message, because an API that
/// echoes a bad request back would otherwise put the key in the log.
/// </summary>
public sealed class AiUnavailableException(string reason) : Exception(reason);

/// <summary>
/// The provider for "no AI, thank you".
///
/// A real implementation rather than a null check at every call site: CLAUDE.md requires that every
/// feature works with the provider disabled, and the cheapest way to guarantee that is for
/// "disabled" to be the same code path as "enabled" with nothing to say.
/// </summary>
public sealed class DisabledAiGenerator : IAiGenerator
{
    public async IAsyncEnumerable<string> GenerateAsync(
        AiPrompt prompt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        //An empty stream. `yield break` after an await keeps this a genuine async iterator without
        //a compiler warning about the unused parameters.
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public Task<AiProbe> ProbeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AiProbe(false, TimeSpan.Zero, "disabled"));
}
