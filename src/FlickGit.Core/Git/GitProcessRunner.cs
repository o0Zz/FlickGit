using System.Diagnostics;
using System.Text;
using FlickGit.Diagnostics;
using FlickGit.Logging;
using FlickGit.Models;

namespace FlickGit.Git;

/// <summary>
/// Runs git.exe. Async end to end, no console window, cancellable, and it never builds
/// a command string.
///
/// Four things here are load-bearing and each of them is a bug that has bitten real
/// Git front-ends:
///
/// <list type="number">
/// <item><description><b>ArgumentList, never a command line.</b> A repository path or
/// a branch name containing a space, a quote or an <c>&amp;</c> would otherwise be
/// re-parsed by the CRT's argv splitter. CLAUDE.md forbids string concatenation
/// anywhere in the codebase for exactly this reason.</description></item>
/// <item><description><b>stdout and stderr are read concurrently.</b> Draining one and
/// then the other deadlocks the moment the un-drained pipe's 4 KB buffer fills, which a
/// diff reaches immediately.</description></item>
/// <item><description><b><c>-c core.quotepath=false</c></b>, so a non-ASCII path comes
/// back as UTF-8 bytes rather than <c>\303\251</c> octal escapes that every parser
/// downstream would then have to un-escape.</description></item>
/// <item><description><b>Cancellation kills the whole process tree.</b> `git pull`
/// spawns ssh or the credential helper; killing only the parent leaves those holding
/// the repository's locks.</description></item>
/// </list>
/// </summary>
public sealed class GitProcessRunner(GitExecutable git, ILog log, OperationTimings? timings = null)
    : IGitProcessRunner
{
    /// <summary>UTF-8 with no BOM. Git speaks UTF-8 on stdout regardless of the console code page.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public Task<GitResult> RunAsync(
        string? repositoryPath,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken) =>
        ExecuteAsync(repositoryPath, args, readOnly: false, cancellationToken);

    public Task<GitResult> ReadAsync(
        string? repositoryPath,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken) =>
        ExecuteAsync(repositoryPath, args, readOnly: true, cancellationToken);

    /// <summary>
    /// The streaming path, for `clone --progress`.
    ///
    /// Separate from <see cref="ExecuteAsync"/> rather than a flag on it, because the two
    /// differ in the one thing that matters: this one must not wait for the stderr pipe to
    /// reach the end before reporting anything, and everything else in the product must.
    /// </summary>
    public async Task<GitResult> RunStreamingAsync(
        string? repositoryPath,
        IReadOnlyList<string> args,
        Action<string> onStandardErrorLine,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(onStandardErrorLine);

        var startInfo = new ProcessStartInfo
        {
            FileName = git.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            WorkingDirectory = repositoryPath is not null && Directory.Exists(repositoryPath)
                ? repositoryPath
                : Environment.CurrentDirectory,
        };

        BuildArguments(startInfo.ArgumentList, repositoryPath, args, readOnly: false);

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_FLUSH"] = "1";

        long startedAt = Stopwatch.GetTimestamp();

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            log.Error($"Failed to start git.exe ({git.Path}): {ex.Message}");
            throw new GitLaunchException(git.Path, ex);
        }

        process.StandardInput.Close();

        var stderrText = new StringBuilder();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task stderrTask = PumpStandardErrorAsync(process, stderrText, onStandardErrorLine);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        await stderrTask.ConfigureAwait(false);

        var duration = Stopwatch.GetElapsedTime(startedAt);
        var result = new GitResult(process.ExitCode, stdout, stderrText.ToString(), duration);

        string commandName = args.Count > 0 ? args[0] : "(none)";
        log.Debug($"git {commandName} (streaming) -> {result.ExitCode} in {duration.TotalMilliseconds:F0} ms");
        timings?.Record($"git {commandName}", duration);

        return result;
    }

    /// <summary>
    /// Reads stderr character by character, cutting a line on either terminator.
    ///
    /// A StreamReader line loop is not enough here. Git redraws a progress line by writing a
    /// carriage return and overwriting it, so an entire clone's progress is one "line" as far
    /// as ReadLine is concerned, and nothing would be reported until the clone finished --
    /// which is the exact opposite of the point.
    /// </summary>
    private static async Task PumpStandardErrorAsync(
        Process process,
        StringBuilder full,
        Action<string> onLine)
    {
        var line = new StringBuilder();
        char[] buffer = new char[512];

        while (true)
        {
            int read = await process.StandardError.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read == 0)
                break;

            full.Append(buffer, 0, read);

            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];

                if (c is '\r' or '\n')
                {
                    if (line.Length > 0)
                    {
                        onLine(line.ToString());
                        line.Clear();
                    }

                    continue;
                }

                line.Append(c);
            }
        }

        //Git's last progress line often has no terminator at all.
        if (line.Length > 0)
            onLine(line.ToString());
    }

    public Task<GitResult> RunWithInputAsync(
        string? repositoryPath,
        IReadOnlyList<string> args,
        string standardInput,
        CancellationToken cancellationToken) =>
        ExecuteAsync(repositoryPath, args, readOnly: false, standardInput, cancellationToken);

    private async Task<GitResult> ExecuteAsync(
        string? repositoryPath,
        IReadOnlyList<string> args,
        bool readOnly,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(repositoryPath, args, readOnly, standardInput: null, cancellationToken)
            .ConfigureAwait(false);

    private async Task<GitResult> ExecuteAsync(
        string? repositoryPath,
        IReadOnlyList<string> args,
        bool readOnly,
        string? standardInput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);

        var startInfo = new ProcessStartInfo
        {
            FileName = git.Path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,

            //stdin is redirected and, unless a caller supplied something to write, immediately
            //closed. Left attached and empty, a Git command that decides to ask something (a pager, a
            //merge tool, a prompt) would block forever on a console this process does not have.
            RedirectStandardInput = true,

            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,

            //No BOM, or the first line of a patch arrives with three bytes in front of it.
            StandardInputEncoding = Utf8NoBom,
            WorkingDirectory = repositoryPath is not null && Directory.Exists(repositoryPath)
                ? repositoryPath
                : Environment.CurrentDirectory,
        };

        BuildArguments(startInfo.ArgumentList, repositoryPath, args, readOnly);

        //No terminal exists behind this process, so a Git that decides to prompt on the
        //terminal would hang until cancelled. Failing fast with Git's own
        //"could not read Username" is a message the user can act on. A GUI credential
        //helper is unaffected -- that is a separate process with its own window.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        //Git's own progress spinner is meant for a terminal; without this it emits
        //carriage-return redraws into a pipe nobody renders.
        startInfo.Environment["GIT_FLUSH"] = "1";

        long startedAt = Stopwatch.GetTimestamp();

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            log.Error($"Failed to start git.exe ({git.Path}): {ex.Message}");
            throw new GitLaunchException(git.Path, ex);
        }

        //Both pipes drained at once. See the class comment -- doing this sequentially is
        //a deadlock on any output larger than a pipe buffer. Started *before* stdin is written for
        //the same reason: a patch bigger than the pipe buffer would block this process on the write
        //while Git blocks on an output nobody is reading.
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        if (standardInput is not null)
        {
            //NewLine left alone and Write rather than WriteLine: the text already contains its own
            //line breaks, and translating them would rewrite the carriage returns a CRLF file's
            //patch carries on purpose.
            await process.StandardInput.WriteAsync(standardInput).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }

        process.StandardInput.Close();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            throw;
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        var duration = Stopwatch.GetElapsedTime(startedAt);
        var result = new GitResult(process.ExitCode, stdout, stderr, duration);

        //Command name and timing only. CLAUDE.md, "Logging": never the diff, never file
        //contents, never a commit message body -- and Git's stderr is only recorded on
        //failure, where the user is about to be shown it anyway.
        string commandName = args.Count > 0 ? args[0] : "(none)";
        log.Debug($"git {commandName} -> {result.ExitCode} in {duration.TotalMilliseconds:F0} ms  [{repositoryPath}]");
        timings?.Record($"git {commandName}", duration);

        if (!result.Succeeded)
            log.Warn($"git {commandName} failed ({result.ExitCode}): {Truncate(stderr)}");

        return result;
    }

    /// <summary>
    /// Assembles the argument list. Public-in-effect via the tests, which assert that
    /// every invariant below holds for the commands the product actually issues.
    /// </summary>
    internal static void BuildArguments(
        IList<string> target,
        string? repositoryPath,
        IReadOnlyList<string> args,
        bool readOnly)
    {
        //-C before everything else, so Git resolves the repository before any -c option
        //or subcommand is considered. Always -C, never "cd then run": the working
        //directory of this process is shared by the whole resident service.
        if (repositoryPath is not null)
        {
            target.Add("-C");
            target.Add(repositoryPath);
        }

        //Non-ASCII paths arrive as raw UTF-8 rather than octal escapes.
        target.Add("-c");
        target.Add("core.quotepath=false");

        if (readOnly)
        {
            //THE flag. Without it `git status` takes the index lock to write a refreshed
            //stat cache, and a background scan across ten repositories then collides with
            //whatever the user's IDE is doing in the same trees.
            target.Add("--no-optional-locks");
        }

        foreach (string arg in args)
        {
            ArgumentNullException.ThrowIfNull(arg);
            target.Add(arg);
        }
    }

    private void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            //The process exited between the check and the kill, or it was already gone.
            //Nothing to recover, and cancellation is already propagating.
            log.Debug($"Killing the git process tree after cancellation failed: {ex.Message}");
        }
    }

    private static string Truncate(string text)
    {
        string single = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return single.Length <= 400 ? single : single[..400] + "…";
    }
}

/// <summary>git.exe exists but could not be started at all.</summary>
public sealed class GitLaunchException(string gitPath, Exception inner)
    : Exception($"Could not start git.exe:\n\n{gitPath}\n\n{inner.Message}", inner);
