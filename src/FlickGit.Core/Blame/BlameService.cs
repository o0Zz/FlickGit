using System.Diagnostics;
using FlickGit.Diagnostics;
using FlickGit.Git;
using FlickGit.Models;

namespace FlickGit.Blame;

/// <summary>
/// One question, asked once: who last touched each line of this file, at this revision.
///
/// <b>Read-only in the strict sense.</b> The blame window offers no checkout, revert, cherry-pick or
/// edit, and this service is the only thing behind it — every call goes through
/// <see cref="IGitProcessRunner.ReadAsync"/>, which is what makes that promise mechanical rather
/// than a matter of review. A test asserts it.
///
/// The walk back through history is not implemented here, because Git implements it:
/// <see cref="BlameCommit.PreviousSha"/> and <see cref="BlameCommit.PreviousPath"/> come straight
/// out of the porcelain stream, so "blame the previous version" is this same method called again
/// with those two values. Nothing appends <c>^</c>, and a rename is followed for free.
/// </summary>
public sealed class BlameService(IGitProcessRunner git, OperationTimings? timings = null)
{
    /// <param name="relativePath">Repository-relative, forward slashes, as Git spells a path.</param>
    /// <param name="revision">
    /// The commit to blame at, or null for the working tree — which is what a right-click on a file
    /// means, and which reports uncommitted lines under a sha of forty zeros.
    /// </param>
    public async Task<BlameOutcome> BlameAsync(
        RepositoryInfo repository,
        string relativePath,
        string? revision,
        CancellationToken cancellationToken)
    {
        long startedAt = Stopwatch.GetTimestamp();

        var args = new List<string> { "blame", "--porcelain" };

        if (revision is { Length: > 0 })
            args.Add(revision);

        //-- so a path that looks like a revision is still a path.
        args.Add("--");
        args.Add(relativePath);

        //No --no-color, unlike every other command in the product: the porcelain format carries no
        //colour whatever `color.ui` says, and Hard Requirement 2 rules out a flag that does nothing.
        //
        //`blame.ignoreRevsFile` is deliberately *not* overridden. A user who configured a
        //.git-blame-ignore-revs did it so a bulk reformat stops masking authorship -- overriding it
        //would be overriding the answer they asked for.
        GitResult result = await git.ReadAsync(repository.Root, args, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            //Git's own words. "no such path ... in HEAD" is what an untracked file gets, and it is
            //exactly what the user needs to read.
            return BlameOutcome.Failed(result.ErrorText);
        }

        IReadOnlyList<BlameLine> lines = BlamePorcelainParser.Parse(result.StdOut);

        //Git does not refuse to blame a binary file -- it blames it into nonsense, one "line" per
        //run of bytes that happened to contain a newline. Sniffing the text we already hold costs
        //nothing; asking `git show` so FileTextLoader could decide would cost a process on every
        //blame to catch a case that is almost never hit.
        if (lines.Any(static l => l.Text.Contains('\0', StringComparison.Ordinal)))
            return BlameOutcome.Binary;

        timings?.Record("blame.read", Stopwatch.GetElapsedTime(startedAt));

        return new BlameOutcome(true, lines, null, false);
    }
}
