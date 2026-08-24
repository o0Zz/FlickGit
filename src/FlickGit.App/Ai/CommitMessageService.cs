using System.Diagnostics;
using System.Text;
using FlickGit.Ai;
using FlickGit.App.Localization;
using FlickGit.App.Resident;
using FlickGit.App.Settings;
using FlickGit.Diagnostics;
using FlickGit.Logging;
using FlickGit.Models;

namespace FlickGit.App.Ai;

/// <param name="Succeeded">True only when there is a usable message.</param>
/// <param name="Message">The finished text. Empty unless it succeeded.</param>
/// <param name="FailureReason">Why not, in the user's language. Already redacted.</param>
public readonly record struct GenerationOutcome(bool Succeeded, string Message, string? FailureReason);

/// <summary>
/// The one place a commit message is generated, for every surface that wants one.
///
/// Every surface that wants one needs the payload, the stream, the 8-second timeout, the timings,
/// the failure counter and the rule that a blank answer is a <i>failure</i> rather than an empty
/// message. Two copies of that would be two chances for one of them to commit a placeholder, which
/// CLAUDE.md forbids outright.
///
/// The only state it keeps is the consecutive-failure count, which has to outlive one window for the
/// warning threshold to mean anything.
/// </summary>
public sealed class CommitMessageService(
    ICommitMessageGenerator generator,
    CommitContextBuilder contexts,
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
    /// Streams a message for the ticked files in <paramref name="status"/>.
    ///
    /// Never throws. Every failure comes back as <see cref="GenerationOutcome.FailureReason"/>,
    /// because the caller's only correct response to any of them is the same: leave an editable box
    /// and let the user type.
    /// </summary>
    /// <param name="onDelta">
    /// The message so far, once per fragment. Called on the caller's thread, so a UI caller is
    /// resumed on the dispatcher and needs no marshalling.
    /// </param>
    public async Task<GenerationOutcome> StreamAsync(
        RepositoryInfo repository,
        RepositoryStatus status,
        Action<string> onDelta,
        CancellationToken cancellationToken)
    {
        if (config.Provider == AiProvider.Disabled)
            return Failed(Strings.Get("ai.disabled"), count: false);

        if (!config.HasKey)
            return Failed(Strings.Get("ai.nokey"), count: false);

        var clock = Stopwatch.StartNew();
        bool firstTokenSeen = false;

        try
        {
            CommitContext context = await contexts
                .BuildAsync(repository, status, config.Options.MaxDiffBytes, cancellationToken)
                .ConfigureAwait(true);

            if (context.IsEmpty)
                return Failed(Strings.Get("commit.empty.selection"), count: false);

            var message = new StringBuilder();

            await foreach (string delta in generator.GenerateAsync(context, cancellationToken).ConfigureAwait(true))
            {
                if (!firstTokenSeen)
                {
                    firstTokenSeen = true;
                    timings.Record("ai.firsttoken", clock.Elapsed);
                }

                message.Append(delta);

                //Rendered as it arrives. This is the whole reason the interface streams, and it is
                //what makes the wait feel like nothing when combined with a queued Enter.
                onDelta(message.ToString());
            }

            timings.Record("ai.complete", clock.Elapsed);

            string finished = CommitPrompt.Clean(message.ToString());

            //A blank answer is a failure, not an empty message. This is the guard that makes
            //"never commit an empty or placeholder message" structural rather than a rule the
            //queued-Enter path has to remember.
            if (finished.Length == 0)
                return Failed(Strings.Get("ai.empty"), count: true);

            _consecutiveFailures = 0;
            LastFailure = null;
            return new GenerationOutcome(true, finished, null);
        }
        catch (OperationCanceledException)
        {
            //The user typed, or dismissed the popup. Not a provider failure and not counted.
            return Failed(null, count: false);
        }
        catch (AiUnavailableException ex)
        {
            return Failed(ex.Message, count: true);
        }
        catch (Exception ex)
        {
            log.Error($"Message generation failed: {ex}");
            return Failed(ex.Message, count: true);
        }
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
            log.Warn($"Message generation unavailable: {reason}");

        return new GenerationOutcome(false, string.Empty, reason);
    }
}
