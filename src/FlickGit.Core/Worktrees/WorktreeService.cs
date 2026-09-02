using FlickGit.Git;
using FlickGit.Models;
using FlickGit.Repositories;

namespace FlickGit.Worktrees;

/// <summary>
/// Listing, adding and removing worktrees -- one repository's several checkouts.
///
/// A worktree is not a ref, so this is not a fourth picker beside Branches and Tags. It is an
/// operation <i>on</i> a branch, which is why it lives on the Branches window's rows: Git allows
/// at most one worktree per branch, so a branch row is the natural and only index.
///
/// <b>The safety rules here are all refusals, and every one of them happens before Git is
/// asked.</b> The target has to be an absolute path outside the repository's own working tree,
/// removal never escalates to <c>--force</c> on its own, and a locked worktree is left alone.
/// </summary>
public sealed class WorktreeService(IGitProcessRunner git, RepositoryService repositories)
{
    /// <summary>Every worktree of this repository, main one first.</summary>
    public async Task<IReadOnlyList<GitWorktree>> ListAsync(
        RepositoryInfo repository,
        CancellationToken cancellationToken)
    {
        //--porcelain without -z: `worktree list -z` arrived in Git 2.36 and CLAUDE.md's stated minimum
        //is 2.23. The format is one `key SP value` per line with the value running to the end, so a path
        //containing spaces parses correctly anyway -- and a path containing a newline is not a thing
        //Windows can produce.
        GitResult result = await git.ReadAsync(
            repository.Root,
            ["worktree", "list", "--porcelain"],
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded ? ParseList(result.StdOut) : [];
    }

    /// <summary>
    /// Creates a worktree at <paramref name="path"/>.
    ///
    /// Not confirmed, and it needs no confirmation: nothing existing is touched. It creates a
    /// directory and checks a branch out into it, which is the one Git operation in this file that
    /// cannot lose anything.
    /// </summary>
    public async Task<WorktreeOutcome> AddAsync(
        RepositoryInfo repository,
        string path,
        WorktreeStart start,
        CancellationToken cancellationToken)
    {
        if (CheckTarget(repository.Root, path) is { } refusal)
            return WorktreeOutcome.Refused(refusal);

        //No `--` separator: `git worktree add` does not document one, and neither argument could be read
        //as an option -- the path is absolute, so it begins with a drive letter, and a branch name
        //beginning with a dash is refused by both validators in BranchService.
        var args = new List<string> { "worktree", "add" };

        if (start.NewBranch is { Length: > 0 } created)
        {
            //--track before -b, and the start point last: `worktree add [--track] [-b <new>] <path>
            //[<commit-ish>]`. Without --track a worktree made from a remote row would get a branch with no
            //upstream, so its first push would ask to create one that already exists.
            if (start.StartPoint is { Length: > 0 })
                args.Add("--track");

            args.Add("-b");
            args.Add(created);
            args.Add(path);

            if (start.StartPoint is { Length: > 0 } from)
                args.Add(from);
        }
        else if (start.Branch is { Length: > 0 } existing)
        {
            args.Add(path);
            args.Add(existing);
        }
        else
        {
            //Neither spelling. A detached worktree is a thing Git can make and nothing here asks for, so
            //this is a caller bug rather than a user choice.
            return WorktreeOutcome.Refused(WorktreeRefusal.None);
        }

        GitResult result = await git.RunAsync(repository.Root, args, cancellationToken).ConfigureAwait(false);

        repositories.Invalidate(repository.Root);

        return result.Succeeded
            ? WorktreeOutcome.Ok
            : new WorktreeOutcome(false, result.ErrorText);
    }

    /// <summary>
    /// Removes a worktree: Git's bookkeeping entry and the directory both.
    ///
    /// <b>There is no <c>force</c> parameter, and <c>--force</c> appears nowhere in this file.</b>
    /// That is the one place this feature departs from the shape <c>branch -d</c> / <c>-D</c> has, and
    /// the reason is the difference between the two: forcing a branch deletion leaves the commits in
    /// the reflog, while <c>worktree remove --force</c> deletes a directory of modified and untracked
    /// files outright -- not to the Recycle Bin, not recoverable by <c>git restore</c>, because Git has
    /// never seen them. CLAUDE.md, "Safety Rules": <i>never discard uncommitted work</i>, which is
    /// unconditional and has no second-question exemption.
    ///
    /// So a dirty worktree is refused, and the refusal comes back as
    /// <see cref="WorktreeOutcome.HasLocalChanges"/> so the caller can name the two ways out that do
    /// not destroy anything: commit the work, or delete the folder in Explorer, where it goes to the
    /// Recycle Bin. <c>git worktree remove --force</c> stays a thing the user types themselves.
    /// </summary>
    public async Task<WorktreeOutcome> RemoveAsync(
        RepositoryInfo repository,
        GitWorktree worktree,
        CancellationToken cancellationToken)
    {
        //Both refused before any command runs. Git refuses them too, but its wording is about its own
        //bookkeeping rather than about what the user should do.
        if (worktree.IsMain)
            return WorktreeOutcome.Refused(WorktreeRefusal.IsMainWorktree);

        if (worktree.IsLocked)
            return WorktreeOutcome.Refused(WorktreeRefusal.IsLocked);

        GitResult result = await git.RunAsync(
            repository.Root,
            ["worktree", "remove", worktree.Path],
            cancellationToken).ConfigureAwait(false);

        repositories.Invalidate(repository.Root);

        if (result.Succeeded)
            return WorktreeOutcome.Ok;

        //Matched on the phrase rather than the exit code, which is 1 for every failure `worktree remove`
        //has. Git says "contains modified or untracked files, use --force to delete it".
        bool dirty = result.ErrorText.Contains("modified or untracked", StringComparison.OrdinalIgnoreCase)
                     || result.ErrorText.Contains("use --force", StringComparison.OrdinalIgnoreCase);

        return new WorktreeOutcome(false, result.ErrorText, WorktreeRefusal.None, dirty);
    }

    /// <summary>
    /// Drops bookkeeping for worktrees whose directory is gone.
    ///
    /// <b>The one operation here that fixes a state the user cannot otherwise get out of.</b> Deleting
    /// a worktree folder in Explorer leaves Git still believing its branch is checked out, so every
    /// later attempt to switch to that branch is refused with a message naming a directory that does
    /// not exist. Nothing is destroyed: a worktree that still exists on disk is not pruned, whatever
    /// its state.
    /// </summary>
    public async Task<WorktreeOutcome> PruneAsync(RepositoryInfo repository, CancellationToken cancellationToken)
    {
        GitResult result = await git.RunAsync(
            repository.Root,
            ["worktree", "prune"],
            cancellationToken).ConfigureAwait(false);

        repositories.Invalidate(repository.Root);

        return result.Succeeded
            ? WorktreeOutcome.Ok
            : new WorktreeOutcome(false, result.ErrorText);
    }

    /// <summary>
    /// Why this path may not become a worktree, or null when it may.
    ///
    /// <list type="bullet">
    /// <item><description><b>Absolute only.</b> Every Git call in the product runs
    /// <c>git -C &lt;root&gt;</c>, so a relative path would resolve against the repository root --
    /// producing exactly the nested worktree the next rule refuses, from a value that looked like it
    /// pointed elsewhere.</description></item>
    /// <item><description><b>Never inside the repository's working tree.</b> A worktree nested in
    /// another shows up as a directory full of untracked files in the outer one's status, which means
    /// it can be swept away by a `clean` and offered for staging by us.</description></item>
    /// <item><description><b>Never an existing non-empty directory.</b> Git refuses this, but its
    /// message is about its own checkout; ours can say to pick another name.</description></item>
    /// </list>
    /// </summary>
    public static WorktreeRefusal? CheckTarget(string repositoryRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
            return WorktreeRefusal.NotAbsolute;

        if (IsInside(repositoryRoot, path))
            return WorktreeRefusal.InsideRepository;

        try
        {
            //Exists and holds something. An empty directory is fine -- `worktree add` accepts one, and a
            //user who made the folder in the dialog before selecting it would otherwise be refused for
            //having done nothing wrong.
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                return WorktreeRefusal.NotEmpty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            //Cannot be enumerated. Let Git try and report in its own words rather than refusing a path
            //that might be perfectly usable.
        }

        return null;
    }

    /// <summary>
    /// The folder name to suggest for a worktree of <paramref name="branch"/>.
    ///
    /// The repository name and the branch, with the branch's slashes flattened -- so
    /// <c>feature/storage-gw</c> of <c>d360-portal</c> suggests <c>d360-portal-feature-storage-gw</c>.
    /// Flattened whole rather than reduced to the last segment: <c>fix/pool</c> and <c>feature/pool</c>
    /// would otherwise suggest the same directory, and the second attempt would be refused for a
    /// reason that reads like a bug.
    /// </summary>
    public static string SuggestFolderName(string repositoryName, string branch)
    {
        string flattened = new(branch
            .Trim()
            .Select(c => c is '/' or '\\' or ':' or ' ' ? '-' : c)
            .ToArray());

        flattened = flattened.Trim('-');

        return flattened.Length == 0 ? repositoryName : $"{repositoryName}-{flattened}";
    }

    /// <summary>
    /// True when <paramref name="path"/> is the repository root or below it.
    ///
    /// The separator is appended before comparing, or <c>C:\repo2</c> would count as inside
    /// <c>C:\repo</c> -- the same trap <c>WorkingTreeWriter.ResolveInsideRepository</c> guards, and
    /// the reason this is not a bare <c>StartsWith</c>.
    ///
    /// Case-insensitive on every platform, and deliberately not <c>PathComparison</c>. This one
    /// answers a question whose <i>yes</i> is a refusal, so comparing case-insensitively can only
    /// ever refuse a superset -- safe. <c>PathComparison</c> is for the inverse question, where a
    /// <i>yes</i> grants a write and case-insensitivity would widen what is allowed, and for
    /// identity, where two spellings on a case-sensitive volume are two different directories.
    /// </summary>
    internal static bool IsInside(string repositoryRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot))
            return false;

