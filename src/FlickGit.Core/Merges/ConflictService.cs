using FlickGit.Git;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;
using static FlickGit.Git.GitPathspec;

namespace FlickGit.Merges;

/// <param name="Succeeded">False leaves the index and the working tree exactly as they were.</param>
/// <param name="Error">Git's own words, or ours when the refusal happened before Git ran.</param>
/// <param name="Blocked">
/// The gate refused and <b>no Git command ran at all</b> — so <see cref="Error"/> is the list of paths
/// still unmerged rather than anything Git said.
///
/// A separate flag rather than something to sniff out of the text, because the two failures want
/// different sentences above them: one is "resolve these first", the other is Git objecting to a
/// command it did run, and a surface guessing between them by parsing our own message would be
/// parsing human-readable output one layer up from where CLAUDE.md forbids it.
/// </param>
public sealed record ConflictResult(bool Succeeded, string? Error, bool Blocked = false)
{
    public static readonly ConflictResult Ok = new(true, null);

    public static ConflictResult Failed(string error) => new(false, error);

    /// <summary>Refused before Git ran, naming what is in the way.</summary>
    public static ConflictResult Refused(string paths) => new(false, paths, Blocked: true);
}

/// <summary>
/// The way out of a conflict: resolve a path, then continue — or abandon the whole operation.
///
/// <b>Every rule here is negative, and each one is a way this could destroy work:</b>
///
/// <list type="bullet">
/// <item><description><b>Continue is gated on a fresh read.</b> <see cref="ContinueAsync"/> asks Git
/// itself whether an unmerged path is left, and runs nothing at all when one is. It does not trust
/// the window's status, which was read before the user started clicking — and
/// <c>rebase --continue</c> over a half-resolved tree is how conflict markers reach a
/// commit.</description></item>
/// <item><description><b>The checkout comes before the add, always.</b> Reversed, the add would
/// record the file with its markers in it as the resolution, and the checkout would then quietly
/// overwrite the working tree under an index already saying "resolved".</description></item>
/// <item><description><b>Nothing here is forced.</b> No <c>--force</c> and no <c>-f</c> on any
/// argument vector this class can issue — which is what a test asserts rather than a reviewer. It is
/// also why a delete/modify conflict is not fully served: recording "take the deletion" needs
/// <c>git rm --force</c> on an unmerged path, so the surface offers what it can and names the command
/// for the rest.</description></item>
/// <item><description><b>Abort is only ever reached by being called.</b> CLAUDE.md: "do not
/// automatically abort a rebase". <see cref="ContinueAsync"/> has no failure path falling through to
/// <see cref="AbortAsync"/>, and a test says so.</description></item>
/// </list>
/// </summary>
public sealed class ConflictService(IGitProcessRunner git, RepositoryService repositories, ILog log)
{
    /// <summary>
    /// Resolves one path by taking a whole side of it, then records the resolution.
    ///
    /// <b>Two commands, and the order is the safety rule</b> — see the class summary. They are not
    /// one call for the same reason: <c>git checkout --ours -- &lt;path&gt;</c> writes the working
    /// tree and leaves the path unmerged, and only the <c>add</c> that follows tells Git the conflict
    /// is over.
    ///
    /// <b>The side that does not exist is refused before this is reached, not by Git.</b> A "deleted
    /// by us" conflict has no stage 2, and <c>checkout --ours</c> answers
    /// <c>error: path 'x' does not have our version</c> — accurate, and meaningless beside a button
    /// the user just pressed. The surface offers only the sides
    /// <see cref="GitFileChange.HasOurSide"/> and <see cref="GitFileChange.HasTheirSide"/> report, and
    /// this is where it would show up if it stopped doing so.
    /// </summary>
    public async Task<ConflictResult> TakeSideAsync(
        RepositoryInfo repository,
        string path,
        ConflictSide side,
        CancellationToken cancellationToken)
    {
        string flag = side == ConflictSide.Ours ? "--ours" : "--theirs";

        GitResult checkout = await git.RunAsync(
            repository.Root,
            ["checkout", flag, "--", Literal(path)],
            cancellationToken).ConfigureAwait(false);

        if (!checkout.Succeeded)
        {
            log.Warn($"git checkout {flag} refused {path} ({checkout.ExitCode}).");

            //Nothing has been recorded, so the path is still unmerged and still resolvable by every
            //other route. Returning before the add is what keeps that true.
            return ConflictResult.Failed(Words(checkout));
        }

        repositories.Invalidate(repository.Root);

        return await MarkResolvedAsync(repository, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records one path as resolved, whatever is in it now.
    ///
    /// This is the hand-edit path: the user took the markers out in the diff pane, saved, and is
    /// telling Git that what is on disk is the answer. It is <c>git add</c> and nothing else — the
    /// same command <c>CommitService.StageAsync</c> runs, because "resolved" and "staged" are one
    /// state in Git and pretending otherwise would need a second index.
    ///
    /// <b>It cannot check the file for markers, and must not pretend to.</b> A file legitimately
    /// containing a line of angle brackets exists — a diff of a conflict is one — so a refusal based
    /// on content would be a guess that blocks a correct resolution. Git itself does not check either.
    /// </summary>
    public async Task<ConflictResult> MarkResolvedAsync(
        RepositoryInfo repository,
        string path,
        CancellationToken cancellationToken)
    {
        //No --force, for CommitService.StageAsync's reasoning: an ignored path never reaches the file
        //list, so the flag has no case to serve here and would remove the gitignore backstop for any
        //path that arrived by another route.
        GitResult add = await git.RunAsync(
            repository.Root,
            ["add", "--", Literal(path)],
            cancellationToken).ConfigureAwait(false);

        if (!add.Succeeded)
        {
            log.Warn($"git add refused {path} ({add.ExitCode}).");
            return ConflictResult.Failed(Words(add));
        }

        repositories.Invalidate(repository.Root);
        log.Info($"Resolved {path} in {repository.Root}.");

        return ConflictResult.Ok;
    }

    /// <summary>
    /// Carries the operation on to its next commit, or to its end.
    ///
    /// <b>The gate is the whole reason this is in Core.</b> <see cref="UnmergedPathsAsync"/> is asked
    /// first, every time, and one unmerged path stops this with no Git command run at all. The window
    /// disables the button on the status it happens to be holding; this refuses on the state the
    /// repository is actually in a moment before the command would go.
    ///
    /// <b><c>-c core.editor=true</c> is not optional.</b> All four <c>--continue</c> spellings open an
    /// editor for the commit message, and this process has no console for one to appear on — so
    /// without it the command hangs until it is cancelled. <c>true</c> is the program that exits 0
    /// having written nothing, which is Git's own idiom for "take the message as it stands". That
    /// message is the one the interrupted commit already carried, so there is nothing here for a user
    /// to type.
    /// </summary>
    public async Task<ConflictResult> ContinueAsync(
        RepositoryInfo repository,
        MergeOperation operation,
        CancellationToken cancellationToken)
    {
        if (operation == MergeOperation.None)
            return ConflictResult.Failed("No operation is in progress.");

        IReadOnlyList<string> unmerged = await UnmergedPathsAsync(repository, cancellationToken)
            .ConfigureAwait(false);

        if (unmerged.Count > 0)
        {
            log.Warn($"Refused {MergeState.Verb(operation)} --continue: {unmerged.Count} path(s) still unmerged.");

            //Named rather than counted. "2 files are still conflicted" leaves the user hunting, and
            //the list is short by construction.
            return ConflictResult.Refused(string.Join("\n", unmerged));
        }

        GitResult result = await git.RunAsync(
            repository.Root,
            ["-c", "core.editor=true", MergeState.Verb(operation), "--continue"],
            cancellationToken).ConfigureAwait(false);

        repositories.Invalidate(repository.Root);

        if (result.Succeeded)
        {
            log.Info($"{MergeState.Verb(operation)} --continue succeeded in {repository.Root}.");
            return ConflictResult.Ok;
        }

        //Reported, never acted on. The ordinary cause is a step that resolved to no change at all,
        //where Git says so and names `--skip` — which this product does not offer, because skipping
        //drops somebody's commit.
        log.Warn($"{MergeState.Verb(operation)} --continue failed ({result.ExitCode}).");
        return ConflictResult.Failed(Words(result));
    }

    /// <summary>
    /// Throws the whole operation away and puts the branch back where it started.
    ///
    /// <b>Destructive, and reachable only by being called.</b> Everything resolved since the operation
    /// stopped goes with it, and Git keeps no reflog of a resolution. That is why the surface asks
    /// first, in its own words, and why nothing in this class falls through to here on a failure —
    /// CLAUDE.md's "do not automatically abort a rebase" is a rule about the code path, not about the
    /// wording of a message.
    ///
    /// It is still safe in the one sense that matters: <c>--abort</c> is Git's own documented way back,
    /// and it refuses rather than overwriting uncommitted work that predates the operation.
    /// </summary>
    public async Task<ConflictResult> AbortAsync(
        RepositoryInfo repository,
        MergeOperation operation,
        CancellationToken cancellationToken)
    {
        if (operation == MergeOperation.None)
            return ConflictResult.Failed("No operation is in progress.");

        GitResult result = await git.RunAsync(
            repository.Root,
            [MergeState.Verb(operation), "--abort"],
            cancellationToken).ConfigureAwait(false);

        repositories.Invalidate(repository.Root);

        if (result.Succeeded)
        {
            log.Info($"{MergeState.Verb(operation)} --abort succeeded in {repository.Root}.");
            return ConflictResult.Ok;
        }

        log.Warn($"{MergeState.Verb(operation)} --abort failed ({result.ExitCode}).");
        return ConflictResult.Failed(Words(result));
    }

    /// <summary>
    /// The paths Git still considers unmerged.
    ///
    /// <c>--diff-filter=U --name-only -z</c>: a machine format, per CLAUDE.md, and one whose output is
    /// nothing but paths. <c>ls-files --unmerged</c> answers the same question with up to three
    /// records per path and a mode and a hash to step over.
    ///
    /// <b>A failed read answers "one unmerged path", not "none".</b> This gates a command that writes
    /// history, and the direction to fail in is the one that stops.
    /// </summary>
    private async Task<IReadOnlyList<string>> UnmergedPathsAsync(
        RepositoryInfo repository,
        CancellationToken cancellationToken)
    {
        GitResult result = await git.ReadAsync(
            repository.Root,
            ["diff", "--name-only", "--diff-filter=U", "-z", .. GitDiffFlags.ReadSafe],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            log.Warn($"Could not list unmerged paths ({result.ExitCode}): {result.StdErr.Trim()}");
            return ["(the repository state could not be read)"];
        }

        var paths = new List<string>();

        //Never split on anything but the NUL: a path may contain any other byte, newlines included.
        //The trim is for the terminator some Git versions add after the last record, not for the
        //paths — an entry that is empty once stripped of it is not one.
        foreach (string entry in result.StdOut.Split('\0'))
        {
            if (entry.Trim('\r', '\n') is { Length: > 0 } path)
                paths.Add(path);
        }

        return paths;
    }

    /// <summary>Git's own account of a failure, wherever it chose to write it.</summary>
    private static string Words(GitResult result) =>
        result.StdErr.Trim() is { Length: > 0 } stderr ? stderr : result.StdOut.Trim();
}
