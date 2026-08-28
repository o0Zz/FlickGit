namespace FlickGit.Cli;

/// <summary>
/// Process exit codes. A contract, not an implementation detail: scripts and launchers drive the
/// same actions Explorer does, and they can only branch on the number.
///
/// <b>Shared as source, compiled into both executables</b>, exactly as <c>IpcMessages.cs</c> and
/// <c>ShellCommandIds.cs</c> are — and for the same reason. Both processes exit with these numbers:
/// <c>flick.exe</c> when it cannot find or start the app at all, and <c>FlickGit.exe</c> for
/// everything a verb does. This lived in <c>FlickGit.Core</c>, which the AOT stub is not allowed to
/// reference, so the stub carried <c>private const int ExitConfigurationError = 4;</c> — a second
/// hand-maintained copy of one number in a documented contract, which is precisely the drift
/// <c>src/Shared/</c> exists to prevent.
///
/// The namespace stays <c>FlickGit.Cli</c> rather than becoming <c>FlickGit.Shared</c>: this is the
/// command-line grammar, it sits beside <c>Verb</c> and <c>VerbKind</c> conceptually, and every
/// caller in the App already has the using.
/// </summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int GitError = 1;
    public const int NotARepository = 2;
    public const int UserCancelled = 3;
    public const int ConfigurationError = 4;

    /// <summary>Refused for safety: a blocked switch, a diverged push.</summary>
    public const int RefusedForSafety = 5;
}
