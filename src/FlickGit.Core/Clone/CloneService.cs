using System.Text.RegularExpressions;
using FlickGit.Git;
using FlickGit.Logging;
using FlickGit.Models;

namespace FlickGit.Clone;

/// <summary>
/// Cloning, with a determinate progress bar and a cancellation that cleans up after itself.
///
/// Three decisions from CLAUDE.md, "Clone" shape this:
///
/// <list type="bullet">
/// <item><description><b>Always into a subdirectory.</b> `git clone` refuses a non-empty
/// directory, and the right-clicked folder usually is not empty — it is <c>C:\dev</c>. So the
/// target is always <c>&lt;clicked&gt;\&lt;name&gt;</c>.</description></item>
/// <item><description><b>Progress comes from stderr.</b> `clone --progress` writes it there,
/// not to stdout, which is why this is the one caller of
/// <see cref="IGitProcessRunner.RunStreamingAsync"/>.</description></item>
/// <item><description><b>Cancellation deletes the partial directory — but only if this
/// operation created it.</b> "A half-cloned directory that looks like a repository is worse
/// than no directory", and deleting one the user already had would be far worse
/// still.</description></item>
/// </list>
///
/// Authentication is not handled here at all. Git's credential helper does that job, and
/// FlickGit never prompts for a password and never stores one.
/// </summary>
public sealed partial class CloneService(IGitProcessRunner git, ILog log)
{
    /// <summary>
    /// `Receiving objects:  45% (450/1000)` and friends. The phase name is whatever precedes
    /// the colon, so a future Git that adds a phase is reported rather than ignored.
    /// </summary>
    [GeneratedRegex(@"^(?<phase>[A-Za-z][A-Za-z ]+):\s+(?<percent>\d{1,3})%", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressLine();

    /// <summary>
    /// Clones <paramref name="url"/> into a new subdirectory of <paramref name="parentDirectory"/>.
    /// </summary>
    /// <param name="options">Submodules and shallow depth.</param>
    /// <param name="progress">Reported per progress line. May be null.</param>
    public async Task<CloneOutcome> CloneAsync(
        string parentDirectory,
        string url,
        string directoryName,
        CloneOptions options,
        IProgress<CloneProgress>? progress,
        CancellationToken cancellationToken)
    {
        CloneOutcome? rejection = Validate(parentDirectory, url, directoryName);
        if (rejection is not null)
            return rejection;

        string target = Path.Combine(parentDirectory, directoryName);

        //Recorded before the clone starts. It is the only thing that distinguishes "delete the
        //mess we made" from "delete the user's directory" if this is cancelled.
        bool weCreatedTheDirectory = !Directory.Exists(target);

        var args = new List<string> { "clone", "--progress" };

        if (options.ShallowDepth is { } depth && depth > 0)
        {
            args.Add("--depth");
            args.Add(depth.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (options.RecurseSubmodules)
        {
            //Preferred over a separate `submodule update` pass: it clones submodules in parallel
            //with the main history and is meaningfully faster.
            args.Add("--recurse-submodules");
        }

        //`--` before the operands, so a URL or a directory name beginning with a dash cannot be
        //read as an option.
        args.Add("--");
        args.Add(url);
        args.Add(target);

        try
        {
            GitResult result = await git.RunStreamingAsync(
                //No repository yet, so the command runs in the parent directory.
                parentDirectory,
                args,
                line => ReportLine(line, progress),
                cancellationToken).ConfigureAwait(false);

            if (result.Succeeded)
            {
                log.Info($"Cloned into {target}.");
                return new CloneOutcome(true, target, null);
            }

            //A failed clone leaves the same debris a cancelled one does.
            CleanUp(target, weCreatedTheDirectory);

            return new CloneOutcome(false, target, result.ErrorText)
            {
                Suggestion = SuggestionFor(result.ErrorText),
            };
        }
        catch (OperationCanceledException)
        {
            log.Info($"Clone into {target} cancelled.");
            CleanUp(target, weCreatedTheDirectory);
            throw;
        }
    }

    /// <summary>
    /// Everything that can be refused without starting a process.
    /// </summary>
    internal static CloneOutcome? Validate(string parentDirectory, string url, string directoryName)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new CloneOutcome(false, null, "Enter the URL of the repository to clone.");

        if (string.IsNullOrWhiteSpace(directoryName))
            return new CloneOutcome(false, null, "Enter a name for the new directory.");

        if (directoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return new CloneOutcome(false, null, $"'{directoryName}' is not a valid directory name.");

        if (!Directory.Exists(parentDirectory))
            return new CloneOutcome(false, null, $"{parentDirectory} does not exist.");

        string target = Path.Combine(parentDirectory, directoryName);

        //A pre-existing non-empty directory is refused, and the user is told to rename rather
        //than being asked whether to overwrite: `git clone` would fail anyway, and offering to
        //empty a directory the tool did not create is not a button this product has.
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            return new CloneOutcome(false, target,
                $"{target} already exists and is not empty.\n\nChoose a different name.");
        }

        return File.Exists(target)
            ? new CloneOutcome(false, target, $"{target} already exists as a file.")
            : null;
    }

    private static void ReportLine(string line, IProgress<CloneProgress>? progress)
    {
        if (progress is null)
            return;

        Match match = ProgressLine().Match(line.Trim());

        progress.Report(match.Success
            ? new CloneProgress(match.Groups["phase"].Value.Trim(), int.Parse(match.Groups["percent"].Value), line.Trim())

            //Not a progress line: a remote message, a warning, or Git narrating. Passed through
            //with no percentage so the dialog can show it as text without moving the bar.
            : new CloneProgress(null, null, line.Trim()));
    }

    /// <summary>
    /// Removes a partial clone, and only ever one this operation created.
    /// </summary>
    private void CleanUp(string target, bool weCreatedTheDirectory)
    {
        if (!weCreatedTheDirectory)
        {
            //The directory existed before this ran -- it was empty, which is why the clone was
            //allowed, but it was the user's. Leaving whatever is in it is the safe choice.
            log.Info($"Not deleting {target}: it existed before the clone started.");
            return;
        }

        try
        {
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
                log.Info($"Deleted the partial clone at {target}.");
            }
        }
        catch (Exception ex)
        {
            //A file still held by a killed git.exe child can block the delete. Reported, never
            //retried in a loop: the user is better told there is a directory to remove than
            //left watching a spinner.
            log.Warn($"Could not delete the partial clone at {target}: {ex.Message}");
        }
    }

    /// <summary>
    /// A next action for the failures that have one. Authentication is the common case, and
    /// Git's credential manager is the answer rather than anything FlickGit could do.
    /// </summary>
    private static string? SuggestionFor(string gitError)
    {
        if (gitError.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
            || gitError.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
            || gitError.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return "This looks like an authentication failure. Check your credentials with:\n\n" +
                   "git credential-manager github login\n\n" +
                   "FlickGit never asks for or stores a password.";
        }

        return gitError.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? "Check the URL is correct and that you have access to the repository."
            : null;
    }
}

/// <param name="RecurseSubmodules">Default on, matching the dialog's default.</param>
/// <param name="ShallowDepth">Null for a full clone; 1 for `--depth 1`.</param>
public sealed record CloneOptions(bool RecurseSubmodules = true, int? ShallowDepth = null);

/// <param name="Phase">"Receiving objects", "Resolving deltas", … or null for a non-progress line.</param>
/// <param name="Percent">0-100, or null when the line carried no percentage.</param>
/// <param name="Text">The raw line, for the dialog's detail area.</param>
public sealed record CloneProgress(string? Phase, int? Percent, string Text);

/// <param name="Succeeded">The clone completed.</param>
/// <param name="TargetDirectory">Where it went, or would have gone. Null when the input was rejected.</param>
/// <param name="Error">Git's stderr, or the validation message.</param>
public sealed record CloneOutcome(bool Succeeded, string? TargetDirectory, string? Error)
{
    public string? Suggestion { get; init; }
}
