using FlickGit.Config;
using FlickGit.Diff;
using FlickGit.Git;
using FlickGit.Models;
using FlickGit.Repositories;

namespace FlickGit.Submodules;

/// <summary>
/// Listing, adding, initialising and removing submodules.
///
/// <b>Nothing here commits.</b> <c>submodule add</c> and <c>git rm</c> both leave their work in the
/// index, and that is where this stops -- the commit window is the product's only commit surface, so
/// the caller reports "staged, not committed" and hands off to it rather than growing a second one.
///
/// <b>And nothing here parses <c>git submodule status</c>.</b> It has no <c>--porcelain</c>, its
/// output is the form shaped for a terminal, and CLAUDE.md forbids reading that. What the window
/// needs comes from two machine-readable reads instead -- <c>.gitmodules</c> through the same
/// <c>config --list -z</c> parser the repository's own config goes through, and one
/// <c>diff --name-only -z</c> for which of them have moved.
/// </summary>
public sealed class SubmoduleService(IGitProcessRunner git, RepositoryService repositories)
{
    /// <summary>
    /// Every submodule <c>.gitmodules</c> declares, in the order it declares them, with the two
    /// facts the window shows beyond name and URL: whether it is checked out, and whether it has
    /// moved since HEAD.
    ///
    /// <c>.gitmodules</c> rather than the index, because it is the only source that lists a
    /// submodule nobody has initialised yet -- which is the row this window most needs to show, since
    /// it is the one with something to do.
    /// </summary>
    public async Task<IReadOnlyList<GitSubmodule>> ListAsync(
        RepositoryInfo repository,
        CancellationToken cancellationToken)
    {
        //-f resolves against the working directory, which GitProcessRunner sets to the repository root
        //for the same reason it passes `-C` -- so the relative name is the root's own .gitmodules.
        //A repository with no .gitmodules exits non-zero, which is an empty list rather than a failure.
        GitResult declared = await git.ReadAsync(
            repository.Root,
            ["config", "-f", ".gitmodules", "--list", "-z"],
            cancellationToken).ConfigureAwait(false);

        if (!declared.Succeeded)
            return [];

        IReadOnlyList<DeclaredSubmodule> modules = ParseModules(declared.StdOut);

        if (modules.Count == 0)
            return [];

        IReadOnlyCollection<string> changed = await ChangedPathsAsync(
            repository,
            [.. modules.Select(module => module.Path)],
            cancellationToken).ConfigureAwait(false);

        return
        [
            .. modules.Select(module => new GitSubmodule(
                Name: module.Name,
                Path: module.Path,
                Url: module.Url,
                IsInitialised: IsInitialised(repository.Root, module.Path),
                HasChanges: changed.Contains(module.Path))),
        ];
    }

