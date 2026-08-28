using FlickGit.Git;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;
using static FlickGit.Git.GitPathspec;

namespace FlickGit.Commits;

/// <summary>
/// Staging and committing.
///
/// Two rules here are absolute, both from CLAUDE.md:
///
/// <list type="bullet">
/// <item><description><b>Never `git add -A` or `git add .`.</b> Not anywhere, not as a
/// shortcut when every box happens to be ticked. The user's selection is the commit; a
/// bulk add would quietly include whatever appeared in the working tree between the
/// status refresh and the button press.</description></item>
/// <item><description><b>The message always goes through a temp file</b>, even a
/// one-line one. `commit -m` puts the message on a command line, and a message
/// containing a quote, a percent sign or a newline then depends on how the CRT feels
/// about it. `-F file` has no quoting rules at all.</description></item>
/// </list>
/// </summary>
public sealed class CommitService(IGitProcessRunner git, RepositoryService repositories, ILog log)
{
    /// <summary>
    /// Stages exactly <paramref name="paths"/>.
    ///
    /// <c>--</c> before the path list is not decoration: without it a file named
    /// <c>-f</c> or <c>--cached</c> is read as an option. <c>--</c> alone is not enough either:
    /// everything after it is still a pathspec, so every path goes through
    /// <see cref="GitPathspec.Literal"/> -- a ticked <c>report[final].xlsx</c> would otherwise stage
    /// <c>reportf.xlsx</c> instead, and the commit would not be the one the user reviewed.
    /// </summary>
    public async Task StageAsync(
        RepositoryInfo repository,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
            return;

        //No --force. It cannot do any good here and it can do harm: StatusService passes no
        //--ignored, so an ignored file never appears in the list and can never be ticked -- there is
        //no "the user explicitly asked for this ignored file" case for the flag to serve. What it
        //would do is remove the last .gitignore backstop for any path that reaches this method by
        //another route, on the one code path that stages the commit. CLAUDE.md, "Staging defaults":
        //keeping .env, appsettings.Development.json, bin/ and obj/ out of a hurried commit is "the
        //single most valuable safety default in the product", and gitignore is half of it.
        var args = new List<string>(paths.Count + 2) { "add", "--" };
        args.AddRange(paths.Select(Literal));

        GitResult result = await git.RunAsync(repository.Root, args, cancellationToken).ConfigureAwait(false);
        repositories.Invalidate(repository.Root);

        if (!result.Succeeded)
            throw new GitOperationException("Stage files", repository.Root, result);
    }

    /// <summary>
    /// Unstages <paramref name="paths"/>, leaving the working tree untouched.
    ///
    /// `git restore --staged` and nothing else. It is unambiguous about not touching the
    /// working tree, unlike `git reset`, whose name also spells the most destructive command in
    /// Git — and there is no fallback to it: Git 2.23 is the stated minimum.
    /// </summary>
    public async Task UnstageAsync(
        RepositoryInfo repository,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
            return;

        var args = new List<string>(paths.Count + 3) { "restore", "--staged", "--" };
        args.AddRange(paths.Select(Literal));

        GitResult result = await git.RunAsync(repository.Root, args, cancellationToken).ConfigureAwait(false);
        repositories.Invalidate(repository.Root);

        if (!result.Succeeded)
            throw new GitOperationException("Unstage files", repository.Root, result);
    }

