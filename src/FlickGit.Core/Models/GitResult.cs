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
    /// <summary>
    /// The same outcome with stdout left as the bytes Git wrote, for the one read where decoding it
    /// would destroy the answer: a blob out of the object store.
    ///
    /// <c>git show HEAD:&lt;path&gt;</c> hands back the file exactly as it was committed, and that file
    /// may be UTF-16, or Latin-1, or carry a BOM. Decoded as UTF-8 it comes back full of U+FFFD, and
    /// no amount of detection afterwards can undo that -- so this read never decodes at all and hands
    /// the bytes straight to the same detector the working copy goes through.
    /// </summary>
    /// <param name="StdOut">Git's stdout, undecoded.</param>
    public sealed record Bytes(int ExitCode, byte[] StdOut, string StdErr, TimeSpan Duration)
    {
        public bool Succeeded => ExitCode == 0;
    }

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
