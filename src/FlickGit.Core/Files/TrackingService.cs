using FlickGit.Git;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;
using static FlickGit.Git.GitPathspec;

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
/// <b>Neither operation can reach the working tree, and that is the whole shape of this class.</b>
/// <see cref="AddAsync"/> only ever writes the index, and <see cref="UntrackAsync"/> carries
/// <c>--cached</c>, so removing a path from Git leaves the file exactly where it is — the row becomes
/// a staged deletion and the file comes back as untracked, both of which the user can see. There is
/// therefore nothing here to gate, nothing to send to the Recycle Bin and nothing to confirm: the
/// destructive removal this class used to perform, and the ordering rules that made it safe, are gone
/// rather than made optional.
///
/// <b>Recursion exists on exactly one vector and <c>--cached</c> is what disarms it.</b>
/// <c>git add</c> walks into a directory pathspec on its own, so <see cref="AddAsync"/> is the same
/// argument vector for a file and for a folder and carries no <c>-r</c> at all.
/// <see cref="UntrackAsync"/> needs the flag so that one call serves both, and it is safe there for a
/// reason a reviewer does not have to supply: <c>--cached</c> is on the same line, and a test asserts
/// there is no <c>-r</c> in this file without it.
///
/// <b>Nothing here is forced.</b> Without <c>-f</c>, <c>git rm --cached</c> refuses the one state it
/// could get wrong — index content differing from <i>both</i> HEAD and the file on disk, where
/// dropping the entry would strand a staged version nothing else holds. That refusal is Git's own
/// words, reported rather than parsed, and nothing has happened when it comes back.
/// </summary>
public sealed class TrackingService(IGitProcessRunner git, RepositoryService repositories, ILog log)
{
    /// <summary>
    /// How many paths the index holds under <paramref name="path"/> — one for a tracked file, zero
    /// for an untracked one, and everything below it for a folder.
    ///
    /// Asked before a removal, so a path Git has nothing under is answered with a sentence naming the
    /// fact rather than with <c>fatal: pathspec … did not match any files</c> — which is Git's
    /// accurate answer to a question the user did not ask. It is also what keeps such a path out of
    /// the batch entirely: <c>git rm</c> is all-or-nothing, so one untracked pathspec would refuse
    /// the removal of everything selected beside it.
    /// </summary>
    public Task<int> TrackedCountAsync(
        RepositoryInfo repository,
        string path,
        CancellationToken cancellationToken) =>
        CountAsync(repository, ["ls-files", "-z", "--", Literal(path)], cancellationToken);

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
    /// Takes <paramref name="paths"/> out of the index and <b>leaves every file exactly where it
    /// is</b> — the removal this product performs, on a file or on a folder.
    ///
    /// <c>--cached</c> is the whole operation rather than a variant of it. What the user asked for is
    /// "stop tracking this, keep my file", so nothing is deleted, nothing goes to the Recycle Bin and
    /// there is nothing to confirm. Git reports the result as two rows for the same path — a staged
    /// deletion, which is the change waiting to be committed, and an untracked file, which is the copy
    /// the user kept.
    ///
    /// <b>What it does to a staged addition is the same command and the right answer.</b> HEAD has no
    /// copy of such a path, so dropping the index entry is exactly unstaging it, and the file stays.
    /// One vector therefore covers "mark this deleted" and "unstage this" without asking which the
    /// row is.
    ///
    /// <c>-r</c> so that one call serves a folder as well as a file, and <c>--cached</c> immediately
    /// beside it so the flag cannot reach the working tree — see the class comment for the rule a test
    /// keeps.
    /// </summary>
    public Task<TrackingResult> UntrackAsync(
        RepositoryInfo repository,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken) =>
        paths.Count == 0
            ? Task.FromResult(TrackingResult.Ok)
            : RunAsync(
                repository,
                ["rm", "-r", "--cached", "--", .. paths.Select(Literal)],
                Untracked(paths),
                cancellationToken);

    /// <summary>
    /// What the log says a batch did. The one path is named when there is one, because that is the
    /// line worth reading back; past that the count is the honest summary and a hundred paths on one
    /// line is not.
    /// </summary>
    private static string Staged(IReadOnlyList<string> paths) =>
        paths.Count == 1 ? $"Staged {paths[0]}" : $"Staged {paths.Count} paths";

    /// <inheritdoc cref="Staged"/>
    private static string Untracked(IReadOnlyList<string> paths) =>
        paths.Count == 1 ? $"Untracked {paths[0]}" : $"Untracked {paths.Count} paths";

    /// <summary>
    /// How many entries a <c>-z</c> listing came back with.
    ///
    /// <b>A read that failed counts nothing</b>, which is the direction to fail in: zero is "Git has
    /// nothing here", and a removal that believes that leaves the path alone rather than running a
    /// command over a state it could not read.
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

        //The index moved -- the working tree never does here -- so every cached answer about this
        //repository is stale, and the palette's overview reads the same generation counter.
        repositories.Invalidate(repository.Root);

        log.Info($"{logMessage} in {repository.Root}.");
        return TrackingResult.Ok;
    }

    /// <summary>Git's own account of a failure, wherever it chose to write it.</summary>
    private static string Words(GitResult result) =>
        result.StdErr.Trim() is { Length: > 0 } stderr ? stderr : result.StdOut.Trim();
}
