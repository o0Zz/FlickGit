using System.IO;
using FlickGit.App.Infrastructure;
using FlickGit.App.Localization;
using FlickGit.App.Views;
using FlickGit.Branches;
using FlickGit.Cli;
using FlickGit.Commits;
using FlickGit.Diff;
using FlickGit.Files;
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
    FolderRemovalFlow folderRemovals,
    WorkingTreeDeleter deleter,
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
    /// `flick add &lt;path&gt;` — the Explorer menu's Add on a file or on a folder, and the CLI
    /// spelling of both.
    ///
    /// <b>A file asks nothing</b>: staging discards nothing, and unticking the row in the commit
    /// window is how it is taken back out again.
    ///
    /// <b>A folder asks, and the question carries the count.</b> Not because staging became
    /// dangerous — it did not — but because one click on a directory can stage several hundred files
    /// and the number is the only part of that the user cannot see. Counting first is also what lets
    /// a folder with nothing to stage say so instead of running a command that would do nothing.
    /// </summary>
    public async Task<VerbResult> AddAsync(VerbOutput output, RepositoryInfo repository, string path)
    {
        string title = Strings.Get("action.add");

        if (PathIn(output, title, repository, path) is not { } target)
            return VerbResult.Exit(ExitCodes.NotARepository);

        if (target.IsFolder && await ConfirmFolderAddAsync(output, repository, target.Relative) is { } refused)
            return refused;

        TrackingResult result = await files
            .AddAsync(repository, target.Relative, CancellationToken.None)
            .ConfigureAwait(true);

        if (!result.Succeeded)
        {
            output.Fail(title, result.Error ?? string.Empty);
            return VerbResult.Exit(ExitCodes.GitError);
        }

        output.Say(title, Strings.Get("file.added", target.Relative));
        return VerbResult.Exit(ExitCodes.Success);
    }

    /// <summary>
    /// The folder add's two reads and its question — or the result to return instead of adding.
    ///
    /// Two reads rather than one, because the two sets are disjoint and so neither needs
    /// de-duplicating: <c>ls-files --others</c> is what Git has never seen, <c>diff --name-only</c>
    /// is what it has and what has changed since. Their sum is what <c>git add</c> would touch.
    /// </summary>
    private async Task<VerbResult?> ConfirmFolderAddAsync(
        VerbOutput output,
        RepositoryInfo repository,
        string relative)
    {
        int untracked = await files
            .UntrackedCountAsync(repository, relative, CancellationToken.None)
            .ConfigureAwait(true);

        int changed = await files
            .ChangedCountAsync(repository, relative, CancellationToken.None)
            .ConfigureAwait(true);

        int total = untracked + changed;

        if (total == 0)
        {
            //Success, not a failure: the folder is simply already staged or already clean. Exit 0 so
            //a script that adds several folders in a row is not stopped by the quiet one.
            output.Say(Strings.Get("action.add"), Strings.Get("folder.nothingtoadd", relative));
            return VerbResult.Exit(ExitCodes.Success);
        }

        if (!ConfirmWindow.Ask(
                null,
                Strings.Get("folder.add.title"),
                Strings.Get("folder.add.ask", relative, total, untracked),
                Strings.Get("folder.add.yes"),
                Strings.Get("common.cancel")))
        {
            return VerbResult.Exit(ExitCodes.UserCancelled);
        }

        return null;
    }

    /// <summary>
    /// `flick rm &lt;path&gt;` — the menu's Remove on a file or on a folder: gone from the working
    /// tree, and the deletion staged, not committed.
    ///
    /// <b>It asks first, on every surface, and a dialog even from the command line</b> — the same
    /// rule and the same reason as <see cref="ConsentToUpstreamAsync"/>: CLAUDE.md's Safety Rules
    /// want explicit intent expressed in the moment, and the fast surfaces are not shortcuts around
    /// them. Nothing is forced anywhere, so Git still refuses content that differs from both HEAD
    /// and the index, and the confirmation says what remains recoverable rather than promising more
    /// than that.
    ///
    /// A path Git is not tracking is refused <i>before</i> the question, because a question about an
    /// operation that cannot happen is worse than the refusal it precedes. That is the same count
    /// the folder question then goes on to state, which is why it is read rather than asked as a
    /// yes/no.
    ///
    /// The two halves part company after that: a file is <c>git rm</c>, which does the deleting
    /// itself, and a folder is <see cref="RemoveFolderAsync"/>, which cannot be.
    /// </summary>
    public async Task<VerbResult> RemoveAsync(VerbOutput output, RepositoryInfo repository, string path)
    {
        string title = Strings.Get("action.rm");

        if (PathIn(output, title, repository, path) is not { } target)
            return VerbResult.Exit(ExitCodes.NotARepository);

        if (target.IsFolder)
            return await RemoveFolderAsync(output, title, repository, target.Relative).ConfigureAwait(true);

        if (await files.TrackedCountAsync(repository, target.Relative, CancellationToken.None)
                .ConfigureAwait(true) == 0)
        {
            //Git's own answer here is `fatal: pathspec … did not match any files`, which is accurate
            //about a question the user did not ask. The exit code is still Git's, so a script branches
            //on the same number either way.
            output.Fail(title, Strings.Get("file.untracked", target.Relative));
            return VerbResult.Exit(ExitCodes.GitError);
        }

        if (!ConfirmWindow.Ask(
                null,
                Strings.Get("file.remove.title"),
                Strings.Get("file.remove.ask", target.Relative),
                Strings.Get("file.remove.yes"),
                Strings.Get("common.cancel")))
        {
            return VerbResult.Exit(ExitCodes.UserCancelled);
        }

        TrackingResult result = await files
            .RemoveAsync(repository, target.Relative, CancellationToken.None)
            .ConfigureAwait(true);

        if (!result.Succeeded)
        {
            output.Fail(title, result.Error ?? string.Empty);
            return VerbResult.Exit(ExitCodes.GitError);
        }

        output.Say(title, Strings.Get("file.removed", target.Relative));
        return VerbResult.Exit(ExitCodes.Success);
    }

    /// <summary>
    /// The folder half of Remove: <see cref="FolderRemovalFlow"/>, and the words for each way it can
    /// end.
    ///
    /// <b>The sequence itself is in Core</b> — gate, ask, bin, record — because the order is the
    /// safety rule and getting it wrong is a silent failure, which is the kind of bug only a test can
    /// find. What is left here is the half that cannot be: a window to ask in, and a Recycle Bin that
    /// <c>net9.0</c> cannot reach.
    ///
    /// The outcomes divide by what is true afterwards, which is what the messages have to say. Every
    /// one before <c>BinFailed</c> left the working tree and the index exactly as they were.
    /// <c>RecordFailed</c> is the only one that did not, and its message names the Recycle Bin,
    /// because that sentence is the way back.
    /// </summary>
    private async Task<VerbResult> RemoveFolderAsync(
        VerbOutput output,
        string title,
        RepositoryInfo repository,
        string relative)
    {
        FolderRemoval removal = await folderRemovals.RunAsync(
            repository,
            relative,
            plan => Task.FromResult(ConfirmWindow.Ask(
                null,
                Strings.Get("folder.remove.title"),
                Strings.Get("folder.remove.ask", relative, plan.TrackedFiles, plan.UntrackedFiles),
                Strings.Get("folder.remove.yes"),
                Strings.Get("common.cancel"))),
            () => Task.FromResult(Binned(deleter.DeleteFolder(repository.Root, relative))),
            CancellationToken.None).ConfigureAwait(true);

        switch (removal.Outcome)
        {
            case FolderRemovalOutcome.Removed:
                output.Say(title, Strings.Get("folder.removed", relative, removal.TrackedFiles));
                return VerbResult.Exit(ExitCodes.Success);

            case FolderRemovalOutcome.NotTracked:
                output.Fail(title, Strings.Get("folder.untracked", relative));
                return VerbResult.Exit(ExitCodes.GitError);

            case FolderRemovalOutcome.Declined:
                return VerbResult.Exit(ExitCodes.UserCancelled);

            case FolderRemovalOutcome.RecordFailed:
                output.Fail(
                    title,
                    (removal.Error ?? string.Empty) + "\n\n" + Strings.Get("folder.remove.binned", relative));

                return VerbResult.Exit(ExitCodes.GitError);

            default:
                //Refused, with Git naming the files that hold uncommitted work -- or BinFailed, whose
                //message may be null because the shell has already put up its own and paraphrasing it
                //would be worse than saying nothing. See DeleteOutcome.
                if (removal.Error is { Length: > 0 } why)
                    output.Fail(title, why);

                return VerbResult.Exit(ExitCodes.GitError);
        }
    }

    /// <summary>
    /// The App's <see cref="DeleteOutcome"/> as the Core flow's <see cref="TrackingResult"/> — the
    /// same two fields, and the null message means the same thing in both.
    /// </summary>
    private static TrackingResult Binned(DeleteOutcome outcome) =>
        outcome.Succeeded ? TrackingResult.Ok : new TrackingResult(false, outcome.Message);

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
