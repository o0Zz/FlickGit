using FlickGit.Logging;

namespace FlickGit.Git;

/// <summary>
/// Finds the git executable once and remembers it.
///
/// Resolution order, per CLAUDE.md, "Git Command Execution": the user's setting, then
/// PATH, then the standard install locations. The result is cached for the life of the
/// process — a resident service that probed the file system on every Git call would
/// spend more time looking for Git than running it.
///
/// A missing Git surfaces as one clear error from <see cref="Path"/>, not as a
/// mystery failure per command.
/// </summary>
public sealed class GitExecutable
{
    private readonly string? _configuredPath;
    private readonly ILog _log;
    private readonly Lazy<string?> _resolved;

    /// <summary>
    /// The file name to look for. Windows needs the extension and Unix must not have it: a
    /// candidate of "git.exe" matches nothing on any PATH entry there, which would leave Git
    /// undiscoverable on a machine that has it installed and working.
    ///
    /// Internal because <see cref="GitNotFoundException"/> names it in the message it builds.
    /// </summary>
    internal static string ExecutableName { get; } = OperatingSystem.IsWindows() ? "git.exe" : "git";

    /// <summary>
    /// Where Git for Windows and the common portable layouts put it, relative to a shell folder.
    /// Ordered: 64-bit system install, 32-bit, per-user install.
    /// </summary>
    private static readonly string[] WindowsRelativePaths =
    [
        @"Git\cmd\git.exe",
        @"Git\bin\git.exe",
    ];

    /// <summary>
    /// Where Git is when it is not on PATH on a Unix machine. Absolute rather than relative to a
    /// shell folder, because the folders that scheme relies on -- ProgramFiles and its 32-bit
    /// sibling -- come back empty off Windows. Ordered: Homebrew on Apple silicon, Homebrew on
    /// Intel, the ordinary system location, then the Xcode command line tools' shim.
    /// </summary>
    private static readonly string[] UnixPaths =
    [
        "/opt/homebrew/bin/git",
        "/usr/local/bin/git",
        "/usr/bin/git",
        "/Library/Developer/CommandLineTools/usr/bin/git",
    ];

    public GitExecutable(string? configuredPath, ILog log)
    {
        _configuredPath = string.IsNullOrWhiteSpace(configuredPath) ? null : configuredPath.Trim();
        _log = log;
        _resolved = new Lazy<string?>(Resolve, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>True when Git was found. Every surface checks this before offering an action.</summary>
    public bool IsAvailable => _resolved.Value is not null;

    /// <summary>
    /// The full path to the git executable.
    /// </summary>
    /// <exception cref="GitNotFoundException">Git could not be located.</exception>
    public string Path => _resolved.Value ?? throw new GitNotFoundException(_configuredPath);

    private string? Resolve()
    {
        //An explicit setting is honoured even if it is wrong: silently falling back to
        //a different Git than the one the user named would be worse than failing.
        if (_configuredPath is not null)
        {
            if (File.Exists(_configuredPath))
            {
                _log.Info($"{ExecutableName} from settings: {_configuredPath}");
                return _configuredPath;
            }

            _log.Warn($"Configured {ExecutableName} does not exist: {_configuredPath}");
            return null;
        }

        foreach (string candidate in EnumerateCandidates())
        {
            if (!File.Exists(candidate))
                continue;

            _log.Info($"{ExecutableName} resolved to {candidate}");
            return candidate;
        }

        _log.Warn($"{ExecutableName} was not found on PATH or in any standard install location.");
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
                    candidate = System.IO.Path.Combine(trimmed, ExecutableName);
                }
                catch (ArgumentException)
                {
                    //A malformed PATH entry is not a reason to stop looking.
                    continue;
                }

                yield return candidate;
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            foreach (string absolute in UnixPaths)
                yield return absolute;

            yield break;
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

            foreach (string relative in WindowsRelativePaths)
                yield return System.IO.Path.Combine(root, relative);
        }
    }
}

/// <summary>
/// Git is not installed, or the configured path is wrong. Carries enough to tell
/// the user what to do about it, which is the whole contract of CLAUDE.md,
/// "Error Handling".
/// </summary>
public sealed class GitNotFoundException(string? configuredPath) : Exception(BuildMessage(configuredPath))
{
    private static string InstallHint => OperatingSystem.IsWindows()
        ? "Install Git for Windows, or set the path to git.exe in FlickGit settings."
        : "Install Git -- `xcode-select --install` or `brew install git` -- or set the path to git in FlickGit settings.";

    private static string BuildMessage(string? configuredPath) =>
        configuredPath is null
            ? $"{GitExecutable.ExecutableName} was not found on PATH or in any standard install location.\n\n" +
              InstallHint
            : $"{GitExecutable.ExecutableName} was not found at the configured path:\n\n{configuredPath}\n\n" +
              "Correct it in FlickGit settings, or clear it to search PATH instead.";
}
