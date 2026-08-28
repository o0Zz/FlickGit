using FlickGit.Git;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;

namespace FlickGit.Files;

/// <param name="Succeeded">False leaves the working tree and the index exactly as they were.</param>
/// <param name="Error">Git's own words. Never paraphrased, never generic.</param>
public sealed record TrackingResult(bool Succeeded, string? Error)
{
    public static readonly TrackingResult Ok = new(true, null);

    public static TrackingResult Failed(string error) => new(false, error);
}

/// <summary>
/// Puts one path under Git's control, or takes it out again — the operations the Explorer menu
/// offers on a right-clicked file beside Blame, and on a right-clicked folder at the foot of the
/// submenu.
///
/// <b>The paths are a resolved selection, and the guard is that resolution rather than the arity.</b>
/// Explorer hands over every item the user selected, so both take a list. What keeps a list from
/// quietly meaning "everything" is not that it is short: it is that <c>RepositoryVerbs.PathIn</c> has
/// already refused the repository root by name, and anything resolving outside it, <i>per path</i>,
/// before one of them reaches here. An empty list is therefore a command that stages nothing rather
/// than one that widens. <b>An empty list runs no command at all</b>, because <c>git add --</c> with
/// no pathspec after it is the one shape a plural signature could produce that means something other
/// than what was asked — the same rule <c>ActionPlaceholders</c> keeps for <c>{files}</c>, where
/// "expanding it to <c>.</c> would be the single worst thing this class could do".
///
/// <b>One process for the whole selection</b>, which is also what <c>CommitService.StageAsync</c>
/// does with a commit's ticked paths. Git is all-or-nothing over the pathspecs after <c>--</c>, so a
/// path it will not take stops the batch with nothing done rather than leaving half of it applied and
/// no way for the user to tell which half.
///
/// <b>Recursion exists here, and every place it does is named.</b> <c>git add</c> walks into a
/// directory pathspec on its own, so <see cref="AddAsync"/> is the same argument vector for a file
/// and for a folder and carries no <c>-r</c> at all. Removal needs the flag, and it appears on
/// exactly two vectors, each disarmed by a second flag beside it: <see cref="CanRemoveFolderAsync"/>
/// is <c>--dry-run</c> and therefore changes nothing, and <see cref="RemoveFolderAsync"/> is
/// <c>--cached</c> and therefore cannot reach the working tree. <b>There is no <c>-r</c> in this
/// file without one of those two words next to it</b>, which is what a test asserts rather than a
/// reviewer.
///
/// The step those two leave out — actually removing the folder from disk — is not Git's here. It is
/// the Recycle Bin, through the App's <c>WorkingTreeDeleter</c>, because a folder holds untracked
/// files that <c>git rm</c> would refuse and that <c>git restore</c> could never bring back.
///
/// <b>Nothing here is forced.</b> <c>git rm</c> without <c>-f</c> refuses a file whose content
/// differs from both HEAD and the index, which is exactly CLAUDE.md's "never discard uncommitted
/// work" enforced by Git rather than by us — and what is left is recoverable, because HEAD still has
/// the content and the file list's <i>Revert file…</i> restores it. That is what lets a deletion sit
/// behind one confirmation instead of a warning nobody can act on.
/// </summary>
public sealed class TrackingService(IGitProcessRunner git, RepositoryService repositories, ILog log)
{
    /// <summary>
    /// How many paths the index holds under <paramref name="path"/> — one for a tracked file, zero
    /// for an untracked one, and everything below it for a folder.
    ///
    /// Asked before a removal, so an untracked path is refused with a sentence naming the fact rather
    /// than with <c>fatal: pathspec … did not match any files</c> — which is Git's accurate answer to
    /// a question the user did not ask. Asked as a count rather than as a yes/no because the folder
    /// confirmation has to state the number before it is allowed to happen.
    /// </summary>
    public Task<int> TrackedCountAsync(
        RepositoryInfo repository,
        string path,
        CancellationToken cancellationToken) =>
        CountAsync(repository, ["ls-files", "-z", "--", Literal(path)], cancellationToken);

    /// <summary>
    /// How many files under <paramref name="path"/> Git has never seen — the ones an add would start
    /// tracking, and the ones a removal sends to the Recycle Bin with nothing behind them.
    ///
    /// <c>--exclude-standard</c>, so the answer is the user's own files rather than <c>bin</c> and
    /// <c>obj</c>. Those are still inside the folder and still go to the bin with it, which is what
    /// the confirmation says instead of counting them.
    /// </summary>
    public Task<int> UntrackedCountAsync(
        RepositoryInfo repository,
        string path,
        CancellationToken cancellationToken) =>
        CountAsync(
            repository,
            ["ls-files", "-z", "--others", "--exclude-standard", "--", Literal(path)],
            cancellationToken);

