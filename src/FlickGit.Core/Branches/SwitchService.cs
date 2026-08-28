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

    public Task<SwitchOutcome> SwitchAsync(
        RepositoryInfo repository,
        string branch,
        CancellationToken cancellationToken) =>
        RunSwitchAsync(repository, ["switch", branch], branch, cancellationToken);

    /// <summary>
    /// Checks a revision out by name -- a tag, from the tag window -- leaving HEAD detached.
    ///
    /// <b>The only place in the product that detaches HEAD on purpose.</b> Everywhere else it is a
    /// state to be reported and refused: <see cref="ListCandidatesAsync"/> drops <c>origin/HEAD</c>
    /// rather than offer a row that would produce one, and both <c>PushService</c> and
    /// <c>PullRequestService</c> stop when they find one. So the surface that reaches this asks
    /// first, in words that say what the state is and how to leave it.
    ///
    /// <c>switch --detach</c> rather than <c>checkout</c>: Git 2.23 is the stated minimum, and the
    /// older spelling would be a second way to say the same thing.
    /// </summary>
    public Task<SwitchOutcome> DetachAsync(
        RepositoryInfo repository,
        string revision,
        CancellationToken cancellationToken) =>
        RunSwitchAsync(repository, ["switch", "--detach", revision], revision, cancellationToken);

    /// <summary>
    /// One `git switch`, and what to make of its answer. Shared by the two entry points above rather
    /// than copied, so a refusal cannot come to mean two different things depending on which of them
    /// was called.
    /// </summary>
    /// <param name="target">The branch or revision, for the log line only.</param>
    private async Task<SwitchOutcome> RunSwitchAsync(
        RepositoryInfo repository,
        IReadOnlyList<string> args,
        string target,
        CancellationToken cancellationToken)
    {
        GitResult result = await git.RunAsync(
            repository.Root,
            args,
            cancellationToken).ConfigureAwait(false);

        repositories.Invalidate(repository.Root);

        if (result.Succeeded)
            return SwitchOutcome.Success();

        //Refused rather than broken. Git names the files it would have to overwrite, and those are
        //exactly what the user needs to decide what to do next -- nothing has been modified or discarded.
        IReadOnlyList<string> blocking = ParseBlockingFiles(result.ErrorText);

        log.Info($"Switch to {target} refused; {blocking.Count} blocking file(s).");

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
        StashLookup found = await FindStashAsync(repository, message, cancellationToken).ConfigureAwait(false);

        if (!found.Read)
        {
            //`stash push` worked and `stash list` did not, so nothing here can name what was just put
            //away -- and switching now would leave no way to put it back. Stop before the switch, and
            //say what to look for: the message is the only handle left on the stash.
            log.Warn($"stash list failed after stashing for a switch to {branch}; the stash message contains {message}.");

            return new SwitchOutcome(
                Succeeded: false,
                BlockingFiles: [],
                GitError:
                    $"{found.Error}\n\n" +
                    $"Your changes were stashed and this could not read the stash list, so the switch " +
                    $"was not attempted. The stash is still there, with a message containing:\n{message}",
                StashRef: null,
                RestoreConflicted: false)
            {
                FailedStep = SwitchStep.Stash,
            };
        }

        string? stashRef = found.Reference;

        SwitchOutcome switched = await SwitchAsync(repository, branch, cancellationToken).ConfigureAwait(false);

        if (!switched.Succeeded)
        {
            //The switch still failed, for some reason other than local changes. Put the work back before
            //reporting, so the user is where they started.
            if (stashRef is not null)
            {
                GitResult putBack = await RestoreAsync(repository, stashRef, cancellationToken).ConfigureAwait(false);
                repositories.Invalidate(repository.Root);

                if (!putBack.Succeeded)
                {
                    //Two failures, and this is the one that has to lead. A refused switch leaves the user
                    //where they were; a stash nobody named leaves them looking at an emptied working tree
                    //with no idea where their work went. The reference is the actionable part, and the
                    //outcome the switch produced carries a null one -- so it is set here, or the window
                    //shows the switch error over an empty tree and says nothing about the stash.
                    log.Warn($"Stash restore failed after a refused switch to {branch}; stash kept at {stashRef}.");

                    return switched with
                    {
                        GitError = $"{switched.GitError}\n\n{putBack.ErrorText}",
                        StashRef = stashRef,
                        RestoreConflicted = true,
                        FailedStep = SwitchStep.Restore,
                    };
                }
            }

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
    private async Task<StashLookup> FindStashAsync(
        RepositoryInfo repository,
        string message,
        CancellationToken cancellationToken)
    {
        GitResult list = await git.ReadAsync(
            repository.Root,
            ["stash", "list", "--format=%gd%x09%gs"],
            cancellationToken).ConfigureAwait(false);

        //A failed read is not "nothing was stashed". Collapsing the two into one null is how a stash
        //that exists gets reported as plain success, so the caller is told which it was.
        if (!list.Succeeded)
            return new StashLookup(Read: false, Reference: null, Error: list.ErrorText);

        foreach (string line in list.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('\t', 2);
            if (parts.Length == 2 && parts[1].Contains(message, StringComparison.Ordinal))
                return new StashLookup(Read: true, Reference: parts[0].Trim(), Error: string.Empty);
        }

        //Read, and ours is not in it: `stash push` answered "No local changes to save".
        return new StashLookup(Read: true, Reference: null, Error: string.Empty);
    }

    /// <summary>
    /// The answer to "where is the stash this service just made", with <b>"the list could not be
    /// read" kept separate from "there is no stash"</b> -- the two states a bare <c>string?</c>
    /// cannot tell apart, and whose consequences are opposite.
    /// </summary>
    private readonly record struct StashLookup(bool Read, string? Reference, string Error);

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
