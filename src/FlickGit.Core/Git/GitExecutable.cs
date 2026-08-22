using FlickGit.Logging;

namespace FlickGit.Git;

/// <summary>
/// Finds git.exe once and remembers it.
///
/// Resolution order, per CLAUDE.md, "Git Command Execution": the user's setting, then
/// PATH, then the standard install locations. The result is cached for the life of the
/// process — a resident service that probed the file system on every Git call would
/// spend more time looking for git.exe than running it.
///
/// A missing git.exe surfaces as one clear error from <see cref="Path"/>, not as a
/// mystery failure per command.
/// </summary>
public sealed class GitExecutable
{
    private readonly string? _configuredPath;
    private readonly ILog _log;
    private readonly Lazy<string?> _resolved;

    /// <summary>
    /// Where Git for Windows and the common portable layouts put it. Ordered:
    /// 64-bit system install, 32-bit, per-user install, Scoop, then the two
    /// portable roots people actually use.
    /// </summary>
    private static readonly string[] WellKnownRelativePaths =
    [
        @"Git\cmd\git.exe",
        @"Git\bin\git.exe",
    ];

    public GitExecutable(string? configuredPath, ILog log)
    {
        _configuredPath = string.IsNullOrWhiteSpace(configuredPath) ? null : configuredPath.Trim();
        _log = log;
        _resolved = new Lazy<string?>(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>True when git.exe was found. Every surface checks this before offering an action.</summary>
    public bool IsAvailable => _resolved.Value is not null;

    /// <summary>
    /// The full path to git.exe.
    /// </summary>
    /// <exception cref="GitNotFoundException">git.exe could not be located.</exception>
    public string Path => _resolved.Value ?? throw new GitNotFoundException(_configuredPath);

    private string? Resolve()
    {
        //An explicit setting is honoured even if it is wrong: silently falling back to
        //a different git.exe than the one the user named would be worse than failing.
        if (_configuredPath is not null)
        {
            if (File.Exists(_configuredPath))
            {
                _log.Info($"git.exe from settings: {_configuredPath}");
                return _configuredPath;
            }

            _log.Warn($"Configured git.exe does not exist: {_configuredPath}");
            return null;
        }

        foreach (string candidate in EnumerateCandidates())
        {
            if (!File.Exists(candidate))
                continue;

            _log.Info($"git.exe resolved to {candidate}");
            return candidate;
        }

        _log.Warn("git.exe was not found on PATH or in any standard install location.");
        return null;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        //PATH first. This is what the user's own shell would run, so it is the least
        //surprising answer, and on a machine with several Gits installed it is the one
        //whose credential helper and config the user has already set up.
        string? pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (pathVariable is not null)
        {
            foreach (string directory in pathVariable.Split(System.IO.Path.PathSeparator))
            {
                if (directory.Length == 0)
                    continue;

                string trimmed = directory.Trim('"');
                if (trimmed.Length == 0 || trimmed.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0)
                    continue;

                string candidate;
                try
                {
                    candidate = System.IO.Path.Combine(trimmed, "git.exe");
                }
                catch (ArgumentException)
                {
                    //A malformed PATH entry is not a reason to stop looking.
                    continue;
                }

                yield return candidate;
            }
        }

        foreach (Environment.SpecialFolder folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.LocalApplicationData,
                 })
        {
            string root = Environment.GetFolderPath(folder);
            if (root.Length == 0)
                continue;

            foreach (string relative in WellKnownRelativePaths)
                yield return System.IO.Path.Combine(root, relative);
        }
    }
}

/// <summary>
/// git.exe is not installed, or the configured path is wrong. Carries enough to tell
/// the user what to do about it, which is the whole contract of CLAUDE.md,
/// "Error Handling".
/// </summary>
public sealed class GitNotFoundException(string? configuredPath) : Exception(BuildMessage(configuredPath))
{
    private static string BuildMessage(string? configuredPath) =>
        configuredPath is null
            ? "git.exe was not found on PATH or in any standard install location.\n\n" +
              "Install Git for Windows, or set the path to git.exe in FlickGit settings."
            : $"git.exe was not found at the configured path:\n\n{configuredPath}\n\n" +
              "Correct it in FlickGit settings, or clear it to search PATH instead.";
}
