using FlickGit.App.Localization;
using FlickGit.App.Settings;
using FlickGit.App.Views;
using FlickGit.Branches;
using FlickGit.Cli;
using FlickGit.Models;
using FlickGit.Remotes;
using FlickGit.Status;
using FlickGit.Tags;

namespace FlickGit.App.CommandLine;

/// <summary>
/// The verbs that answer with text about a repository: status, switch, push.
///
/// Text rather than a window is the product's distinction, not this file's: the CLI stub waits for
/// exactly these and forwards their exit code, and refuses to wait for the ones that open something.
/// CLAUDE.md's exit-code table exists so a script can branch on the answer, which only works if the
/// answer arrives before the process ends.
///
/// <see cref="VerbOutput"/> is a parameter rather than a constructor dependency. Where the answer
/// goes is per invocation — a direct launch prints to its own console, a pipe request collects the
/// text for the response — and passing it in is what lets this class be a singleton with nothing to
/// reset between two right-clicks.
/// </summary>
public sealed class RepositoryVerbs(
    StatusService status,
    SwitchService switches,
    PushService pushes,
    TagService tags,
    FlickSettings settings)
{
    /// <summary>`flick status` — the file list as text.</summary>
    public async Task<VerbResult> StatusAsync(VerbOutput output, RepositoryInfo repository)
    {
        RepositoryStatus state = await status
            .GetStatusAsync(repository, CancellationToken.None)
            .ConfigureAwait(true);

        output.Line($"{repository.Name}  ({repository.Root})");

        output.Line(
            state.IsDetachedHead ? "HEAD detached"
            : state.Branch is null ? "no branch"
            : $"{state.Branch}{(state.Upstream is null ? "  (no upstream)" : $"  ↑{state.Ahead} ↓{state.Behind}")}");

        if (state.Files.Count == 0)
        {
            output.Line("clean");
            return VerbResult.Exit(ExitCodes.Success);
        }

        output.Line();

        foreach (GitFileChange file in state.Files)
        {
            //"bin" rather than a count, because a binary file has no honest line count.
            string counts = file.IsBinary ? "bin" : $"+{file.AddedLines ?? 0} -{file.RemovedLines ?? 0}";
            output.Line($"  {file.DisplayStatus.ToShortCode()}  {file.Path,-60} {counts}");
        }

        return VerbResult.Exit(ExitCodes.Success);
    }

    /// <summary>
    /// `flick switch &lt;path&gt; &lt;branch&gt;` — a direct switch, no picker.
    ///
    /// A script naming a branch must not stop to show a list. A refusal reports the blocking files
    /// and exits 5; it never stashes, because that is a decision the picker asks for explicitly.
    /// </summary>
    public async Task<VerbResult> SwitchAsync(VerbOutput output, RepositoryInfo repository, string branch)
    {
        RepositoryStatus state = await status
            .GetStatusAsync(repository, CancellationToken.None)
            .ConfigureAwait(true);

        string target = branch.Trim();
        string title = Strings.Get("switch.button");

        if (string.Equals(target, state.Branch, StringComparison.Ordinal))
        {
            //Already there. CLAUDE.md, "Branch Selector": naming the current branch performs no
            //switch at all.
            output.Say(title, Strings.Get("branch.switched", target));
            return VerbResult.Exit(ExitCodes.Success);
        }

        SwitchOutcome outcome = await switches
            .SwitchAsync(repository, target, CancellationToken.None)
            .ConfigureAwait(true);

        if (outcome.Succeeded)
        {
            output.Say(title, Strings.Get("branch.switched", target));
            return VerbResult.Exit(ExitCodes.Success);
        }

        if (outcome.RefusedByLocalChanges)
        {
            output.Fail(
                title,
                Strings.Get("branch.blocked", string.Join('\n', outcome.BlockingFiles))
                + "\n\n" + Strings.Get("branch.blocked.hint"));

            return VerbResult.Exit(ExitCodes.RefusedForSafety);
        }

        output.Fail(title, outcome.GitError ?? string.Empty);
        return VerbResult.Exit(ExitCodes.GitError);
    }

    /// <summary>
    /// `flick tag &lt;path&gt; &lt;name&gt;` — creates that tag on HEAD.
    ///
    /// Create and nothing else. The window is where deletion lives, because there is no `--force`
    /// under this and so nothing here can overwrite anything, whereas deleting a published tag is
    /// exactly the "explicit user intent, expressed in the moment" that a script flag is not.
    /// </summary>
    public async Task<VerbResult> TagAsync(VerbOutput output, RepositoryInfo repository, string name)
    {
        string title = Strings.Get("tag.create");

        //Lightweight: a message would have to come from somewhere, and a second positional argument
        //that is sometimes a message is a grammar nobody can remember. `git tag -a` is right there
        //for anyone who wants one from a script.
        TagOutcome outcome = await tags
            .CreateAsync(repository, name, null, null, CancellationToken.None)
            .ConfigureAwait(true);

        if (outcome.Succeeded)
        {
            output.Say(title, Strings.Get("tag.created", name.Trim()));
            return VerbResult.Exit(ExitCodes.Success);
        }

        output.Fail(title, outcome.GitError ?? string.Empty);
        return VerbResult.Exit(ExitCodes.GitError);
    }

    /// <summary>
    /// `flick push`, with every guardrail.
    ///
    /// The one thing it will not do is push a diverged branch, or offer to force it.
    /// </summary>
    public async Task<VerbResult> PushAsync(VerbOutput output, RepositoryInfo repository)
    {
        RepositoryStatus state = await status
            .GetStatusAsync(repository, CancellationToken.None)
            .ConfigureAwait(true);

        PushPlan plan = await pushes.PlanAsync(repository, state, CancellationToken.None).ConfigureAwait(true);

        switch (plan.Action)
        {
            case PushAction.Refuse:
                output.Fail(Strings.Get("push.refused.title"), plan.Reason!);
                return VerbResult.Exit(ExitCodes.RefusedForSafety);

            case PushAction.NothingToPush:
                output.Say(Strings.Get("push.button"), plan.Reason!);
                return VerbResult.Exit(ExitCodes.Success);

            case PushAction.PullThenPush:
                //Offered rather than attempted: the pull hits the network and can stop on a rebase
                //conflict, so it is not something a Push command does silently.
                output.Fail(
                    Strings.Get("push.pullfirst.title"),
                    Strings.Get("push.pullfirst.ask", plan.Branch ?? string.Empty, plan.Upstream ?? string.Empty));

                return VerbResult.Exit(ExitCodes.RefusedForSafety);

            case PushAction.SetUpstream when !ConsentToUpstream(repository, plan):
                return VerbResult.Exit(ExitCodes.UserCancelled);
        }

        PushOutcome outcome = await pushes.ExecuteAsync(repository, plan, CancellationToken.None).ConfigureAwait(true);

        if (outcome.Succeeded)
        {
            output.Say(
                Strings.Get("push.button"),
                Strings.Get("push.success", plan.Branch ?? string.Empty, plan.Upstream ?? plan.Remote ?? "origin"));

            return VerbResult.Exit(ExitCodes.Success);
        }

        output.Fail(Strings.Get("push.button"), outcome.Error ?? string.Empty);
        return VerbResult.Exit(outcome.Refused ? ExitCodes.RefusedForSafety : ExitCodes.GitError);
    }

    /// <summary>
    /// Asks once per repository whether an upstream may be created, and remembers the answer.
    ///
    /// A dialog even from the command line: this is consent to publish a branch to a remote other
    /// people read, and there is no terminal to prompt on.
    /// </summary>
    private bool ConsentToUpstream(RepositoryInfo repository, PushPlan plan)
    {
        if (settings.UpstreamAnswerFor(repository.Root) is { } remembered)
            return remembered;

        bool allow = ConfirmWindow.Ask(
            owner: null,
            Strings.Get("push.upstream.title"),
            Strings.Get("push.upstream.ask", plan.Branch ?? string.Empty, plan.Remote ?? "origin"),
            Strings.Get("push.upstream.yes"),
            Strings.Get("push.upstream.no"));

        settings.RememberUpstreamAnswer(repository.Root, allow);
        return allow;
    }
}
