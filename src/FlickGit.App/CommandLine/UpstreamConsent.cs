using FlickGit.App.Localization;
using FlickGit.App.Settings;
using FlickGit.Commits;
using FlickGit.Models;

namespace FlickGit.App.CommandLine;

/// <summary>
/// Answers <see cref="CommitFlow"/>'s guardrail questions.
///
/// Both commit surfaces have to answer the same two questions the same way, and one of them —
/// "create an upstream?" — is consent to publish a branch to a remote other people read. CLAUDE.md
/// requires it to be asked once per repository and the answer remembered; a second copy of that
/// logic in the popup would be a second place for "once" to stop meaning once.
///
/// The dialog itself arrives as a callback, so nothing here constructs a window: the popup owns a
/// different one from the commit window, and a guardrail asked from the wrong owner appears behind
/// the surface that asked it.
/// </summary>
public sealed class UpstreamConsent(FlickSettings settings)
{
    /// <param name="ask">
    /// Shows the question and waits. Title, question, affirmative label, negative label; true only
    /// if the user chose the affirmative.
    /// </param>
    public async Task<bool> AnswerAsync(
        RepositoryInfo repository,
        CommitFlowQuestion question,
        Func<string, string, string, string, Task<bool>> ask)
    {
        if (question.Kind == CommitFlowQuestionKind.PullBeforePush)
        {
            //Asked every time. Unlike an upstream, pulling is not a one-off decision about the
            //repository -- it is a decision about the state it is in right now.
            return await ask(
                Strings.Get("push.pullfirst.title"),
                Strings.Get("push.pullfirst.ask", question.Branch ?? string.Empty, question.Upstream ?? string.Empty),
                Strings.Get("push.pullfirst.yes"),
                Strings.Get("commit.button.cancel")).ConfigureAwait(true);
        }

        if (settings.UpstreamAnswerFor(repository.Root) is { } remembered)
            return remembered;

        bool allow = await ask(
            Strings.Get("push.upstream.title"),
            Strings.Get("push.upstream.ask", question.Branch ?? string.Empty, question.Remote ?? "origin"),
            Strings.Get("push.upstream.yes"),
            Strings.Get("push.upstream.no")).ConfigureAwait(true);

        //Remembered either way. A user who said no once should not be asked again on every commit.
        settings.RememberUpstreamAnswer(repository.Root, allow);
        return allow;
    }
}
