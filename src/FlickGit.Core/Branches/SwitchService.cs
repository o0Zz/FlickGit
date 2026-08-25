using FlickGit.Git;
using FlickGit.Logging;
using FlickGit.Models;
using FlickGit.Repositories;

namespace FlickGit.Branches;

/// <summary>
/// Switching branches, and creating them.
///
/// The most dangerous service in the product, and the rules that make it safe are all negative:
///
/// <list type="bullet">
/// <item><description><b>Try the plain switch first.</b> `git switch` carries uncommitted changes
/// across when there is no conflict.</description></item>
/// <item><description><b>If Git refuses, stop.</b> No stashing, no forcing. The blocking files are
/// reported and the working tree is left byte-identical.</description></item>
/// <item><description><b>The stash path restores only its own stash</b>, located by a unique
/// message and never by index -- `stash pop` with no argument would pop whatever the user had
/// stashed last week.</description></item>
/// </list>
/// </summary>
public sealed class SwitchService(IGitProcessRunner git, RepositoryService repositories, ILog log)
{
    /// <summary>
    /// Prefix of the stash message this service creates. Long and specific on purpose: it is how the
    /// stash is found again, so it must not collide with anything a human would type.
    /// </summary>
    internal const string StashMessagePrefix = "flickgit-switch";

    public async Task<SwitchOutcome> SwitchAsync(
        RepositoryInfo repository,
        string branch,
        CancellationToken cancellationToken)
    {
        GitResult result = await git.RunAsync(
            repository.Root,
            ["switch", branch],
            cancellationToken).ConfigureAwait(false);

        repositories.Invalidate(repository.Root);

        if (result.Succeeded)
            return SwitchOutcome.Success();

        //Refused rather than broken. Git names the files it would have to overwrite, and those are
        //exactly what the user needs to decide what to do next -- nothing has been modified or discarded.
        IReadOnlyList<string> blocking = ParseBlockingFiles(result.ErrorText);

        log.Info($"Switch to {branch} refused; {blocking.Count} blocking file(s).");

        return new SwitchOutcome(
            Succeeded: false,
            BlockingFiles: blocking,
            GitError: result.ErrorText,
            StashRef: null,
            RestoreConflicted: false);
    }

    /// <summary>Switches to a remote-tracking branch, creating a local branch that tracks it.</summary>
    public async Task<SwitchOutcome> SwitchTrackingAsync(
        RepositoryInfo repository,
        string remoteBranch,
        CancellationToken cancellationToken)
    {
        GitResult result = await git.RunAsync(
            repository.Root,
            ["switch", "--track", remoteBranch],
            cancellationToken).ConfigureAwait(false);

        repositories.Invalidate(repository.Root);

        return result.Succeeded
            ? SwitchOutcome.Success()
            : new SwitchOutcome(false, ParseBlockingFiles(result.ErrorText), result.ErrorText, null, false);
    }

    /// <summary>
    /// Creates a branch at the current commit and switches to it. If creation fails the caller must
    /// not commit, which is why this returns an outcome rather than throwing.
    /// </summary>
    public async Task<SwitchOutcome> CreateAsync(
        RepositoryInfo repository,
        string branch,
        CancellationToken cancellationToken)
    {
        //`switch -c`, with no fallback to `checkout -b`. Git 2.23 is the stated minimum, so the older
        //spelling would be a second code path for a Git nobody runs.
        GitResult result = await git.RunAsync(
            repository.Root,
            ["switch", "-c", branch],
            cancellationToken).ConfigureAwait(false);

        repositories.Invalidate(repository.Root);

        return result.Succeeded
            ? SwitchOutcome.Success()
            : new SwitchOutcome(false, ParseBlockingFiles(result.ErrorText), result.ErrorText, null, false);
    }

    /// <summary>
    /// Stash, switch, restore -- the explicit second choice after a refused switch. Every step is
    /// checked, and the stash is only ever restored by the reference this method created. If the
    /// restore conflicts, it says so and says where the stash still is.
    /// </summary>
    public async Task<SwitchOutcome> StashSwitchRestoreAsync(
        RepositoryInfo repository,
        string branch,
        CancellationToken cancellationToken)
    {
        //Unique per attempt. This string is the only handle on the stash, so it carries a GUID rather
        //than a timestamp -- two attempts in the same second must not find each other's stash.
        string message = $"{StashMessagePrefix} {Guid.NewGuid():N}";

        GitResult stash = await git.RunAsync(
            repository.Root,
            ["stash", "push", "--include-untracked", "-m", message],
            cancellationToken).ConfigureAwait(false);

        if (!stash.Succeeded)
        {
            return new SwitchOutcome(false, [], stash.ErrorText, null, false)
            {
                FailedStep = SwitchStep.Stash,
            };
        }

        //"No local changes to save" is a success with nothing stashed. Looking the reference up rather
        //than assuming stash@{0} is what makes that case safe: there is nothing of ours to restore, and
        //popping stash@{0} would take somebody else's.
        string? stashRef = await FindStashAsync(repository, message, cancellationToken).ConfigureAwait(false);

        SwitchOutcome switched = await SwitchAsync(repository, branch, cancellationToken).ConfigureAwait(false);

        if (!switched.Succeeded)
        {
            //The switch still failed, for some reason other than local changes. Put the work back before
            //reporting, so the user is where they started.
            if (stashRef is not null)
                await RestoreAsync(repository, stashRef, cancellationToken).ConfigureAwait(false);

            return switched with { FailedStep = SwitchStep.Switch };
        }

        if (stashRef is null)
        {
            return SwitchOutcome.Success();
        }

        GitResult restore = await RestoreAsync(repository, stashRef, cancellationToken).ConfigureAwait(false);
        repositories.Invalidate(repository.Root);

        if (restore.Succeeded)
            return SwitchOutcome.Success();

        //On the new branch, with the work still in a stash. Reported as a distinct state because the
        //recovery is a command the user has to run, and it needs the reference.
        log.Warn($"Stash restore conflicted after switching to {branch}; stash kept at {stashRef}.");

        return new SwitchOutcome(
            Succeeded: false,
            BlockingFiles: [],
            GitError: restore.ErrorText,
            StashRef: stashRef,
            RestoreConflicted: true)
        {
            FailedStep = SwitchStep.Restore,
        };
    }

