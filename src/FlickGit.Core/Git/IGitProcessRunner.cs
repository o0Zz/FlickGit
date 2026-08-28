using FlickGit.Models;

namespace FlickGit.Git;

/// <summary>
/// The single way anything in FlickGit runs Git. There is no other process-start call
/// in the product.
///
/// The read/write split is not cosmetic. CLAUDE.md, "Git Command Execution":
/// read operations must always pass <c>--no-optional-locks</c>, or `git status`
/// refreshes and writes the index, and the tool ends up fighting the user's IDE over
/// <c>index.lock</c>. Making that a separate method means forgetting it is a
/// compile-time choice rather than a silently missing flag.
/// </summary>
public interface IGitProcessRunner
{
    /// <summary>
    /// Runs a Git command that may write: commit, add, switch, push.
    /// </summary>
    /// <param name="repositoryPath">Repository root, or null for a repository-less command.</param>
    /// <param name="args">Arguments after `git`. Passed to ArgumentList verbatim; never concatenated.</param>
    Task<GitResult> RunAsync(
        string? repositoryPath,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs a read-only Git command, adding <c>--no-optional-locks</c>. Use this for
    /// anything that only observes: status, diff, show, rev-parse, for-each-ref.
    /// </summary>
    Task<GitResult> ReadAsync(
        string? repositoryPath,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs a Git command, feeding <paramref name="standardInput"/> to it.
    ///
    /// Exists for <c>git apply --cached -</c>, which reads its patch from stdin. Every other command
    /// in the product has stdin closed immediately — see <see cref="RunAsync"/> — because a Git that
    /// decides to prompt would otherwise block forever on a console this process does not have.
    ///
    /// The text is written as UTF-8 with no byte-order mark and no line-ending translation. Both
    /// matter: a BOM would be read as part of the first patch line, and rewriting <c>\n</c> as
    /// <c>\r\n</c> would corrupt the carriage returns a CRLF file's patch deliberately carries.
    /// </summary>
    Task<GitResult> RunWithInputAsync(
        string? repositoryPath,
        IReadOnlyList<string> args,
        string standardInput,
        CancellationToken cancellationToken);

    /// <summary>
    /// Runs a Git command, reporting each stderr line as it arrives.
    ///
    /// Exists for exactly one reason: `git clone --progress` writes its progress to
    /// <b>stderr</b>, not stdout, and a determinate progress bar needs those lines while the
    /// clone is still running. <see cref="RunAsync"/> reads both pipes to the end, which is
    /// correct for every other command in the product and useless for this one.
    ///
    /// <paramref name="onStandardErrorLine"/> is invoked on a background thread, once per
    /// line. Git separates progress redraws with carriage returns rather than newlines, so
    /// the implementation splits on both.
    /// </summary>
    Task<GitResult> RunStreamingAsync(
        string? repositoryPath,
        IReadOnlyList<string> args,
        Action<string> onStandardErrorLine,
        CancellationToken cancellationToken);

    /// <summary>
    /// A read whose stdout is <b>not decoded</b>. Same flags and same guarantees as
    /// <see cref="ReadAsync"/> in every other respect.
    ///
    /// For reading a blob, and only that. Everything else Git writes to stdout is a machine format
    /// this product chose and is UTF-8 by construction; a blob is the user's own file, in whatever
    /// encoding they committed it, and decoding it as UTF-8 is a one-way loss.
    /// </summary>
    Task<GitResult.Bytes> ReadBytesAsync(
        string? repositoryPath,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken);
}
