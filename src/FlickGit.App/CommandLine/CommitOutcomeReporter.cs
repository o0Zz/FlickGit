using FlickGit.App.Localization;
using FlickGit.Commits;

namespace FlickGit.App.CommandLine;

/// <summary>
/// Turns a <see cref="CommitFlowResult"/> into the words a user reads. Nine outcomes to phrase.
///
/// <b>One caller: <c>CommitViewModel</c>.</b> This used to claim it was separate "because the
/// command line has to phrase the same nine", and that was never true -- no command-line path runs
/// <see cref="CommitFlow"/> at all. <c>flick commit</c> opens the window, and <c>flick push</c>
/// phrases its own refusals straight from the <c>push.*</c> keys. Hard Requirement 2 would have it
/// folded into its caller on that basis, and it is not, for the reason the same document gives two
/// bullets earlier: the caller is already the largest file in the product, and sixty lines of
/// outcome-to-string mapping in the middle of its state machine makes both harder to read. What is
/// here is a pure function of its argument that happens to live in its own file.
///
/// Presentation, so it stays in the App assembly. The decision each outcome describes was already
/// made in <see cref="CommitFlow"/>, which is where it is tested.
/// </summary>
public static class CommitOutcomeReporter
{
    /// <param name="Title">The operation, for a window title or a notice heading.</param>
    /// <param name="Message">What happened, in full.</param>
    public readonly record struct Report(string Title, string Message);

    /// <summary>
    /// The one-line success text: the hash and the subject, or the push confirmation when it also
    /// pushed.
    ///
    /// Null when nothing was committed, so a caller can tell "say nothing" from "say this".
    /// </summary>
    public static string? SuccessText(CommitFlowResult result)
    {
        if (result.Commit is not { } commit)
            return null;

        if (result.Outcome == CommitFlowOutcome.Committed)
        {
            if (result.Pushed)
                return Strings.Get("push.success", result.Branch ?? string.Empty, result.Upstream ?? string.Empty);

            //A commit that had nothing to push says so rather than claiming a push.
            if (result.Detail is { Length: > 0 } note)
                return note;
        }

        return Strings.Get("commit.success", commit.ShortHash, commit.Subject);
    }

    /// <summary>
    /// The failure or refusal to show, or null when there is nothing to say.
    ///
    /// <see cref="CommitFlowOutcome.Cancelled"/> returns null on purpose: the user declined a
    /// guardrail question a moment ago and does not need to be told what they just chose.
    /// </summary>
    public static Report? FailureText(CommitFlowResult result) => result.Outcome switch
    {
        CommitFlowOutcome.Committed or CommitFlowOutcome.Cancelled => null,

        CommitFlowOutcome.InvalidBranchName =>
            new Report(Strings.Get("branch.label"), result.Detail ?? string.Empty),

        //Nothing was modified or discarded. The stash path is offered in the Switch branch window,
        //never taken on the user's behalf here.
        CommitFlowOutcome.SwitchRefused => new Report(
            Strings.Get("branch.label"),
            result.Files.Count > 0
                ? Strings.Get("branch.blocked", string.Join('\n', result.Files))
                  + "\n\n" + Strings.Get("branch.blocked.hint")
                : result.Detail ?? string.Empty),

        CommitFlowOutcome.AbortedSelectionChanged => new Report(
            Strings.Get("branch.label"),
            Strings.Get("branch.aborted", string.Join('\n', result.Files))),

        CommitFlowOutcome.NothingToCommit =>
            new Report(Strings.Get("app.name"), Strings.Get("commit.empty.selection")),

        CommitFlowOutcome.PushRefused or CommitFlowOutcome.PushFailed =>
            new Report(Strings.Get("push.refused.title"), result.Detail ?? string.Empty),

        CommitFlowOutcome.PullFailed => new Report(
            Strings.Get("pull.conflict"),
            (result.Detail ?? string.Empty)
            + (result.Suggestion is { Length: > 0 } ? $"\n\n{result.Suggestion}" : string.Empty)),

        _ => new Report(Strings.Get("error.title"), result.Detail ?? string.Empty),
    };
}
