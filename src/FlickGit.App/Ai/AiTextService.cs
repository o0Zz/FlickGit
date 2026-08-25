using System.Diagnostics;
using System.Text;
using FlickGit.Ai;
using FlickGit.App.Localization;
using FlickGit.App.Resident;
using FlickGit.App.Settings;
using FlickGit.Diagnostics;
using FlickGit.History;
using FlickGit.Logging;
using FlickGit.Models;

namespace FlickGit.App.Ai;

/// <param name="Succeeded">True only when there is usable text.</param>
/// <param name="Message">The finished text. Empty unless it succeeded.</param>
/// <param name="FailureReason">Why not, in the user's language. Already redacted.</param>
public readonly record struct GenerationOutcome(bool Succeeded, string Message, string? FailureReason);

/// <summary>
/// The one place AI text is generated, for every surface that wants some.
///
/// Every surface needs the payload, the stream, the 8-second timeout, the timings, the failure
/// counter and the rule that a blank answer is a <i>failure</i> rather than empty text. Two copies of
/// that would be two chances for one of them to commit a placeholder, which CLAUDE.md forbids
/// outright.
///
/// It was <c>CommitMessageService</c>, and the pull-request description is why it is not any more:
/// the second surface needs all six of those things and a different prompt. What is <b>not</b>
/// duplicated is the consecutive-failure count — three failures raise one tray warning whether they
/// came from commit messages, descriptions or a mix, because what the user needs to be told is that
/// the provider is not working, not which button noticed.
///
/// The two public methods differ only in what they build a payload out of. Everything after that is
/// <see cref="StreamAsync"/>.
/// </summary>
public sealed class AiTextService(
    IAiGenerator generator,
    AiContextBuilder contexts,
    AiConfiguration config,
    Notifier notifier,
    OperationTimings timings,
    ILog log)
{
    /// <summary>
    /// CLAUDE.md: "Three consecutive failures: persistent tray warning rather than failing silently
    /// on every commit."
    /// </summary>
    private const int DegradedAfter = 3;

    private int _consecutiveFailures;

    /// <summary>Whether asking is possible at all: a provider, and a key for it.</summary>
    public bool IsUsable => config.IsUsable;

    /// <summary>How many times in a row the provider has refused. Reported by `flick ai`.</summary>
    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>The last refusal, already redacted by the generator. Null once one succeeds.</summary>
    public string? LastFailure { get; private set; }

    /// <summary>Establishes the pooled connection, and answers whether the provider is reachable.</summary>
    public Task<AiProbe> ProbeAsync(CancellationToken cancellationToken) =>
        generator.ProbeAsync(cancellationToken);

    /// <summary>
    /// Streams a commit message for the ticked files in <paramref name="status"/>.
    /// </summary>
    /// <param name="onDelta">
    /// The text so far, once per fragment. Called on the caller's thread, so a UI caller is resumed
    /// on the dispatcher and needs no marshalling.
    /// </param>
    public async Task<GenerationOutcome> StreamCommitMessageAsync(
        RepositoryInfo repository,
        RepositoryStatus status,
        Action<string> onDelta,
        CancellationToken cancellationToken)
    {
        if (Unavailable() is { } refusal)
            return refusal;

        (AiContext? context, GenerationOutcome refused) = await GatherAsync(
            token => contexts.ForCommitAsync(repository, status, config.Options.MaxDiffBytes, token),
            Strings.Get("commit.empty.selection"),
            cancellationToken).ConfigureAwait(true);

        if (context is null)
            return refused;

        return await StreamAsync(
            new AiPrompt(
                CommitPrompt.For(config.Options.ConventionalCommits),
                context.ToPromptText(),
                AiOptions.CommitMaxTokens),
            "ai",
            CommitPrompt.Clean,
            onDelta,
            cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Streams a pull-request title and description for a branch.
    ///
    /// The answer is one piece of text with the title on its first line — see
    /// <see cref="PullRequestPrompt"/> for why it is not two requests. Splitting it is the caller's,
    /// because the window does it on every fragment so the title box fills in as it arrives rather
    /// than jumping at the end.
    /// </summary>
    /// <param name="mergeBase">
    /// Where the branch parted from its target. Empty when the target is not known locally, which
    /// produces a description from the commit subjects alone rather than no description.
    /// </param>
    public async Task<GenerationOutcome> StreamPullRequestAsync(
        RepositoryInfo repository,
        string mergeBase,
        string sourceBranch,
        string targetBranch,
        IReadOnlyList<LogCommit> branchCommits,
        IReadOnlyList<GitFileChange> files,
        Action<string> onDelta,
        CancellationToken cancellationToken)
    {
        if (Unavailable() is { } refusal)
            return refusal;

        (AiContext? context, GenerationOutcome refused) = await GatherAsync(
            token => contexts.ForPullRequestAsync(
                repository,
                mergeBase,
                sourceBranch,
                targetBranch,
                branchCommits,
                files,
                config.Options.MaxDiffBytes,
                token),
            Strings.Get("pr.nothing"),
            cancellationToken).ConfigureAwait(true);

        if (context is null)
            return refused;

        return await StreamAsync(
            new AiPrompt(PullRequestPrompt.System, context.ToPromptText(), AiOptions.PullRequestMaxTokens),

            //Its own timing prefix, so `flick diag timings` can show that a description takes longer
            //than a commit message rather than averaging the two into one meaningless number.
            "ai.pr",
            text => text.Trim(),
            onDelta,
            cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Builds the payload, and turns the two ways it can produce nothing into the outcome to return.
    ///
    /// A null context means "do not generate", and the second half of the tuple says why: an empty
    /// selection is a refusal with a sentence, and a cancellation is a refusal with none — neither
    /// counts towards the tray warning, because neither is the provider's fault.
    /// </summary>
    private async Task<(AiContext? Context, GenerationOutcome Refused)> GatherAsync(
        Func<CancellationToken, Task<AiContext>> gather,
        string nothingToSay,
        CancellationToken cancellationToken)
    {
        try
        {
            AiContext context = await gather(cancellationToken).ConfigureAwait(true);

            return context.IsEmpty
                ? (null, Failed(nothingToSay, count: false))
                : (context, default);
        }
        catch (OperationCanceledException)
        {
            return (null, Failed(null, count: false));
        }
    }

    /// <summary>
    /// The half both surfaces share: run the stream, count the failures, and treat a blank answer as
    /// one.
    /// </summary>
    /// <param name="timingPrefix">
    /// Names the two measurements this records, <c>&lt;prefix&gt;.firsttoken</c> and
    /// <c>&lt;prefix&gt;.complete</c> — the two rows CLAUDE.md's table has for the AI.
    /// </param>
    /// <param name="finish">
    /// Tidies the finished text. Fence stripping for a commit message; a trim for a description,
    /// whose Markdown must survive intact.
    /// </param>
    private async Task<GenerationOutcome> StreamAsync(
        AiPrompt prompt,
        string timingPrefix,
        Func<string, string> finish,
        Action<string> onDelta,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        bool firstTokenSeen = false;

        try
        {
            var text = new StringBuilder();

            await foreach (string delta in generator.GenerateAsync(prompt, cancellationToken).ConfigureAwait(true))
            {
                if (!firstTokenSeen)
                {
                    firstTokenSeen = true;
                    timings.Record($"{timingPrefix}.firsttoken", clock.Elapsed);
                }

                text.Append(delta);

                //Rendered as it arrives. This is the whole reason the interface streams, and it is
                //what makes the wait feel like nothing when combined with a queued Enter.
                onDelta(text.ToString());
            }

            timings.Record($"{timingPrefix}.complete", clock.Elapsed);

            string finished = finish(text.ToString());

            //A blank answer is a failure, not empty text. This is the guard that makes "never commit
            //an empty or placeholder message" structural rather than a rule the queued-Enter path has
            //to remember.
            if (finished.Length == 0)
                return Failed(Strings.Get("ai.empty"), count: true);

            _consecutiveFailures = 0;
            LastFailure = null;
            return new GenerationOutcome(true, finished, null);
        }
        catch (OperationCanceledException)
        {
            //The user typed, or closed the window. Not a provider failure and not counted.
            return Failed(null, count: false);
        }
        catch (AiUnavailableException ex)
        {
            return Failed(ex.Message, count: true);
        }
        catch (Exception ex)
        {
            log.Error($"Generation failed: {ex}");
            return Failed(ex.Message, count: true);
        }
    }

    /// <summary>
    /// The two reasons nothing can be asked, neither of which is a provider failure.
    ///
    /// Checked before a payload is built rather than after, so a repository with no key configured
    /// costs no Git call at all.
    /// </summary>
    private GenerationOutcome? Unavailable()
    {
        if (config.Provider == AiProvider.Disabled)
            return Failed(Strings.Get("ai.disabled"), count: false);

        //A missing key is only a reason for a provider that needs one. Ollama has none, and asking
        //about it here is how "no key stored" would have become the answer for a local model.
        return !config.RequiresKey || config.HasKey
            ? null
            : Failed(Strings.Get("ai.nokey"), count: false);
    }

    private GenerationOutcome Failed(string? reason, bool count)
    {
        if (count && reason is { Length: > 0 })
        {
            LastFailure = reason;
            _consecutiveFailures++;

            //Once, on the call that crosses the threshold. CLAUDE.md wants a persistent warning
            //"rather than failing silently on every commit" -- and equally, not one per commit.
            if (_consecutiveFailures == DegradedAfter)
                notifier.Warn(Strings.Get("app.name"), Strings.Get("ai.degraded", DegradedAfter, reason));
        }

        if (reason is { Length: > 0 })
            log.Warn($"Generation unavailable: {reason}");

        return new GenerationOutcome(false, string.Empty, reason);
    }
}
