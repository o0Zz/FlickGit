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
    RemovalFlow removals,
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
    /// <b>Files ask nothing</b>: staging discards nothing, and unticking the row in the commit window
    /// is how it is taken back out again. Explorer also has those rows highlighted, so the size of what
    /// is about to happen is on screen already.
    ///
    /// <b>A folder in the selection asks, and the question carries the count.</b> Not because staging
    /// became dangerous — it did not — but because one click on a directory can stage several hundred
    /// files and the number is the only part of that the user cannot see. Counting first is also what
    /// lets a folder with nothing to stage say so instead of running a command that would do nothing.
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

        if (await ConfirmAddAsync(output, repository, targets).ConfigureAwait(true) is { } refused)
            return refused;

        TrackingResult result = await files
            .AddAsync(repository, [.. targets.Select(t => t.Relative)], CancellationToken.None)
            .ConfigureAwait(true);

        if (!result.Succeeded)
        {
            output.Fail(title, result.Error ?? string.Empty);
            return VerbResult.Exit(ExitCodes.GitError);
        }

        output.Say(
            title,
            targets.Count == 1
                ? Strings.Get("file.added", targets[0].Relative)
                : Strings.Get("selection.added", targets.Count));

        return VerbResult.Exit(ExitCodes.Success);
    }

    /// <summary>
    /// The folder reads and the one question — or the result to return instead of adding.
    ///
    /// <b>Asked only when the selection holds a folder</b>, which is the case the question was written
    /// for. Two reads per folder rather than one, because the two sets are disjoint and so neither
    /// needs de-duplicating: <c>ls-files --others</c> is what Git has never seen and
    /// <c>diff --name-only</c> is what it has and what has changed since. Their sum, plus the files
    /// that were selected outright, is what <c>git add</c> would touch.
    ///
    /// <b>"Nothing to stage" only ends the verb when there is nothing else in the batch.</b> A folder
    /// with nothing under it sitting beside two selected files must not stop those files being staged
    /// — which is what returning the early success here would do.
    /// </summary>
    private async Task<VerbResult?> ConfirmAddAsync(
        VerbOutput output,
        RepositoryInfo repository,
        IReadOnlyList<TargetPath> targets)
    {
        TargetPath[] folders = [.. targets.Where(t => t.IsFolder)];

        if (folders.Length == 0)
            return null;

        int untracked = 0;
        int changed = 0;

        foreach (TargetPath folder in folders)
        {
            untracked += await files
                .UntrackedCountAsync(repository, folder.Relative, CancellationToken.None)
                .ConfigureAwait(true);

            changed += await files
                .ChangedCountAsync(repository, folder.Relative, CancellationToken.None)
                .ConfigureAwait(true);
        }

        int selectedFiles = targets.Count - folders.Length;
        int total = untracked + changed + selectedFiles;

        if (total == 0)
        {
            //Success, not a failure: the folder is simply already staged or already clean. Exit 0 so
            //a script that adds several folders in a row is not stopped by the quiet one.
            output.Say(
                Strings.Get("action.add"),
                folders.Length == 1
                    ? Strings.Get("folder.nothingtoadd", folders[0].Relative)
                    : Strings.Get("selection.nothingtoadd", targets.Count));

            return VerbResult.Exit(ExitCodes.Success);
        }

        bool oneFolderOnly = targets.Count == 1;

        if (!ConfirmWindow.Ask(
                null,
                Strings.Get(oneFolderOnly ? "folder.add.title" : "selection.add.title"),
                oneFolderOnly
                    ? Strings.Get("folder.add.ask", folders[0].Relative, total, untracked)
                    : Strings.Get("selection.add.ask", targets.Count, total, untracked),
                Strings.Get(oneFolderOnly ? "folder.add.yes" : "selection.add.yes"),
                Strings.Get("common.cancel")))
        {
            return VerbResult.Exit(ExitCodes.UserCancelled);
        }

        return null;
    }

    /// <summary>
    /// `flick rm &lt;path&gt;...` — the menu's Remove on whatever was selected: gone from the working
    /// tree, and the deletions staged, not committed.
    ///
    /// <b>It asks first, on every surface, and a dialog even from the command line</b> — the same rule
    /// and the same reason as <see cref="ConsentToUpstreamAsync"/>: CLAUDE.md's Safety Rules want
    /// explicit intent expressed in the moment, and the fast surfaces are not shortcuts around them.
    /// Nothing is forced anywhere, so Git still refuses content that differs from both HEAD and the
    /// index, and the confirmation says what remains recoverable rather than promising more than that.
    ///
    /// <b>One question for the whole selection, asked after every path has been gated.</b> That order
    /// is <see cref="RemovalFlow"/>'s, and it is the reason the sequence lives in Core with tests: a
    /// question per item would run the gate for the fifth only after the first four had already gone.
    ///
    /// What is left here is the half Core cannot do — a window to ask in, and a Recycle Bin that
    /// <c>net9.0</c> cannot reach — plus the words for each way it can end.
    /// </summary>
    public async Task<VerbResult> RemoveAsync(
        VerbOutput output,
        RepositoryInfo repository,
        IReadOnlyList<string> paths)
    {
        string title = Strings.Get("action.rm");

        if (TargetsIn(output, title, repository, paths) is not { } targets)
            return VerbResult.Exit(ExitCodes.NotARepository);

        RemovalTarget[] batch = [.. targets.Select(t => new RemovalTarget(t.Relative, t.IsFolder))];

        Removal removal = await removals.RunAsync(
            repository,
            batch,
            plan => Task.FromResult(Ask(batch, plan)),

            //Folders only: `RemovalFlow` removes the files with `git rm`, which deletes the
            //working-tree copy itself, and calls this for nothing else. See its step 6.
            target => Task.FromResult(Binned(deleter.DeleteFolder(repository.Root, target.Relative))),
            CancellationToken.None).ConfigureAwait(true);

        return Report(output, title, batch, removal);
    }

    /// <summary>
    /// The one destructive question, in the words the batch earns.
    ///
    /// A selection of one keeps today's wording exactly, because it is the same operation and a
    /// message reading "1 items" would be a regression in the common case. Past one, the counts are
    /// what the sentence has to carry — the paths would be a list nobody reads in a dialog.
    /// </summary>
    private static bool Ask(IReadOnlyList<RemovalTarget> batch, RemovalPlan plan)
    {
        bool one = batch.Count == 1;
        bool file = one && !batch[0].IsFolder;

        string question = file
            ? Strings.Get("file.remove.ask", batch[0].Relative)
            : one
                ? Strings.Get("folder.remove.ask", batch[0].Relative, plan.TrackedFiles, plan.UntrackedFiles)
                : Strings.Get(
                    "selection.remove.ask",
                    batch.Count,
                    plan.Folders,
                    plan.TrackedFiles,
                    plan.UntrackedFiles);

        return ConfirmWindow.Ask(
            null,
            Strings.Get(file ? "file.remove.title" : one ? "folder.remove.title" : "selection.remove.title"),
            question,
            Strings.Get(file ? "file.remove.yes" : one ? "folder.remove.yes" : "selection.remove.yes"),
            Strings.Get("common.cancel"),
            destructive: true);
    }

    /// <summary>
    /// What is true after the removal, said in the words of the outcome that ended it.
    ///
    /// The outcomes divide by what is true afterwards, which is what the messages have to say. Every
    /// one before <c>BinFailed</c> left the working tree and the index exactly as they were.
    /// <c>BinFailed</c> and <c>RecordFailed</c> are the two that can stop part-way through a selection,
    /// which is why they are the two that add <c>selection.remove.partial</c>; and
    /// <c>RecordFailed</c>'s message names the Recycle Bin, because that sentence is the way back.
    /// </summary>
    private static VerbResult Report(
        VerbOutput output,
        string title,
        IReadOnlyList<RemovalTarget> batch,
        Removal removal)
    {
        bool one = batch.Count == 1;

        switch (removal.Outcome)
        {
            case RemovalOutcome.Removed:
                output.Say(
                    title,
                    one
                        ? Strings.Get(
                            batch[0].IsFolder ? "folder.removed" : "file.removed",
                            batch[0].Relative,
                            removal.TrackedFiles)
                        : Strings.Get("selection.removed", batch.Count, removal.TrackedFiles));

                return VerbResult.Exit(ExitCodes.Success);

            case RemovalOutcome.NotTracked:
                //Git's own answer here is `fatal: pathspec ... did not match any files`, which is
                //accurate about a question the user did not ask. The path named is the one target that
                //had nothing under it, whatever the size of the batch.
                output.Fail(
                    title,
                    Strings.Get(
                        IsFolder(batch, removal.Path) ? "folder.untracked" : "file.untracked",
                        removal.Path ?? string.Empty));

                return VerbResult.Exit(ExitCodes.GitError);

            case RemovalOutcome.Declined:
                return VerbResult.Exit(ExitCodes.UserCancelled);

            case RemovalOutcome.RecordFailed:
                output.Fail(
                    title,
                    Join(
                        removal.Error,
                        Strings.Get("folder.remove.binned", removal.Path ?? string.Empty),
                        Partial(removal)));

                return VerbResult.Exit(ExitCodes.GitError);

            default:
                //Refused, with Git naming the files that hold uncommitted work -- or BinFailed, whose
                //message may be null because the shell has already put up its own and paraphrasing it
                //would be worse than saying nothing. See DeleteOutcome.
                if (Join(removal.Error, Partial(removal)) is { Length: > 0 } why)
                    output.Fail(title, why);

                return VerbResult.Exit(ExitCodes.GitError);
        }
    }

    /// <summary>How much of the selection went before the failure, when any of it did.</summary>
    private static string? Partial(Removal removal) =>
        removal.Done > 0 ? Strings.Get("selection.remove.partial", removal.Done) : null;

    /// <summary>Whether the target a path names is a folder, for choosing between two messages.</summary>
    private static bool IsFolder(IReadOnlyList<RemovalTarget> batch, string? path) =>
        path is not null
        && batch.Any(t => t.IsFolder && string.Equals(t.Relative, path, StringComparison.Ordinal));

    /// <summary>The non-empty parts, one per paragraph.</summary>
    private static string Join(params string?[] parts) =>
        string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>
    /// The App's <see cref="DeleteOutcome"/> as the Core flow's <see cref="TrackingResult"/> — the
    /// same two fields, and the null message means the same thing in both.
    /// </summary>
    private static TrackingResult Binned(DeleteOutcome outcome) =>
        outcome.Succeeded ? TrackingResult.Ok : new TrackingResult(false, outcome.Message);

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