    /// <summary>
    /// Commits whatever is staged, with <paramref name="message"/>.
    ///
    /// Refuses an empty message rather than letting Git open an editor that has nowhere
    /// to appear — this process has no console, so `git commit` with no message would
    /// hang until cancelled.
    /// </summary>
    /// <returns>The short hash of the new commit.</returns>
    public async Task<CommitResult> CommitAsync(
        RepositoryInfo repository,
        string message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message))
            throw new InvalidOperationException("A commit message is required.");

        //Written into the Git directory rather than %TEMP%: same volume as the
        //repository, so no cross-device copy, and it is inside the trust boundary Git
        //already owns. Named per-process so two commits in two windows cannot collide.
        //
        //RepositoryInfo.GitDirectory, never <root>\.git composed by hand: in a submodule and in a
        //linked worktree that name is a *file*, so composing it throws DirectoryNotFoundException
        //here -- after StageAsync has already mutated the index, and with no commit possible from
        //FlickGit in either kind of repository.
        string messageFile = Path.Combine(
            repository.GitDirectory,
            $"FLICKGIT_COMMITMSG_{Environment.ProcessId}_{Environment.CurrentManagedThreadId}");

        try
        {
            //UTF-8 without a BOM. A BOM would end up as the first three bytes of the
            //commit subject, which shows as a stray glyph in every log viewer.
            await File.WriteAllTextAsync(
                messageFile,
                Normalise(message),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken).ConfigureAwait(false);

            GitResult result = await git.RunAsync(
                repository.Root,
                ["commit", "-F", messageFile],
                cancellationToken).ConfigureAwait(false);

            repositories.Invalidate(repository.Root);

            if (!result.Succeeded)
                throw new GitOperationException("Commit", repository.Root, result);

            GitResult hash = await git.ReadAsync(
                repository.Root,
                ["rev-parse", "--short", "HEAD"],
                cancellationToken).ConfigureAwait(false);

            return new CommitResult(
                hash.Succeeded ? hash.StdOut.Trim() : string.Empty,
                FirstLine(message));
        }
        finally
        {
            //"Delete it afterwards, including on failure." A commit message is user
            //content, and leaving it in .git for the next person to find is exactly the
            //kind of thing this tool must not do.
            TryDelete(messageFile);
        }
    }

    /// <summary>
    /// True when the index holds something to commit. Checked before the button is
    /// enabled, so the user is never told "nothing to commit" after pressing it.
    /// </summary>
    public async Task<bool> HasStagedChangesAsync(
        RepositoryInfo repository,
        CancellationToken cancellationToken)
    {
        //--quiet makes this an exit-code question: 1 means there are staged differences.
        //Cheaper than parsing a diff nobody is going to read.
        GitResult result = await git.ReadAsync(
            repository.Root,
            ["diff", "--cached", "--quiet", .. GitDiffFlags.ReadSafe],
            cancellationToken).ConfigureAwait(false);

        //0 and 1 are the answer; anything else is Git failing, and "no" is then a guess rather than
        //a reading. Still answered "no" -- the caller disables a button with it, and a window that
        //refused to open would be worse -- but logged, because otherwise the case is invisible and
        //the user is told there is nothing to commit when the truth is that git did not run.
        if (result.ExitCode is not (0 or 1))
            log.Warn($"git diff --cached --quiet exited {result.ExitCode}: {result.StdErr.Trim()}");

        return result.ExitCode == 1;
    }

    /// <summary>
    /// CRLF-normalised and trailing-whitespace-trimmed message body.
    ///
    /// Git stores commit messages with LF endings; writing CRLF would leave a carriage
    /// return at the end of every line of the message, visible as <c>^M</c> in `git log`
    /// on any non-Windows machine looking at the same repository.
    /// </summary>
    private static string Normalise(string message)
    {
        string lf = message.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();

        //Git appends nothing; a message without a trailing newline is legal but every
        //other tool writes one.
        return lf + "\n";
    }

    private static string FirstLine(string message)
    {
        int newline = message.IndexOfAny(['\r', '\n']);
        return (newline < 0 ? message : message[..newline]).Trim();
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            log.Warn($"Could not delete the commit message temp file: {ex.Message}");
        }
    }
}

/// <param name="ShortHash">From `rev-parse --short HEAD`, shown in the success notification.</param>
/// <param name="Subject">The message's first line, for the same notification.</param>
public sealed record CommitResult(string ShortHash, string Subject);
