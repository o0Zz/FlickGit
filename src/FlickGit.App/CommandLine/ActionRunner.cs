using System.Diagnostics;
using FlickGit.Actions;
using FlickGit.App.Localization;
using FlickGit.App.Resident;
using FlickGit.App.Views;
using FlickGit.Cli;
using FlickGit.Git;
using FlickGit.Logging;
using FlickGit.Models;

namespace FlickGit.App.CommandLine;

/// <summary>
/// Runs one catalog action, from whichever surface asked.
///
/// This is the only place an action executes, which is what makes the guardrails single-sited: the
/// confirmation for anything destructive, the "this starts a program outside FlickGit" warning for a
/// <see cref="ProcessRun"/>, and the stop-at-first-failure rule for a sequence all live here rather
/// than once per surface. CLAUDE.md: the fast surfaces are "not shortcuts around these rules".
///
/// A <see cref="WindowRun"/> is handed to <see cref="VerbRunner"/> rather than opened here, so a
/// built-in action and the CLI spelling of it are the same code path and cannot apply different
/// guardrails.
/// </summary>
/// <param name="verbs">
/// The verb runner, behind a factory.
///
/// <b>A <c>Func</c> because the dependency is a genuine cycle</b>, not because anything here is
/// optional: the verb runner needs <i>this</i> for <c>flick run</c>, and this needs the verb runner
/// for a <see cref="WindowRun"/>. Deferring one side of the cycle is what lets both arrive through a
/// constructor — which is the whole point, because the settable property this replaced was assigned
/// only by the resident startup path. A one-shot <c>flick run</c> therefore ran with it null and
/// silently opened nothing, and every built-in action is a <see cref="WindowRun"/>.
/// </param>
public sealed class ActionRunner(
    IGitProcessRunner git,
    Func<VerbRunner> verbs,
    Notifier notifier,
    ILog log)
{
    /// <summary>
    /// Runs <paramref name="action"/> against <paramref name="repository"/>.
    ///
    /// Never throws: a user action is user input, and a bad one has to report rather than take the
    /// resident service down with it.
    /// </summary>
    /// <param name="argument">
    /// The action's second token, when a surface collected one: the branch the palette highlighted,
    /// the tag name it typed. Only a <see cref="WindowRun"/> reads it, because that is the only
    /// variant whose target has a place to put one — a <c>GitRun</c> already carries its whole
    /// argument list and expands its placeholders from the repository.
    /// </param>
    public async Task RunAsync(
        GitAction action,
        RepositoryInfo repository,
        VerbOutput output,
        string? argument = null)
    {
        try
        {
            //Expanded first, so the confirmation shows the command that will run rather than the
            //declaration it came from. Approving `git reset --hard {branch}` is not informed consent.
            ActionRun run = ActionPlaceholders.Expand(action.Run, new ActionContext(repository));

            //Before anything executes, per CLAUDE.md: "Any destructive operation requires explicit
            //user intent, expressed in the moment."
            if (action.RequiresConfirmation && !Ask(action, run))
                return;

            if (run is WindowRun window)
            {
                //Through the verb runner, so the repository is resolved, the bare-repository guard
                //applies, and the window is the pre-warmed one.
                await verbs()
                    .RunAsync(new Verb(window.Verb, repository.Root, argument), output)
                    .ConfigureAwait(true);

                return;
            }

            Outcome outcome = await ExecuteAsync(run, action, repository).ConfigureAwait(true);

            Report(action, outcome, output);
        }
        catch (Exception ex)
        {
            log.Error($"Action '{action.Id}' failed: {ex}");
            output.Fail(action.Label, ex.Message);
        }
    }

    /// <param name="Succeeded">False as soon as any step fails.</param>
    /// <param name="Text">
    /// Whatever the steps printed, for an action asking for its output in a window. Collected even
    /// when it is not going to be shown: it is a few hundred bytes, and deciding per step whether to
    /// keep it would be a second thing to get wrong.
    /// </param>
    private readonly record struct Outcome(bool Succeeded, string Text)
    {
        public static readonly Outcome Failed = new(false, string.Empty);

        public static Outcome Ok(string text = "") => new(true, text);
    }

    /// <summary>
    /// Runs one step, or every step of a sequence in order, stopping at the first failure.
    /// </summary>
    private async Task<Outcome> ExecuteAsync(ActionRun run, GitAction action, RepositoryInfo repository)
    {
        switch (run)
        {
            case GitRun gitRun:
            {
                //Already expanded by the caller, which is also what the user was shown.
                IReadOnlyList<string> args = gitRun.Args;

                //Through the shared runner, so this call is quoted, cancellable and logged exactly
                //like every other Git call in the product.
                GitResult result = await git
                    .RunAsync(repository.Root, args, CancellationToken.None)
                    .ConfigureAwait(true);

                if (result.Succeeded)
                    return Outcome.Ok(result.StdOut);

                //Git's own words. CLAUDE.md, "Error Handling": never paraphrased, never generic.
                log.Warn($"Action '{action.Id}' failed: git {string.Join(' ', args)} -> {result.ExitCode}");
                notifier.Warn(action.Label, result.StdErr.Trim() is { Length: > 0 } text ? text : result.StdOut.Trim());
                return Outcome.Failed;
            }

            case ProcessRun processRun:
                return Start(processRun, repository) ? Outcome.Ok() : Outcome.Failed;

            case CompositeRun composite:
            {
                var collected = new List<string>();

                foreach (ActionRun step in composite.Steps)
                {
                    Outcome stepOutcome = await ExecuteAsync(step, action, repository).ConfigureAwait(true);

                    if (!stepOutcome.Succeeded)
                        return Outcome.Failed;

                    if (stepOutcome.Text.Trim().Length > 0)
                        collected.Add(stepOutcome.Text.TrimEnd());
                }

                return Outcome.Ok(string.Join("\n", collected));
            }

            default:
                //A WindowRun nested inside a composite. Not offered, and not worth a code path that
                //would have to decide what "the window closed" means as a step result.
                log.Warn($"Action '{action.Id}' contains a step that cannot run inside a sequence.");
                return Outcome.Failed;
        }
    }

    /// <summary>
    /// Starts an external program.
    ///
    /// <c>UseShellExecute = false</c> and an <c>ArgumentList</c>, the same rules as every Git call:
    /// no shell, no command string, no interpolation. The working directory is the repository, which
    /// is what makes a relative argument in a user action mean what the user expected.
    /// </summary>
    private bool Start(ProcessRun run, RepositoryInfo repository)
    {
        var start = new ProcessStartInfo
        {
            FileName = run.FileName,
            WorkingDirectory = repository.Root.Length > 0 ? repository.Root : null,
            UseShellExecute = false,
            CreateNoWindow = false,
        };

        foreach (string argument in run.Args)
            start.ArgumentList.Add(argument);

        //Not awaited, and deliberately: an external program is the user's, and holding the resident
        //service until it exits would hang the tray on anything interactive.
        using Process? started = Process.Start(start);

        return started is not null;
    }

    /// <summary>
    /// Asks the user to confirm, and waits.
    ///
    /// <c>ConfirmWindow</c> directly, as <see cref="RepositoryVerbs"/> already does. This was a
    /// settable delegate the composition root filled in with exactly this call and a null owner — so
    /// the indirection bought nothing and cost the one-shot path every confirmable action, which
    /// <see cref="ActionSafety"/> makes every <see cref="ProcessRun"/> and every destructive one.
    /// </summary>
    private static bool Ask(GitAction action, ActionRun run)
    {
        //Two different warnings, because they are two different risks: one can discard work, the
        //other runs something FlickGit knows nothing about.
        string body = run is ProcessRun
            ? Strings.Get("action.confirm.process", action.Label, run.Describe())
            : Strings.Get("action.confirm.destructive", action.Label, run.Describe());

        return ConfirmWindow.Ask(
            null,
            Strings.Get("action.confirm.title"),
            body,
            Strings.Get("action.confirm.yes"),
            Strings.Get("action.confirm.no"));
    }

    private void Report(GitAction action, Outcome outcome, VerbOutput output)
    {
        if (!outcome.Succeeded)
            //Already reported with Git's own words, where there were any.
            return;

        switch (action.Output)
        {
            case ActionOutput.Window:
                //The whole of what it printed. An action asking for a window is an action whose
                //output is the point -- `fetch --prune` naming what it deleted, say -- so an empty
                //result still gets a window rather than being silently downgraded to nothing.
                output.Notice(
                    action.Label,
                    outcome.Text.Trim() is { Length: > 0 } text ? text : Strings.Get("action.ran", action.Label),
                    compact: false);

                break;

            case ActionOutput.Toast:
                notifier.Success(action.Label, Strings.Get("action.ran", action.Label));
                break;

            case ActionOutput.None:
            default:
                break;
        }
    }
}