    /// <summary>Local branches and remote-tracking branches, for the switch picker.</summary>
    public async Task<SwitchCandidates> ListCandidatesAsync(
        RepositoryInfo repository,
        CancellationToken cancellationToken)
    {
        //One invocation for both ref namespaces, with the kind included so the two can be told apart
        //without a second call or a name-prefix guess.
        GitResult result = await git.ReadAsync(
            repository.Root,
            ["for-each-ref", "--format=%(refname:short)%09%(refname)", "refs/heads", "refs/remotes"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
            return new SwitchCandidates([], []);

        var local = new List<string>();
        var remote = new List<string>();

        foreach (string line in result.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Trim().Split('\t');
            if (parts.Length < 2 || parts[0].Length == 0)
                continue;

            if (parts[1].StartsWith("refs/heads/", StringComparison.Ordinal))
                local.Add(parts[0]);

            //origin/HEAD is a symbolic ref, not a branch anyone switches to. Offering it would produce a
            //detached HEAD on whatever it points at.
            else if (!parts[0].EndsWith("/HEAD", StringComparison.Ordinal))
                remote.Add(parts[0]);
        }

        local.Sort(StringComparer.OrdinalIgnoreCase);
        remote.Sort(StringComparer.OrdinalIgnoreCase);

        return new SwitchCandidates(local, remote);
    }

    private Task<GitResult> RestoreAsync(RepositoryInfo repository, string stashRef, CancellationToken cancellationToken) =>
        //`pop`, not `apply`: on success the stash should be gone. On failure Git keeps it, which is what
        //makes the conflict path recoverable.
        git.RunAsync(repository.Root, ["stash", "pop", stashRef], cancellationToken);

    /// <summary>
    /// Finds the stash this service just created, by its message. <b>Never by index.</b>
    /// <c>stash@{0}</c> is whatever was stashed most recently, which on a busy working tree is not
    /// necessarily ours -- and restoring the wrong stash is indistinguishable from losing the user's
    /// work.
    /// </summary>
    private async Task<string?> FindStashAsync(
        RepositoryInfo repository,
        string message,
        CancellationToken cancellationToken)
    {
        GitResult list = await git.ReadAsync(
            repository.Root,
            ["stash", "list", "--format=%gd%x09%gs"],
            cancellationToken).ConfigureAwait(false);

        if (!list.Succeeded)
            return null;

        foreach (string line in list.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\t', 2);
            if (parts.Length == 2 && parts[1].Contains(message, StringComparison.Ordinal))
                return parts[0].Trim();
        }

        return null;
    }

    /// <summary>
    /// The files Git named as blocking the switch. Git lists them one per line, tab-indented. The tab
    /// is the marker rather than the heading text, because the wording differs between `switch`,
    /// `checkout` and `merge` and between Git versions, while the indent has not changed.
    /// </summary>
    internal static IReadOnlyList<string> ParseBlockingFiles(string stderr)
    {
        var files = new List<string>();

        foreach (string raw in stderr.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            if (line.Length == 0 || (line[0] != '\t' && !line.StartsWith("        ", StringComparison.Ordinal)))
                continue;

            string path = line.Trim();

            //Git ends the list with hint lines that are also indented. They are sentences, not paths, so they
            //are dropped by the only reliable tell available: a trailing period.
            if (path.Length == 0 || path.EndsWith('.') || path.EndsWith(':'))
                continue;

            files.Add(path);
        }

        return files;
    }
}

/// <summary>Which step of a multi-step switch failed. Null when nothing failed.</summary>
public enum SwitchStep
{
    None,
    Stash,
    Switch,
    Restore,
}

/// <param name="BlockingFiles">Files Git said would be overwritten. Empty unless it refused.</param>
/// <param name="StashRef">
/// Set when a stash this service created still exists -- always shown to the user, because it is
/// where their work is.
/// </param>
/// <param name="RestoreConflicted">The switch happened but the stash could not be reapplied.</param>
public sealed record SwitchOutcome(
    bool Succeeded,
    IReadOnlyList<string> BlockingFiles,
    string? GitError,
    string? StashRef,
    bool RestoreConflicted)
{
    public SwitchStep FailedStep { get; init; }

    /// <summary>
    /// True when Git refused because of local changes, so offering the stash path is appropriate. A
    /// refusal with no named files is a different failure and must not lead the user to that button.
    /// </summary>
    public bool RefusedByLocalChanges => !Succeeded && BlockingFiles.Count > 0;

    public static SwitchOutcome Success() => new(true, [], null, null, false);
}

public sealed record SwitchCandidates(IReadOnlyList<string> Local, IReadOnlyList<string> Remote);