    /// <summary>
    /// How many tracked files under <paramref name="path"/> differ from the index — what an add would
    /// change there, over and above the untracked ones it would start tracking.
    ///
    /// <c>--name-only -z</c> rather than <c>--numstat</c>: the question is how many, not how much,
    /// and this one counts a file deleted from the working tree, which <c>git add</c> also stages.
    ///
    /// The three read-safe flags, like every other diff read in the product: this is a count the
    /// folder-removal question puts in front of the user, and a <c>diff.external</c> driver in their
    /// own gitconfig would answer with something no parser here reads as a NUL-separated name list.
    /// </summary>
    public Task<int> ChangedCountAsync(
        RepositoryInfo repository,
        string path,
        CancellationToken cancellationToken) =>
        CountAsync(
            repository,
            ["diff", "--name-only", "-z", .. GitDiffFlags.ReadSafe, "--", Literal(path)],
            cancellationToken);

    /// <summary>
    /// Stages <paramref name="paths"/>, which for anything Git has never seen is what starts tracking
    /// it.
    ///
    /// <b>The same argument vector for a file and for a folder.</b> A directory pathspec matches
    /// everything below it, so Git supplies the recursion and no flag of ours has to — which is why
    /// a mixed selection needs no sorting into two calls here.
    /// </summary>
    public Task<TrackingResult> AddAsync(
        RepositoryInfo repository,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken) =>
        paths.Count == 0
            ? Task.FromResult(TrackingResult.Ok)
            : RunAsync(
                repository,
                ["add", "--", .. paths.Select(Literal)],
                Staged(paths),
                cancellationToken);

    /// <summary>
    /// Deletes <paramref name="paths"/> from the working tree and stages the deletions.
    ///
    /// <b>Every path here is a file, and that is why there is no <c>-r</c> on this vector.</b> A
    /// folder does not come this way: it goes to the Recycle Bin through <see cref="RemovalFlow"/>,
    /// because a folder is exactly where the untracked files are and <c>git rm</c> would either refuse
    /// over them or leave them behind.
    /// </summary>
    public Task<TrackingResult> RemoveAsync(
        RepositoryInfo repository,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken) =>
        paths.Count == 0
            ? Task.FromResult(TrackingResult.Ok)
            : RunAsync(
                repository,
                ["rm", "--", .. paths.Select(Literal)],
                Removed(paths),
                cancellationToken);

