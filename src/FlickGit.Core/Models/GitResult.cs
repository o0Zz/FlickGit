namespace FlickGit.Models;

/// <summary>
/// The outcome of one git.exe invocation. Immutable, and the only thing
/// <see cref="Git.IGitProcessRunner"/> ever returns.
///
/// stdout and stderr stay separate: Git writes progress and diagnostics to stderr
/// even when it succeeds (`clone --progress` is the obvious one), so merging the
/// two streams would corrupt every parser in this assembly.
/// </summary>
/// <param name="ExitCode">git.exe's exit code. 0 is success for every command used here.</param>
/// <param name="StdOut">Decoded as UTF-8. Never trimmed — parsers depend on trailing NULs.</param>
/// <param name="StdErr">Decoded as UTF-8. What the user is shown when something fails.</param>
/// <param name="Duration">Wall-clock, measured around the process. Fed to `flick diag timings`.</param>
public sealed record GitResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// The message to show a human: Git's own stderr, falling back to stdout when a
    /// command reported the problem there. Never a generic string — CLAUDE.md,
    /// "Error Handling".
    /// </summary>
    public string ErrorText
    {
        get
        {
            string stderr = StdErr.Trim();
            if (stderr.Length > 0)
                return stderr;

            string stdout = StdOut.Trim();
            return stdout.Length > 0 ? stdout : $"git exited with code {ExitCode} and said nothing.";
        }
    }
}
