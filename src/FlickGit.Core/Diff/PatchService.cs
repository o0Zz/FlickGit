using FlickGit.Git;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;

namespace FlickGit.Diff;

/// <param name="Succeeded">False leaves the index exactly as it was — see the class remarks.</param>
/// <param name="Error">Git's own words. Never paraphrased, never generic.</param>
public sealed record PatchResult(bool Succeeded, string? Error)
{
    public static readonly PatchResult Ok = new(true, null);

    public static PatchResult Failed(string error) => new(false, error);
}

/// <summary>
/// Applies a patch to the index, and nothing else.
///
/// <b>The index only — never the working tree.</b> <c>git apply --cached</c> updates what would be
/// committed and leaves the file on disk untouched, which is what makes staging a hunk safe: whatever
/// the patch does, the user's editor still holds every character it did before. There is deliberately
/// no method here that omits <c>--cached</c>.
///
/// <b>Failure is atomic.</b> <c>git apply</c> validates the whole patch against the index before
/// changing anything, so a patch that does not fit is refused in full rather than applied halfway.
/// That is the property that lets a hunk be staged from a diff computed a moment ago without holding
/// a lock: if the index moved underneath, the answer is Git's own "patch does not apply" and the
/// index is untouched.
/// </summary>
public sealed class PatchService(IGitProcessRunner git, RepositoryService repositories, ILog log)
{
    /// <summary>Stages what <paramref name="patch"/> describes.</summary>
    public Task<PatchResult> StageAsync(RepositoryInfo repository, string patch, CancellationToken cancellationToken) =>
        ApplyAsync(repository, patch, reverse: false, cancellationToken);

    /// <summary>
    /// Takes back out of the index what <paramref name="patch"/> describes.
    ///
    /// The same patch, applied in reverse — which is why unstaging needs no separate patch generator
    /// and cannot disagree with staging about what a hunk is.
    /// </summary>
    public Task<PatchResult> UnstageAsync(RepositoryInfo repository, string patch, CancellationToken cancellationToken) =>
        ApplyAsync(repository, patch, reverse: true, cancellationToken);

    private async Task<PatchResult> ApplyAsync(
        RepositoryInfo repository,
        string patch,
        bool reverse,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "apply", "--cached" };

        if (reverse)
            args.Add("--reverse");

        //Read from stdin. A temp file would work too and would mean choosing where to put it, when to
        //delete it, and what to do when the delete fails.
        args.Add("-");

        GitResult result = await git
            .RunWithInputAsync(repository.Root, args, patch, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            //Logged at warning rather than error: a patch that no longer applies is an ordinary race
            //with an IDE saving in the background, not a fault in the product.
            log.Warn($"git apply --cached failed ({result.ExitCode}): {result.StdErr.Trim()}");


            return PatchResult.Failed(
                result.StdErr.Trim() is { Length: > 0 } stderr ? stderr : result.StdOut.Trim());
        }

        //The index moved, so every cached answer about this repository is stale -- and the palette's
        //overview reads the same generation counter.
        repositories.Invalidate(repository.Root);

        log.Info($"{(reverse ? "Unstaged" : "Staged")} a patch in {repository.Root}.");
        return PatchResult.Ok;
    }
}