    /// <summary>
    /// Whether every file in <paramref name="paths"/> could be removed — with nothing done about it
    /// either way.
    ///
    /// <b>This is a gate, not a preview</b>, and it is the file half of the one <see cref="RemovalFlow"/>
    /// runs before it asks anything. Today a single file was gated by <c>git rm</c> itself refusing when
    /// it ran, which is safe only because that call was the whole operation. In a selection it is not:
    /// a folder binned first and a file refused second is half a removal, so the refusal has to be
    /// collected before the question rather than discovered after it.
    ///
    /// One call for the list, because <c>git rm</c> is all-or-nothing over the pathspecs after
    /// <c>--</c> and its refusal already names the offending files — <b>reported, never parsed</b>.
    ///
    /// <b>No <c>-r</c> here.</b> These are files, and the flag is the difference between answering
    /// about what was clicked and answering about a tree.
    /// </summary>
    public async Task<TrackingResult> CanRemoveAsync(
        RepositoryInfo repository,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
            return TrackingResult.Ok;

        GitResult result = await git.ReadAsync(
            repository.Root,
            ["rm", "--dry-run", "--", .. paths.Select(Literal)],
            cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
            return TrackingResult.Ok;

        log.Warn($"git rm --dry-run refused {paths.Count} path(s) ({result.ExitCode}).");
        return TrackingResult.Failed(Words(result));
    }

    /// <summary>
    /// Whether every tracked file under the folder <paramref name="path"/> could be removed — with
    /// nothing done about it either way.
    ///
    /// <b>This is a gate, not a preview.</b> <c>--dry-run</c> runs the same check the real command
    /// would and exits non-zero naming every file whose content differs from both HEAD and the index.
    /// It is asked <i>before</i> the Recycle Bin, which is the one step in the folder removal that
    /// Git does not perform and therefore cannot refuse: by the time the folder is in the bin,
    /// "never discard uncommitted work" is no longer a decision anything can make. Answering here is
    /// what keeps the whole sequence honest.
    ///
    /// Git's refusal is <b>reported, never parsed</b> — it names the offending files, which is a
    /// better answer than any of ours, and reading it as data would be reading human-readable Git
    /// output.
    /// </summary>
    public async Task<TrackingResult> CanRemoveFolderAsync(
        RepositoryInfo repository,
        string path,
        CancellationToken cancellationToken)
    {
        GitResult result = await git.ReadAsync(
            repository.Root,
            ["rm", "-r", "--dry-run", "--", Literal(path)],
            cancellationToken).ConfigureAwait(false);

        if (result.Succeeded)
            return TrackingResult.Ok;

        log.Warn($"git rm --dry-run refused {path} ({result.ExitCode}).");
        return TrackingResult.Failed(Words(result));
    }

    /// <summary>
    /// Records the deletion of the folder <paramref name="path"/> in the index, the working tree
    /// having already been dealt with.
    ///
    /// <b><c>--cached</c>, so this cannot delete anything.</b> The folder is in the Recycle Bin by the
    /// time this runs, and the flag is what makes that ordering structural rather than a comment: a
    /// bare <c>git rm -r</c> here would be a second thing able to destroy the user's files, reached
    /// after the one question has already been answered.
    /// </summary>
    public Task<TrackingResult> RemoveFolderAsync(
        RepositoryInfo repository,
        string path,
        CancellationToken cancellationToken) =>
        RunAsync(
            repository,
            ["rm", "-r", "--cached", "--", Literal(path)],
            $"Removed {path}",
            cancellationToken);

    /// <summary>
    /// What the log says a batch did. The one path is named when there is one, because that is the
    /// line worth reading back; past that the count is the honest summary and a hundred paths on one
    /// line is not.
    /// </summary>
    private static string Staged(IReadOnlyList<string> paths) =>
        paths.Count == 1 ? $"Staged {paths[0]}" : $"Staged {paths.Count} paths";

    /// <inheritdoc cref="Staged"/>
    private static string Removed(IReadOnlyList<string> paths) =>
        paths.Count == 1 ? $"Removed {paths[0]}" : $"Removed {paths.Count} paths";

    /// <summary>
    /// The path as a <b>pathspec that cannot glob</b>.
    ///
    /// Everything after <c>--</c> is still a pathspec, so <c>a[1].txt</c> — an ordinary Windows file
    /// name — is read as a character class: it matches <c>a1.txt</c> instead, and <c>git rm</c> would
    /// then delete a file nobody clicked. <c>:(literal)</c> is Git's own way of saying "these are
    /// bytes, not a pattern", and it is what makes one click act on exactly the path that was
    /// clicked. It is no less load-bearing on a folder: <c>dumps/a[1]</c> and <c>dumps/a1</c> are two
    /// directories, and only one of them was pointed at.
    /// </summary>
    private static string Literal(string path) => $":(literal){path}";

    /// <summary>
    /// How many entries a <c>-z</c> listing came back with.
    ///
    /// <b>A read that failed counts nothing</b>, which is the direction to fail in: for a removal
    /// zero is the refusal, and for an add it is "nothing to stage" rather than a stage of unknown
    /// size. Both stop.
    /// </summary>
    private async Task<int> CountAsync(
        RepositoryInfo repository,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        GitResult result = await git.ReadAsync(repository.Root, args, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            log.Warn($"git {args[0]} failed ({result.ExitCode}): {result.StdErr.Trim()}");
            return 0;
        }

        int count = 0;

        //Never split on anything but the NUL: a path may contain any other byte, spaces and newlines
        //included. The trim is for the terminator Git puts after the last record on some versions,
        //not for the paths -- an entry that is empty once stripped of it is not a path.
        foreach (string entry in result.StdOut.Split('\0'))
        {
            if (entry.Trim('\r', '\n').Length > 0)
                count++;
        }

        return count;
    }

    private async Task<TrackingResult> RunAsync(
        RepositoryInfo repository,
        IReadOnlyList<string> args,
        string logMessage,
        CancellationToken cancellationToken)
    {
        GitResult result = await git
            .RunAsync(repository.Root, args, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            log.Warn($"git {args[0]} failed ({result.ExitCode}): {result.StdErr.Trim()}");

            return TrackingResult.Failed(Words(result));
        }

        //The index moved, and for a removal the working tree with it -- so every cached answer about
        //this repository is stale, and the palette's overview reads the same generation counter.
        repositories.Invalidate(repository.Root);

        log.Info($"{logMessage} in {repository.Root}.");
        return TrackingResult.Ok;
    }

    /// <summary>Git's own account of a failure, wherever it chose to write it.</summary>
    private static string Words(GitResult result) =>
        result.StdErr.Trim() is { Length: > 0 } stderr ? stderr : result.StdOut.Trim();
}
