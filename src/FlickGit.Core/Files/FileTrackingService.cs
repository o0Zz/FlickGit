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
/// Puts one file under Git's control, or takes it out again — the two operations the Explorer file
/// menu offers beside Blame.
///
/// <b>One path per call, and no recursion.</b> Both commands are reached from a right-click on a
/// single file, so there is deliberately no overload taking a list and no <c>-r</c> anywhere: a
/// method that could take every path is a method that could be handed all of them. A directory
/// handed to <see cref="RemoveAsync"/> is refused by Git itself, in words that name the flag it
/// would need, which is a better answer than one of ours.
///
/// <b>Nothing here is forced.</b> <c>git rm</c> without <c>-f</c> refuses a file whose content
/// differs from both HEAD and the index, which is exactly CLAUDE.md's "never discard uncommitted
/// work" enforced by Git rather than by us — and what is left is recoverable, because HEAD still has
/// the content and the file list's <i>Revert file…</i> restores it. That is what lets a deletion sit
/// behind one confirmation instead of a warning nobody can act on.
/// </summary>
public sealed class FileTrackingService(IGitProcessRunner git, RepositoryService repositories, ILog log)
{
    /// <summary>
    /// Whether the index has <paramref name="path"/> at all.
    ///
    /// Asked before a removal, so an untracked file is refused with a sentence naming the fact rather
    /// than with <c>fatal: pathspec … did not match any files</c> — which is Git's accurate answer to
    /// a question the user did not ask.
    /// </summary>
    public async Task<bool> IsTrackedAsync(
        RepositoryInfo repository,
        string path,
        CancellationToken cancellationToken)
    {
        GitResult result = await git.ReadAsync(
            repository.Root,
            ["ls-files", "-z", "--", Literal(path)],
            cancellationToken).ConfigureAwait(false);

        //-z, so the answer is the path itself followed by a NUL, and nothing at all when the index does
        //not have it. Never split: the only question is whether anything came back.
        return result.Succeeded && result.StdOut.Trim('\0', '\r', '\n').Length > 0;
    }

    /// <summary>
    /// Stages <paramref name="path"/>, which for a file Git has never seen is what starts tracking it.
    /// </summary>
    public Task<TrackingResult> AddAsync(
        RepositoryInfo repository,
        string path,
        CancellationToken cancellationToken) =>
        RunAsync(repository, ["add", "--", Literal(path)], $"Staged {path}", cancellationToken);

    /// <summary>
    /// Deletes <paramref name="path"/> from the working tree and stages the deletion.
    /// </summary>
    public Task<TrackingResult> RemoveAsync(
        RepositoryInfo repository,
        string path,
        CancellationToken cancellationToken) =>
        RunAsync(repository, ["rm", "--", Literal(path)], $"Removed {path}", cancellationToken);

    /// <summary>
    /// The path as a <b>pathspec that cannot glob</b>.
    ///
    /// Everything after <c>--</c> is still a pathspec, so <c>a[1].txt</c> — an ordinary Windows file
    /// name — is read as a character class: it matches <c>a1.txt</c> instead, and <c>git rm</c> would
    /// then delete a file nobody clicked. <c>:(literal)</c> is Git's own way of saying "these are
    /// bytes, not a pattern", and it is what makes one click act on exactly one file.
    /// </summary>
    private static string Literal(string path) => $":(literal){path}";

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

            return TrackingResult.Failed(
                result.StdErr.Trim() is { Length: > 0 } stderr ? stderr : result.StdOut.Trim());
        }

        //The index moved, and for a removal the working tree with it -- so every cached answer about
        //this repository is stale, and the palette's overview reads the same generation counter.
        repositories.Invalidate(repository.Root);

        log.Info($"{logMessage} in {repository.Root}.");
        return TrackingResult.Ok;
    }
}
