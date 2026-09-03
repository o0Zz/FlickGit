using System.Diagnostics;
using System.Text;
using FlickGit.Ai;
using FlickGit.App.Localization;
using FlickGit.App.CommandLine;
using FlickGit.App.Settings;
using FlickGit.Diagnostics;
using FlickGit.History;
using FlickGit.Logging;
using FlickGit.Models;

namespace FlickGit.App.Ai;

/// <param name="Message">The finished text. Empty unless it succeeded.</param>
/// <param name="FailureReason">Why not, in the user's language. Already redacted.</param>
public readonly record struct GenerationOutcome(bool Succeeded, string Message, string? FailureReason);

/// <summary>
/// The one place AI text is generated, for every surface that wants some.
///
/// Every surface needs the payload, the stream, the silence budget, the timings, the failure
/// counter and the rule that a blank answer is a <i>failure</i> rather than empty text. Two copies
/// would be two chances for one of them to commit a placeholder.
///
/// The consecutive-failure count is deliberately <b>not</b> per surface: three failures raise one
/// tray warning whether they came from commit messages, descriptions or a mix, because what the
/// user needs to be told is that the provider is not working, not which button noticed.
/// </summary>
public sealed class AiTextService(
    IAiGenerator generator,
    AiContextBuilder contexts,
    PromptStore prompts,
    AiConfiguration config,
    INotifier notifier,
    OperationTimings timings,
    ILog log)
{
    /// <summary>Three consecutive failures raise a persistent tray warning.</summary>
    private const int DegradedAfter = 3;

    private int _consecutiveFailures;

    /// <summary>Whether asking is possible at all: a provider, and a key for it.</summary>
    public bool IsUsable => config.IsUsable;

    public int ConsecutiveFailures => _consecutiveFailures;

    /// <summary>The last refusal, already redacted by the generator. Null once one succeeds.</summary>
    public string? LastFailure { get; private set; }

    public Task<AiProbe> ProbeAsync(CancellationToken cancellationToken) =>
        generator.ProbeAsync(cancellationToken);

    /// <param name="onDelta">
    /// The text so far, once per fragment. Called on the caller's thread, so a UI caller is resumed on
    /// the dispatcher and needs no marshalling.
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
                //Read per generation, not cached: editing commit-prompt.md takes effect on the next
                //message, which is what makes iterating on the wording possible without restarting
                //the resident service.
                prompts.ForCommit(config.Options.ConventionalCommits).Text,
                context.ToPromptText(),
                AiOptions.CommitMaxTokens),
            "ai",
            CommitPrompt.Clean,
            onDelta,
            cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Streams a pull-request title and description. The answer is one piece of text with the title on
    /// its first line; splitting it is the caller's, because the window does it on every fragment so
    /// the title box fills in as it arrives rather than jumping at the end.
    /// </summary>
    /// <param name="mergeBase">
    /// Empty when the target is not known locally, which produces a description from the commit
    /// subjects alone rather than no description.
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
            new AiPrompt(prompts.ForPullRequest().Text, context.ToPromptText(), AiOptions.PullRequestMaxTokens),

            //Its own timing prefix, so `flick diag timings` can show that a description takes longer than a
            //commit message rather than averaging the two into one meaningless number.
            "ai.pr",
            text => text.Trim(),
            onDelta,
            cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Streams a changelog over a range of commits, for the log window.
    ///
    /// <b>The commits are passed in rather than read.</b> The log window already holds the whole range
    /// in memory and <see cref="History.CommitRange"/> has already sliced it, so reading it again
    /// would be a second answer to a question that has one -- and the two could differ, because a
    /// second <c>git log</c> would be over the branch rather than over the range.
    /// </summary>
    /// <param name="baseSpec">
    /// The range's left side, a bare object id. The same two specs the diff and the patch were
    /// computed from, so all three describe one range.
    /// </param>
    public async Task<GenerationOutcome> StreamChangelogAsync(
        RepositoryInfo repository,
        string baseSpec,
        string tipSpec,
        IReadOnlyList<LogCommit> commits,
        IReadOnlyList<GitFileChange> files,
        ChangelogStyle style,
        Action<string> onDelta,
        CancellationToken cancellationToken)
    {
        if (Unavailable() is { } refusal)
            return refusal;

        (AiContext? context, GenerationOutcome refused) = await GatherAsync(
            token => contexts.ForChangelogAsync(
                repository,
                baseSpec,
                tipSpec,
                commits,
                files,
                style,
                config.Options.MaxDiffBytes,
                token),
            Strings.Get("changelog.nothing"),
            cancellationToken).ConfigureAwait(true);

        if (context is null)
            return refused;

        return await StreamAsync(
            new AiPrompt(prompts.ForChangelog().Text, context.ToPromptText(), AiOptions.ChangelogMaxTokens),
            "ai.changelog",

            //Fence stripping rather than the description's bare trim: a changelog is Markdown, and a
            //model handed a Markdown task wraps the answer in ```markdown often enough to be worth
            //defending against. Clean leaves text that does not start with a fence alone.
            CommitPrompt.Clean,
            onDelta,
            cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Builds the payload, and turns the two ways it can produce nothing into the outcome to return.
    /// Neither counts towards the tray warning, because neither is the provider's fault.
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

    /// <param name="timingPrefix">
    /// Names the two measurements this records, <c>&lt;prefix&gt;.firsttoken</c> and
    /// <c>&lt;prefix&gt;.complete</c>.
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

                //Rendered as it arrives. This is the whole reason the interface streams.
                onDelta(text.ToString());
            }

            timings.Record($"{timingPrefix}.complete", clock.Elapsed);

            string finished = finish(text.ToString());

            //A blank answer is a failure, not empty text. This is the guard that makes "never commit an empty
            //or placeholder message" structural rather than a rule the queued-Enter path has to remember.
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
    /// The two reasons nothing can be asked, neither of which is a provider failure. Checked before a
    /// payload is built, so a repository with no key configured costs no Git call at all.
    /// </summary>
    private GenerationOutcome? Unavailable()
    {
        if (config.Provider == AiProvider.Disabled)
            return Failed(Strings.Get("ai.disabled"), count: false);

        //A missing key is only a reason for a provider that needs one. Ollama has none, and asking about
        //it here is how "no key stored" would have become the answer for a local model.
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

            //Once, on the call that crosses the threshold: a persistent warning rather than failing silently
            //on every commit, and equally not one warning per commit.
            if (_consecutiveFailures == DegradedAfter)
                notifier.Show(Strings.Get("app.name"), Strings.Get("ai.degraded", DegradedAfter, reason));
        }

        if (reason is { Length: > 0 })
            log.Warn($"Generation unavailable: {reason}");

        return new GenerationOutcome(false, string.Empty, reason);
    }
}