    /// <summary>
    /// Adds a submodule at <paramref name="path"/>, relative to the repository root.
    ///
    /// Every refusal below happens before Git is asked, which is what lets the window show the
    /// consequence while the user is still typing. The path guard is
    /// <see cref="WorkingTreeWriter.ResolveInsideRepository"/> -- already public for exactly this, and
    /// it rejects an absolute path and one that climbs out with <c>..</c> in the same test.
    /// </summary>
    public async Task<SubmoduleOutcome> AddAsync(
        RepositoryInfo repository,
        string url,
        string path,
        CancellationToken cancellationToken)
    {
        if (CheckNewPath(repository, url, path) is { } refusal)
            return SubmoduleOutcome.Refused(refusal);

        //`--` before the two values: a URL is arbitrary text and could begin with a dash.
        GitResult result = await git.RunAsync(
            repository.Root,
            ["submodule", "add", "--", url.Trim(), Normalise(path)],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
            return SubmoduleOutcome.Failed(result.ErrorText);

        //.gitmodules has appeared or changed, and RepositoryInfo.HasSubmodules is a probe of it.
        repositories.Invalidate(repository.Root);

        return SubmoduleOutcome.Ok;
    }

    /// <summary>
    /// Clones and checks out what is missing: one submodule when <paramref name="path"/> names one,
    /// every submodule when it is null.
    ///
    /// The same command <c>PullService</c> already runs after a rebase, which is deliberate -- "the
    /// submodules are stale" and "initialise this one" are the same operation, and a second spelling
    /// of it would be a second thing to keep right.
    /// </summary>
    public async Task<SubmoduleOutcome> UpdateAsync(
        RepositoryInfo repository,
        string? path,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "submodule", "update", "--init", "--recursive" };

        if (path is { Length: > 0 })
        {
            args.Add("--");
            args.Add(Normalise(path));
        }

        GitResult result = await git.RunAsync(repository.Root, args, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
            return SubmoduleOutcome.Failed(result.ErrorText);

        repositories.Invalidate(repository.Root);

        return SubmoduleOutcome.Ok;
    }

    /// <summary>
    /// Removes a submodule: <c>deinit</c>, then <c>rm</c>, both staged and neither committed.
    ///
    /// <b><paramref name="force"/> is a parameter rather than a decision made here.</b> Unforced,
    /// Git refuses a submodule holding work that is not committed, and that refusal comes back as
    /// <see cref="SubmoduleOutcome.HasLocalChanges"/> so the caller can ask a second question naming
    /// what is at stake -- the shape <c>branch -d</c> and <c>branch -D</c> already have. Nothing in
    /// this file escalates on its own.
    ///
    /// <b><c>.git/modules/&lt;name&gt;</c> is never deleted</b>, forced or not. It is the submodule's own
    /// clone, and it can hold commits that were made there and never pushed -- work the outer
    /// repository has never seen, which is the one thing CLAUDE.md's Safety Rules make
    /// unconditional. The cost is that re-adding the same submodule later needs that directory
    /// cleared by hand, and the window says so rather than doing it.
    /// </summary>
    public async Task<SubmoduleOutcome> RemoveAsync(
        RepositoryInfo repository,
        string path,
        bool force,
        CancellationToken cancellationToken)
    {
        string module = Normalise(path);

        //deinit first. `git rm` on a populated submodule works, but it is deinit's refusal that names
        //the uncommitted work -- and reaching rm first would have emptied the row the question is about.
        var deinit = new List<string> { "submodule", "deinit" };

        if (force)
            deinit.Add("-f");

        deinit.Add("--");
        deinit.Add(module);

        GitResult removed = await git.RunAsync(repository.Root, deinit, cancellationToken).ConfigureAwait(false);

        if (!removed.Succeeded)
            return Refusal(removed);

        var remove = new List<string> { "rm" };

        if (force)
            remove.Add("-f");

        remove.Add("--");
        remove.Add(module);

        //`git rm` on a gitlink takes the .gitmodules entry with it, so there is no third command.
        GitResult dropped = await git.RunAsync(repository.Root, remove, cancellationToken).ConfigureAwait(false);

        if (!dropped.Succeeded)
            return Refusal(dropped);

        repositories.Invalidate(repository.Root);

        return SubmoduleOutcome.Ok;
    }

    /// <summary>
    /// Why this path may not become a submodule, or null when it may. Public so the window can show
    /// the same answer as a hint before the button is pressed.
    /// </summary>
    public SubmoduleRefusal? CheckNewPath(RepositoryInfo repository, string url, string path)
    {
        if (string.IsNullOrWhiteSpace(url))
            return SubmoduleRefusal.NoUrl;

        if (string.IsNullOrWhiteSpace(path))
            return SubmoduleRefusal.NoPath;

        string relative = Normalise(path);

        if (WorkingTreeWriter.ResolveInsideRepository(repository.Root, relative) is not { } absolute)
            return SubmoduleRefusal.OutsideRepository;

        try
        {
            if (Directory.Exists(absolute) && Directory.EnumerateFileSystemEntries(absolute).Any())
                return SubmoduleRefusal.NotEmpty;

            if (File.Exists(absolute))
                return SubmoduleRefusal.NotEmpty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            //Cannot be enumerated. Let Git try and refuse in its own words rather than refusing a
            //path that may be perfectly usable.
        }

        return null;
    }

    /// <summary>
    /// True when the submodule is checked out.
    ///
    /// A file-system probe, never a Git call: CLAUDE.md's rule for <c>.gitmodules</c> itself, applied
    /// one level down. <c>.git</c> inside a submodule is a <i>file</i> holding a <c>gitdir:</c> line
    /// in every Git this product supports; the directory form is checked too, because a repository
    /// cloned by a much older Git and carried forward still has one.
    /// </summary>
    private static bool IsInitialised(string repositoryRoot, string path)
    {
        if (WorkingTreeWriter.ResolveInsideRepository(repositoryRoot, path) is not { } absolute)
            return false;

        string marker = System.IO.Path.Combine(absolute, ".git");

        return File.Exists(marker) || Directory.Exists(marker);
    }

    /// <summary>
    /// Which of <paramref name="paths"/> differ from HEAD -- the pointer moved, or the checkout is
    /// dirty.
    ///
    /// <c>HEAD</c> rather than the default index comparison, so a pointer the user has already staged
    /// still reads as changed: "you have updated this, commit it" is the question, and staging is
    /// half an answer to it. <c>--ignore-submodules=none</c> because a user's
    /// <c>diff.ignoreSubmodules</c> would otherwise silently empty this column.
    ///
    /// A repository with no commits has no HEAD and fails; that is "nothing has changed", not a
    /// failure of the listing.
    /// </summary>
    private async Task<IReadOnlyCollection<string>> ChangedPathsAsync(
        RepositoryInfo repository,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var args = new List<string>(paths.Count + 6)
        {
            "diff", "HEAD", "--name-only", "-z", "--ignore-submodules=none", "--",
        };

        args.AddRange(paths);

        GitResult result = await git.ReadAsync(repository.Root, args, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
            return [];

        return new HashSet<string>(
            result.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Git refused. Told apart from any other failure by its wording, the way
    /// <c>WorktreeService.RemoveAsync</c> reads its own -- the exit code is 1 for everything
    /// <c>deinit</c> and <c>rm</c> can fail at, so there is nothing else to key on.
    /// </summary>
    private static SubmoduleOutcome Refusal(GitResult result)
    {
        bool dirty =
            result.ErrorText.Contains("local modifications", StringComparison.OrdinalIgnoreCase)
            || result.ErrorText.Contains("contains modified or untracked", StringComparison.OrdinalIgnoreCase)
            || result.ErrorText.Contains("use '-f'", StringComparison.OrdinalIgnoreCase)
            || result.ErrorText.Contains("use -f", StringComparison.OrdinalIgnoreCase)
            || result.ErrorText.Contains("--force", StringComparison.OrdinalIgnoreCase);

        return new SubmoduleOutcome(false, result.ErrorText, HasLocalChanges: dirty);
    }

    /// <summary>
    /// Forward slashes, no leading or trailing separator. Git stores and reports a submodule path
    /// that way whatever the user typed, and the value is compared against
    /// <c>diff --name-only</c> output, which is Git's own spelling.
    /// </summary>
    private static string Normalise(string path) =>
        path.Trim().Replace('\\', '/').Trim('/');

    /// <summary>
    /// <c>.gitmodules</c>, as key/value pairs, reduced to one record per submodule.
    ///
    /// The key trap is the one <c>remote.*</c> already has and for the same reason: Git lower-cases
    /// the section and the final component and leaves the subsection verbatim, and a submodule's name
    /// defaults to its <i>path</i> -- so <c>libs/proto.v2</c> is a name with a dot in it, and taking
    /// the second field would cut it in half.
    ///
    /// A declaration with no <c>path</c> is dropped: it is what the row is keyed on, what every
    /// command below takes, and Git itself ignores such an entry.
    /// </summary>
    internal static IReadOnlyList<DeclaredSubmodule> ParseModules(string standardOutput)
    {
        //Insertion-ordered, so the window lists them the way .gitmodules does rather than in a sorted
        //order that would move rows around as submodules are added.
        var order = new List<string>();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (ConfigEntry entry in GitConfigList.ParseList(standardOutput))
        {
            if (entry.Value is null)
                continue;

            if (GitConfigList.SubsectionOf(entry.Key, "submodule", ".path") is { } pathOf)
            {
                if (!paths.ContainsKey(pathOf))
                    order.Add(pathOf);

                paths[pathOf] = entry.Value.Trim();
            }
            else if (GitConfigList.SubsectionOf(entry.Key, "submodule", ".url") is { } urlOf)
            {
                urls[urlOf] = entry.Value.Trim();
            }

            //Everything else -- .branch, .update, .shallow, .ignore -- is Git's business, not ours.
        }

        return
        [
            .. order
                .Where(name => paths[name].Length > 0)
                .Select(name => new DeclaredSubmodule(
                    name,
                    Normalise(paths[name]),
                    urls.TryGetValue(name, out string? url) ? url : string.Empty)),
        ];
    }
}

/// <summary>One <c>.gitmodules</c> declaration, before anything is known about the disk.</summary>
internal sealed record DeclaredSubmodule(string Name, string Path, string Url);

/// <param name="Name">The subsection name in <c>.gitmodules</c>, which defaults to the path.</param>
/// <param name="Path">Relative to the repository root, forward-slashed, as Git spells it.</param>
/// <param name="IsInitialised">Whether the submodule is checked out. A file probe, not a Git call.</param>
/// <param name="HasChanges">Whether it differs from HEAD -- the pointer moved, or its tree is dirty.</param>
public sealed record GitSubmodule(string Name, string Path, string Url, bool IsInitialised, bool HasChanges);

/// <summary>Why a path may not become a submodule. Every one is answered before Git is asked.</summary>
public enum SubmoduleRefusal
{
    NoUrl,
    NoPath,

    /// <summary>Absolute, or it climbs out of the repository with <c>..</c>.</summary>
    OutsideRepository,

    NotEmpty,
}

/// <param name="HasLocalChanges">
/// Git refused because the submodule holds work that is not committed. The caller's cue to ask a
/// second question, never this service's cue to force.
/// </param>
public sealed record SubmoduleOutcome(bool Succeeded, string? GitError, bool HasLocalChanges = false)
{
    public static readonly SubmoduleOutcome Ok = new(true, null);

    public static SubmoduleOutcome Failed(string error) => new(false, error);

    /// <summary>Refused before any command ran, so nothing has happened.</summary>
    public static SubmoduleOutcome Refused(SubmoduleRefusal reason) => new(false, null) { Refusal = reason };

    public SubmoduleRefusal? Refusal { get; init; }
}