        try
        {
            string root = Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar);
            string candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

            if (string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase))
                return true;

            return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            //A path Windows will not resolve. Treated as inside, because refusing is the safe answer for a
            //value nothing can reason about.
            return true;
        }
    }

    /// <summary>
    /// <c>worktree list --porcelain</c>: records separated by a blank line, each a set of
    /// <c>key SP value</c> lines where <c>bare</c>, <c>detached</c> and a bare <c>locked</c> carry no
    /// value at all.
    ///
    /// Two things this has to get right:
    /// <list type="bullet">
    /// <item><description><b>The first record is the main worktree.</b> Git documents that ordering,
    /// and it is the only way to tell -- nothing in the record itself says so.</description></item>
    /// <item><description><b>The value runs to the end of the line.</b> Split at the first space only:
    /// <c>locked</c> carries a free-text reason and a path carries spaces, and splitting on every space
    /// would truncate both.</description></item>
    /// </list>
    /// </summary>
    internal static IReadOnlyList<GitWorktree> ParseList(string stdout)
    {
        var worktrees = new List<GitWorktree>();

        string? path = null;
        string? branch = null;
        bool locked = false;
        bool prunable = false;

        void Flush()
        {
            if (path is null)
                return;

            worktrees.Add(new GitWorktree(
                Path: RepositoryService.NormaliseRoot(path),
                Branch: branch,

                //Position, not content. Git emits the main worktree first.
                IsMain: worktrees.Count == 0,
                IsLocked: locked,
                IsPrunable: prunable));

            path = null;
            branch = null;
            locked = false;
            prunable = false;
        }

        foreach (string raw in stdout.Split('\n'))
        {
            string line = raw.TrimEnd('\r');

            //The blank line between records. A record is also closed by the end of the stream, which is why
            //Flush is called again below rather than only here.
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            int space = line.IndexOf(' ');
            string key = space < 0 ? line : line[..space];
            string value = space < 0 ? string.Empty : line[(space + 1)..];

            switch (key)
            {
                case "worktree":
                    //A new record without a blank line before it should not happen, but flushing here means a
                    //malformed stream loses one record rather than merging two.
                    Flush();
                    path = value;
                    break;

                case "branch":
                    //refs/heads/feature/x -> feature/x. The prefix is fixed: `worktree list` reports a branch
                    //only when HEAD is a symbolic ref into refs/heads.
                    branch = value.StartsWith("refs/heads/", StringComparison.Ordinal)
                        ? value["refs/heads/".Length..]
                        : value;
                    break;

                //`bare`, `detached` and `HEAD` are read and dropped: a bare or detached worktree is
                //recognised by having no `branch`, which is the only thing anything asks about one.
                case "locked":
                    locked = true;
                    break;

                case "prunable":
                    prunable = true;
                    break;
            }
        }

        Flush();

        return worktrees;
    }
}
