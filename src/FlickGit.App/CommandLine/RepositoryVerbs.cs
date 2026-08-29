using System.IO;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.App.Views;
using FlickGit.Branches;
using FlickGit.Cli;
using FlickGit.Commits;
using FlickGit.Diff;
using FlickGit.Files;
using FlickGit.Merges;
using FlickGit.Models;
using FlickGit.Remotes;
using FlickGit.Stashes;
using FlickGit.Status;
using FlickGit.Tags;

namespace FlickGit.App.CommandLine;

/// <summary>
/// The verbs that answer with text about a repository: status, switch, tag, push, and the two that
/// act on one file.
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
    StashService stashes,
    TrackingService files,
    UpstreamConsent consent)
{
    /// <summary>`flick status` — the file list as text.</summary>
    public async Task<VerbResult> StatusAsync(VerbOutput output, RepositoryInfo repository)
    {
        RepositoryStatus state = await status
            .GetStatusAsync(repository, CancellationToken.None)
            .ConfigureAwait(true);

        output.Line($"{repository.Name}  ({repository.Root})");

        output.Line(
            state.IsDetachedHead ? Strings.Get("status.detached")
            : state.Branch is null ? Strings.Get("status.nobranch")
            : $"{state.Branch}{(state.Upstream is null ? "  " + Strings.Get("status.noupstream") : $"  ↑{state.Ahead} ↓{state.Behind}")}");

        //An operation in progress goes above the file list, because it changes what every letter
        //below it means: a U row is not something to commit, it is something to resolve. Said here
        //as well as in the window so a terminal is not the one surface that has to infer it from
        //`git status` -- and it costs nothing, having arrived with the status.
        if (state.Merge.InProgress)
        {
            string name = Strings.Get(state.Merge.Operation switch
            {
                MergeOperation.Merge => "conflict.name.merge",
                MergeOperation.Rebase => "conflict.name.rebase",
                MergeOperation.CherryPick => "conflict.name.cherrypick",
                _ => "conflict.name.revert",
            });

            string sentence = Strings.Get("conflict.inprogress", name);

            output.Line(state.Merge.HasProgress
                ? Strings.Get("conflict.progress", sentence, state.Merge.Step!.Value, state.Merge.Total!.Value)
                : sentence);
        }

        if (state.Files.Count == 0)
        {
            output.Line(Strings.Get("status.clean"));
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
    /// `flick tag &lt;path&gt; &lt;name&gt;` — creates that tag on HEAD and publishes it.
    ///
    /// Create and publish, because that is what creating a tag means in the window as well, and one
    /// verb with two meanings depending on the surface is worse than either. Deletion stays in the
    /// window: there is no `--force` under this and so nothing here can overwrite anything, whereas
    /// deleting a published tag is exactly the "explicit user intent, expressed in the moment" that
    /// a script flag is not.
    /// </summary>
    public async Task<VerbResult> TagAsync(VerbOutput output, RepositoryInfo repository, string name)
    {
        string title = Strings.Get("tag.create");
        string tag = name.Trim();

        //Lightweight: a message would have to come from somewhere, and a second positional argument
        //that is sometimes a message is a grammar nobody can remember. `git tag -a` is right there
        //for anyone who wants one from a script.
        TagOutcome created = await tags
            .CreateAsync(repository, tag, null, null, CancellationToken.None)
            .ConfigureAwait(true);

        if (!created.Succeeded)
        {
            output.Fail(title, created.GitError ?? string.Empty);
            return VerbResult.Exit(ExitCodes.GitError);
        }

        //Null when there is no remote, or several and none called origin. Publishing to a guess is
        //publishing a version number somewhere other people read, so it stays local and says so.
        if (await tags.ResolveRemoteAsync(repository, CancellationToken.None).ConfigureAwait(true) is not { } remote)
        {
            output.Say(title, Strings.Get("tag.created", tag));
            return VerbResult.Exit(ExitCodes.Success);
        }

        TagOutcome published = await tags
            .PushAsync(repository, tag, remote, CancellationToken.None)
            .ConfigureAwait(true);

        if (published.Succeeded)
        {
            output.Say(title, Strings.Get("tag.created.pushed", tag, remote));
            return VerbResult.Exit(ExitCodes.Success);
        }

        //The tag exists here and not there, which the exit code alone cannot say -- so the message
        //says both halves before Git's own words.
        output.Fail(
            title,
            $"{Strings.Get("tag.push.failed", tag, remote)}\n\n{published.GitError}");

        return VerbResult.Exit(ExitCodes.GitError);
    }

    /// <summary>
    /// `flick stash &lt;path&gt; &lt;message&gt;` — puts the working tree away under that message.
    ///
    /// The tag verb's grammar, and the same division of labour: creating cannot overwrite anything,
    /// so a script may do it, while the two operations that name an *existing* stash stay in the
    /// window. That is a sharper line here than it is for tags — a reflog selector is a position, and
    /// a position written into a script is one that will have moved by the time the script runs.
    ///
    /// Untracked files are included, matching the window's ticked-by-default box: a command called
    /// "stash" that left a new file sitting in the working tree would have done half the job.
    /// </summary>
    public async Task<VerbResult> StashAsync(VerbOutput output, RepositoryInfo repository, string message)
    {
        string title = Strings.Get("stash.push");

        StashOutcome outcome = await stashes
            .PushAsync(repository, message, includeUntracked: true, CancellationToken.None)
            .ConfigureAwait(true);

        if (outcome.Refusal == StashRefusal.NothingToStash)
        {
            //Success, on the precedent <see cref="SwitchAsync"/> sets for naming the branch you are
            //already on: the caller asked for the working tree to be put away and the working tree has
            //nothing outstanding, so the requested state is the state. 5 would be wrong -- CLAUDE.md
            //spends that code on "refused for safety", and nothing here was refused.
            output.Say(title, Strings.Get("stash.nothing"));
            return VerbResult.Exit(ExitCodes.Success);
        }

        if (!outcome.Succeeded)
        {
            output.Fail(title, outcome.GitError ?? string.Empty);
            return VerbResult.Exit(ExitCodes.GitError);
        }

        output.Say(title, Strings.Get("stash.pushed"));
        return VerbResult.Exit(ExitCodes.Success);
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

            case PushAction.SetUpstream:
                if (!await ConsentToUpstreamAsync(repository, plan).ConfigureAwait(true))
                    return VerbResult.Exit(ExitCodes.UserCancelled);

                break;
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
    /// `flick add &lt;path&gt;...` — the Explorer menu's Add on whatever was selected, and the CLI
    /// spelling of it.
    ///
    /// <b>It asks nothing, on a file or on a folder.</b> Staging discards nothing: the working tree is
    /// untouched, and unticking the row in the commit window takes it back out of the index again.
    /// There is no state a question could protect, so it costs a click and buys nothing — and the
    /// folder count it used to carry was two Git reads on the way to a command that was going to run
    /// anyway.
    ///
    /// <b>It is also the way back from a removal.</b> Untracking leaves the file on disk, so adding
    /// that path again puts the content straight back into the index and the staged deletion with it.
    ///
    /// <b>Either every path is acted on or none is.</b> The selection is one gesture, so a member that
    /// will not resolve refuses the batch rather than being skipped past — see <see cref="TargetsIn"/>.
    /// </summary>
    public async Task<VerbResult> AddAsync(
        VerbOutput output,
        RepositoryInfo repository,
        IReadOnlyList<string> paths)
    {
        string title = Strings.Get("action.add");

        if (TargetsIn(output, title, repository, paths) is not { } targets)
            return VerbResult.Exit(ExitCodes.NotARepository);

        TrackingResult result = await files
            .AddAsync(repository, [.. targets.Select(t => t.Relative)], CancellationToken.None)
            .ConfigureAwait(true);

        if (!result.Succeeded)
        {
            output.Fail(title, result.Error ?? string.Empty);
            return VerbResult.Exit(ExitCodes.GitError);
        }

        //The repository, not the verb, because with no console this is a notification and the
        //commit toast is titled the same way: five repositories are open and "Add" names none of
        //them. The failures above keep the verb, which is what an error window has to lead with.
        output.Say(
            repository.Name,
            targets.Count == 1
                ? Strings.Get("file.added", targets[0].Relative)
                : Strings.Get("selection.added", targets.Count));

        return VerbResult.Exit(ExitCodes.Success);
    }

    /// <summary>
    /// `flick rm &lt;path&gt;...` — the menu's Remove on whatever was selected: <b>out of Git, still on
    /// disk</b>.
    ///
    /// <c>git rm --cached</c> and nothing else. What the user asked for is "stop tracking this, keep my
    /// file", so the working tree is never touched: each path becomes a staged deletion, ready to
    /// commit, and the file comes back in the list as untracked. On a folder it is the same command with
    /// <c>-r</c>, which is why a folder no longer goes anywhere near the Recycle Bin.
    ///
    /// <b>It asks nothing, and that is a consequence rather than a decision.</b> A confirmation exists
    /// to protect state that cannot be recovered; there is none here, because nothing is deleted and
    /// <c>flick add</c> on the same path puts the index back. The gate, the counts and the one question
    /// went with the destructive step they were guarding.
    ///
    /// <b>A path Git has nothing under is reported, not removed.</b> Explorer's own Delete is what
    /// removes an untracked file, and <c>git rm</c> is all-or-nothing over its pathspecs — so leaving
    /// such a path in the batch would refuse the removal of everything selected beside it. It is
    /// counted out first and named in the notification instead.
    /// </summary>
    public async Task<VerbResult> RemoveAsync(
        VerbOutput output,
        RepositoryInfo repository,
        IReadOnlyList<string> paths)
    {
        string title = Strings.Get("action.rm");

        if (TargetsIn(output, title, repository, paths) is not { } targets)
            return VerbResult.Exit(ExitCodes.NotARepository);

        //Git's own answer to "is there anything here to remove", per target. One read each rather than
        //one for the batch, because the answer is per path: what is skipped has to be nameable, and
        //what is left is what the one command may carry.
        var tracked = new List<TargetPath>(targets.Count);
        var untracked = new List<TargetPath>();

        foreach (TargetPath target in targets)
        {
            int here = await files
                .TrackedCountAsync(repository, target.Relative, CancellationToken.None)
                .ConfigureAwait(true);

            (here > 0 ? tracked : untracked).Add(target);
        }

        if (tracked.Count == 0)
        {
            //Nothing ran and nothing failed: the user pointed at files Git has never seen, which is a
            //no-op with something to say rather than an error. Exit 0, so a script removing several
            //paths is not stopped by the one already outside Git.
            output.Say(repository.Name, NotTracked(untracked));
            return VerbResult.Exit(ExitCodes.Success);
        }

        TrackingResult result = await files
            .UntrackAsync(repository, [.. tracked.Select(t => t.Relative)], CancellationToken.None)
            .ConfigureAwait(true);

        if (!result.Succeeded)
        {
            output.Fail(title, result.Error ?? string.Empty);
            return VerbResult.Exit(ExitCodes.GitError);
        }

        //The repository, not the verb: see AddAsync. The skipped paths ride along in the same
        //notification, because two notifications for one gesture is the thing this must not do.
        output.Say(repository.Name, Removed(tracked) + Skipped(untracked));
        return VerbResult.Exit(ExitCodes.Success);
    }

    /// <summary>What the removal did, in the words the batch earns.</summary>
    private static string Removed(IReadOnlyList<TargetPath> tracked) =>
        tracked.Count == 1
            ? Strings.Get(tracked[0].IsFolder ? "folder.removed" : "file.removed", tracked[0].Relative)
            : Strings.Get("selection.removed", tracked.Count);

    /// <summary>
    /// The paths Git has nothing under, when they are the whole batch — so the message stands alone.
    /// </summary>
    private static string NotTracked(IReadOnlyList<TargetPath> untracked) =>
        untracked.Count == 1
            ? Strings.Get(untracked[0].IsFolder ? "folder.untracked" : "file.untracked", untracked[0].Relative)
            : Strings.Get("selection.untracked", untracked.Count);

    /// <summary>
    /// The same, as a sentence after a removal that did happen. Empty when there were none.
    ///
    /// Two spellings rather than a count in one, for the reason <see cref="Removed"/> keeps the
    /// singular wording: "1 were left alone" is the sentence a plural-only string produces in the
    /// commonest case of all.
    /// </summary>
    private static string Skipped(IReadOnlyList<TargetPath> untracked) =>
        untracked.Count switch
        {
            0 => string.Empty,
            1 => ". " + Strings.Get("selection.remove.skipped.one"),
            _ => ". " + Strings.Get("selection.remove.skipped", untracked.Count),
        };

    /// <summary>
    /// Every selected path as Git speaks it — or null after saying why none of them will be acted on.
    ///
    /// <b>One refusal refuses the whole batch.</b> A selection is a single gesture, so acting on the
    /// members that happened to resolve would be acting on a list the user never chose. It is also what
    /// settles a selection spanning two repositories without a case of its own: the repository was
    /// resolved from the first path, so every member of another one fails
    /// <c>ResolveInsideRepository</c> and the whole thing is refused by name.
    /// </summary>
    private static IReadOnlyList<TargetPath>? TargetsIn(
        VerbOutput output,
        string title,
        RepositoryInfo repository,
        IReadOnlyList<string> paths)
    {
        var targets = new List<TargetPath>(paths.Count);

        foreach (string path in paths)
        {
            if (PathIn(output, title, repository, path) is not { } target)
                return null;

            //Explorer cannot hand over the same item twice, but a command line can, and `git rm` given
            //one path twice removes it once and then fails on the second pathspec.
            if (!targets.Any(t => string.Equals(t.Relative, target.Relative, StringComparison.Ordinal)))
                targets.Add(target);
        }

        return targets.Count == 0 ? null : targets;
    }


    /// <param name="Relative">The repository-relative, forward-slashed path Git speaks.</param>
    /// <param name="IsFolder">
    /// Everything below it is in scope, so it is counted and confirmed before anything runs.
    /// </param>
    private sealed record TargetPath(string Relative, bool IsFolder);

    /// <summary>
    /// The clicked path, as Git speaks it — or null after saying why Add and Remove will not act
    /// on it.
    ///
    /// Two refusals, and the first is the one a terminal reaches: <c>flick add</c> with no path
    /// defaults to the working directory, so <c>flick add</c> typed at a repository root is one
    /// keystroke away from staging the whole thing. <b>The root is refused by name</b> rather than
    /// left to the second refusal, which would also fire — <c>Path.GetRelativePath</c> makes it
    /// <c>"."</c>, and nothing resolves that inside the repository — but would say the wrong thing
    /// about it. The menu cannot produce this click at all, because <c>ActionSurfaces.Folder</c> is
    /// not drawn on a root; the surface for a whole repository is Commit, which is what the message
    /// says. Compared as resolved full paths, because <c>repo\.</c> and <c>repo</c> are one folder
    /// and only one of them looks like it.
    ///
    /// The second is <c>WorkingTreeWriter</c>'s own guard rather than a second answer to the same
    /// question: a path that resolves outside the root is either a bug or an attack, and this is not
    /// the layer that guesses which.
    ///
    /// A directory is no longer one of them. It sets <c>IsFolder</c>, and the caller does the
    /// counting and the asking that earns it.
    /// </summary>
    private static TargetPath? PathIn(
        VerbOutput output,
        string title,
        RepositoryInfo repository,
        string path)
    {
        string full = Path.GetFullPath(path);

        if (string.Equals(
                full.TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(repository.Root).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            output.Fail(title, Strings.Get("folder.isroot", full));
            return null;
        }

        string relative = Path.GetRelativePath(repository.Root, full).Replace('\\', '/');

        if (WorkingTreeWriter.ResolveInsideRepository(repository.Root, relative) is null)
        {
            output.Fail(title, Strings.Get("file.outside", full, repository.Root));
            return null;
        }

        return new TargetPath(relative, Directory.Exists(full));
    }

    /// <summary>
    /// Asks once per repository whether an upstream may be created, and remembers the answer.
    ///
    /// <b>Through <see cref="UpstreamConsent"/>, which is the only thing that reads and writes that
    /// answer.</b> This used to be its own copy of the same three steps -- read the key, ask, write it
    /// back -- which is exactly the second place for "once" to stop meaning once that the service's
    /// own doc comment warns about: a user who declined here and later pressed Commit would have been
    /// asked again about a repository they had already answered for.
    ///
    /// A dialog even from the command line, and unowned: this is consent to publish a branch to a
    /// remote other people read, and there is no terminal to prompt on.
    /// </summary>
    private Task<bool> ConsentToUpstreamAsync(RepositoryInfo repository, PushPlan plan) =>
        consent.AnswerAsync(
            repository,
            new CommitFlowQuestion(
                CommitFlowQuestionKind.CreateUpstream,
                plan.Branch,
                plan.Upstream,
                plan.Remote),
            (title, body, yes, no) => Task.FromResult(ConfirmWindow.Ask(null, title, body, yes, no)));
}
