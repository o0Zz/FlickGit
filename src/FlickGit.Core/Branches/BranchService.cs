using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using FlickGit.Config;
using FlickGit.Git;
using FlickGit.Models;

namespace FlickGit.Branches;

/// <summary>
/// Branch listing, name validation and primary-branch resolution.
///
/// The commit surface needs the primary branch for one reason only: to decide whether to
/// show the "you are committing to main" strip. CLAUDE.md, "Primary Branch Resolution":
/// "Resolving this must never block the menu or the popup" — so the result is cached per
/// repository and every caller has to be able to carry on without it.
/// </summary>
public sealed class BranchService(IGitProcessRunner git, RepositoryConfigService config)
{
    private readonly ConcurrentDictionary<string, string> _primaryBranchCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A branch name Git will reject, caught before any command runs. CLAUDE.md,
    /// "Testing": "an invalid ref name is rejected before any Git command runs."
    ///
    /// This is the cheap half of validation — enough to give the ComboBox live feedback
    /// as the user types, without a process start per keystroke.
    /// <see cref="ValidateAsync"/> then asks Git itself before anything is created.
    /// </summary>
    private static readonly Regex ObviouslyInvalid = new(
        """
        (?x)
          ^$                     # empty
        | ^[-.]                  # leading dash or dot
        | [.]$ | [/]$            # trailing dot or slash
        | \.\.                   # ".." anywhere
        | @\{                    # "@{" is reflog syntax
        | ^@$                    # "@" alone means HEAD
        | //                     # empty path component
        | [\x00-\x20~^:?*\[\\\x7f]   # control chars and the characters git forbids outright
        | \.lock(?:/|$)          # a component ending in .lock
        """,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Local branches, current first, then alphabetical.</summary>
    public async Task<IReadOnlyList<string>> ListLocalBranchesAsync(
        RepositoryInfo repository,
        string? currentBranch,
        CancellationToken cancellationToken)
    {
        //for-each-ref, not `branch --list`: `branch` is a porcelain command whose output
        //carries a "* " marker and column padding meant for humans, and CLAUDE.md forbids
        //parsing that.
        GitResult result = await git.ReadAsync(
            repository.Root,
            ["for-each-ref", "--format=%(refname:short)", "refs/heads"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
            return currentBranch is null ? [] : [currentBranch];

        List<string> branches = result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (currentBranch is not null && branches.Remove(currentBranch))
            branches.Insert(0, currentBranch);

        return branches;
    }

    /// <summary>
    /// Which branch this repository treats as primary, for the warning strip.
    ///
    /// Order: this repository's own <c>flickgit.primaryBranch</c>, then the user's setting, then the
    /// remote's HEAD, then <c>main</c>, then <c>master</c>. The remote HEAD is asked for before the
    /// two guesses because a repository that still uses <c>master</c> should not be warned about
    /// <c>main</c>.
    ///
    /// <b>Neither configured answer is cached, and that is deliberate.</b> The override is one
    /// <c>config --get</c> — cheap, and always current, so the repository window writing it needs no
    /// way to invalidate anything and the warning strip is right on the very next open. Only the
    /// answer that costs a ref lookup is cached.
    /// </summary>
    public async Task<string> ResolvePrimaryBranchAsync(
        RepositoryInfo repository,
        string? configuredPrimaryBranch,
        CancellationToken cancellationToken)
    {
        //The repository's own answer first: the more specific setting wins, which is the whole point
        //of having a per-repository one.
        if (await config.ReadPrimaryBranchOverrideAsync(repository, cancellationToken).ConfigureAwait(false) is { } local)
            return local;

        if (!string.IsNullOrWhiteSpace(configuredPrimaryBranch))
            return configuredPrimaryBranch.Trim();

        if (_primaryBranchCache.TryGetValue(repository.Root, out string? cached))
            return cached;

        string resolved = await ResolveFromRemoteHeadAsync(repository, cancellationToken).ConfigureAwait(false)
                          ?? await FirstExistingAsync(repository, ["main", "master"], cancellationToken).ConfigureAwait(false)
                          ?? "main";

        _primaryBranchCache[repository.Root] = resolved;
        return resolved;
    }

    /// <summary>
    /// Asks Git whether a branch name is acceptable, before creating anything.
    ///
    /// `check-ref-format --branch` is an exit-code question and it is the authoritative
    /// answer — it knows the rules this build of Git enforces, which the regex above only
    /// approximates.
    /// </summary>
    public async Task<BranchNameValidation> ValidateAsync(
        RepositoryInfo repository,
        string branchName,
        CancellationToken cancellationToken)
    {
        string trimmed = branchName.Trim();

        if (ObviouslyInvalid.IsMatch(trimmed))
            return new BranchNameValidation(false, $"'{trimmed}' is not a valid branch name.");

        GitResult result = await git.ReadAsync(
            repository.Root,
            ["check-ref-format", "--branch", trimmed],
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded
            ? new BranchNameValidation(true, null)
            : new BranchNameValidation(false, $"Git rejected '{trimmed}' as a branch name.\n\n{result.ErrorText}");
    }

    /// <summary>Fast, offline check used for live feedback while typing.</summary>
    public static bool LooksValid(string branchName) => !ObviouslyInvalid.IsMatch(branchName.Trim());

    private async Task<bool> BranchExistsAsync(
        RepositoryInfo repository,
        string branchName,
        CancellationToken cancellationToken)
    {
        //--verify with a full refname, so a branch called "main" is not confused with a
        //tag called "main" or with a file of that name in the working tree.
        GitResult result = await git.ReadAsync(
            repository.Root,
            ["rev-parse", "--verify", "--quiet", $"refs/heads/{branchName}"],
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded;
    }

    private async Task<string?> ResolveFromRemoteHeadAsync(
        RepositoryInfo repository,
        CancellationToken cancellationToken)
    {
        //Purely local: this reads refs/remotes/origin/HEAD, which was written at clone
        //time. No network, which is what makes it safe to call while a menu is being
        //built -- CLAUDE.md: "Explorer integration must never block on network
        //operations."
        GitResult result = await git.ReadAsync(
            repository.Root,
            ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
            return null;

        //"origin/main" -> "main".
        string value = result.StdOut.Trim();
        int slash = value.IndexOf('/');
        return slash >= 0 && slash < value.Length - 1 ? value[(slash + 1)..] : null;
    }

    private async Task<string?> FirstExistingAsync(
        RepositoryInfo repository,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        foreach (string candidate in candidates)
        {
            if (await BranchExistsAsync(repository, candidate, cancellationToken).ConfigureAwait(false))
                return candidate;
        }

        return null;
    }
}

/// <param name="IsValid">False when the name must not be used.</param>
/// <param name="Error">Why, in the words to show the user. Null when valid.</param>
public sealed record BranchNameValidation(bool IsValid, string? Error);
