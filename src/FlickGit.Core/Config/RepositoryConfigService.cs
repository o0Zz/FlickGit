using FlickGit.Git;
using FlickGit.Models;

namespace FlickGit.Config;

/// <summary>
/// The repository's own configuration: who it commits as, what remotes it has, and the two
/// preferences FlickGit keeps per repository.
///
/// <b>One read answers the whole window.</b> <c>config --local --list -z</c> returns the identity,
/// every remote and the <c>flickgit.*</c> keys in a single process, which is why there is no
/// <c>git remote -v</c> anywhere here: that is output shaped for a terminal, and CLAUDE.md,
/// "Coding Guidelines" forbids parsing it. One command, one parser.
///
/// FlickGit's own per-repository preferences live here rather than in <c>settings.json</c>, under a
/// <c>flickgit.</c> section. A path-keyed dictionary in a global file goes stale the moment the
/// repository moves and is invisible from the place it applies; <c>.git/config</c> is neither, and
/// it is not committed, so nothing leaks into the repository's history.
/// </summary>
public sealed class RepositoryConfigService(IGitProcessRunner git)
{
    public const string UserNameKey = "user.name";
    public const string UserEmailKey = "user.email";

    /// <summary>Overrides the global primary-branch setting for this repository alone.</summary>
    public const string PrimaryBranchKey = "flickgit.primaryBranch";

    /// <summary>
    /// The remembered answer to "create an upstream for this branch?".
    ///
    /// CLAUDE.md, "Push": asked once and remembered per repository. Here rather than in
    /// <c>settings.json</c> because it *is* a fact about this repository — a user who publishes
    /// freely to their own fork may not want to on a shared origin.
    /// </summary>
    public const string UpstreamAnswerKey = "flickgit.allowUpstreamCreation";

    /// <summary>
    /// Everything the repository window shows, from four parallel reads.
    ///
    /// The effective identity needs its own calls: <c>--local</c> answers "does this repository
    /// override it", and the plain form answers "who would a commit be attributed to", which is the
    /// question the user is actually asking. Both are needed to tell an override from an inheritance.
    /// </summary>
    public async Task<RepositoryConfig> ReadAsync(RepositoryInfo repository, CancellationToken cancellationToken)
    {
        Task<GitResult> local = git.ReadAsync(repository.Root, ["config", "--local", "--list", "-z"], cancellationToken);
        Task<GitResult> name = git.ReadAsync(repository.Root, ["config", "--get", UserNameKey], cancellationToken);
        Task<GitResult> email = git.ReadAsync(repository.Root, ["config", "--get", UserEmailKey], cancellationToken);

        //--quiet so a detached HEAD is an empty answer rather than an error line in the log.
        Task<GitResult> head = git.ReadAsync(repository.Root, ["symbolic-ref", "--short", "--quiet", "HEAD"], cancellationToken);

        await Task.WhenAll(local, name, email, head).ConfigureAwait(false);

        IReadOnlyList<ConfigEntry> entries = local.Result.Succeeded
            ? ParseList(local.Result.StdOut)
            : [];

        string? branch = head.Result.Succeeded ? NullIfEmpty(head.Result.StdOut) : null;

        return new RepositoryConfig(
            LocalName: Value(entries, UserNameKey),
            LocalEmail: Value(entries, UserEmailKey),
            EffectiveName: name.Result.Succeeded ? NullIfEmpty(name.Result.StdOut) : null,
            EffectiveEmail: email.Result.Succeeded ? NullIfEmpty(email.Result.StdOut) : null,
            Remotes: RemotesFrom(entries),
            PrimaryBranch: Value(entries, PrimaryBranchKey),
            AllowUpstreamCreation: ParseBool(Entry(entries, UpstreamAnswerKey)),
            CurrentBranch: branch,
            TrackedRemote: branch is null ? null : Value(entries, $"branch.{branch}.remote"));
    }

    /// <summary>Sets one key in the repository's own config file. Never <c>--global</c>.</summary>
    public async Task<ConfigOutcome> WriteAsync(
        RepositoryInfo repository,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        GitResult result = await git.RunAsync(
            repository.Root,
            ["config", "--local", key, value],
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded ? ConfigOutcome.Ok : ConfigOutcome.Failed(result.ErrorText);
    }

    /// <summary>
    /// Removes a local override, so the value is inherited again.
    ///
    /// <b>Exit code 5 is success.</b> That is Git's "you tried to unset an option which does not
    /// exist" — which is the ordinary case for "use the global identity" on a repository that never
    /// had an override, and reporting it as a failure would put a Git error in front of a user whose
    /// request was already satisfied.
    /// </summary>
    public async Task<ConfigOutcome> UnsetAsync(
        RepositoryInfo repository,
        string key,
        CancellationToken cancellationToken)
    {
        GitResult result = await git.RunAsync(
            repository.Root,
            ["config", "--local", "--unset", key],
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded || result.ExitCode == NothingToUnset
            ? ConfigOutcome.Ok
            : ConfigOutcome.Failed(result.ErrorText);
    }

    /// <summary>
    /// This repository's primary-branch override, or null when it has none.
    ///
    /// Its own one-key read rather than <see cref="ReadAsync"/>, because the caller is
    /// <c>BranchService</c> resolving the commit window's warning strip: it wants one value, not a
    /// window's worth of them.
    /// </summary>
    public async Task<string?> ReadPrimaryBranchOverrideAsync(
        RepositoryInfo repository,
        CancellationToken cancellationToken)
    {
        string? value = await GetAsync(repository, PrimaryBranchKey, cancellationToken).ConfigureAwait(false);
        return value is null ? null : NullIfEmpty(value);
    }

    /// <summary>The remembered upstream answer, or null when this repository has not been asked.</summary>
    public async Task<bool?> ReadUpstreamAnswerAsync(RepositoryInfo repository, CancellationToken cancellationToken)
    {
        string? value = await GetAsync(repository, UpstreamAnswerKey, cancellationToken).ConfigureAwait(false);
        return value is null ? null : ParseBool(new ConfigEntry(UpstreamAnswerKey, value));
    }

    /// <summary>Remembers the answer, either way. A user who said no is not asked again.</summary>
    public Task<ConfigOutcome> WriteUpstreamAnswerAsync(
        RepositoryInfo repository,
        bool allowed,
        CancellationToken cancellationToken) =>
        WriteAsync(repository, UpstreamAnswerKey, allowed ? "true" : "false", cancellationToken);

    /// <summary>Git's "you tried to unset an option which does not exist".</summary>
    private const int NothingToUnset = 5;

    /// <summary>One key, or null when it is not set. A missing key is exit 1, not an error to report.</summary>
    private async Task<string?> GetAsync(RepositoryInfo repository, string key, CancellationToken cancellationToken)
    {
        GitResult result = await git.ReadAsync(
            repository.Root,
            ["config", "--local", "--get", key],
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded ? result.StdOut.Trim() : null;
    }

    /// <summary>
    /// Splits <c>config --list -z</c> into key/value pairs.
    ///
    /// Records are NUL-terminated and the key is separated from its value by the <b>first</b>
    /// newline — which is what makes a value containing newlines survive, and why this is a state
    /// machine over the NUL stream rather than a line split. A record with no newline at all is a
    /// key set with no value, which Git reads as true.
    /// </summary>
    internal static IReadOnlyList<ConfigEntry> ParseList(string standardOutput)
    {
        var entries = new List<ConfigEntry>();

        foreach (string record in standardOutput.Split('\0'))
        {
            if (record.Length == 0)
                continue;

            int newline = record.IndexOf('\n');

            entries.Add(newline < 0
                ? new ConfigEntry(record, null)
                : new ConfigEntry(record[..newline], record[(newline + 1)..]));
        }

        return entries;
    }

    /// <summary>
    /// The remotes, origin first.
    ///
    /// <b>The name is the middle of the key, taken verbatim.</b> <c>git config --list</c> lower-cases
    /// the section and the final component and leaves the subsection alone, so
    /// <c>remote.MyFork.url</c> keeps its capitals while <c>flickgit.primaryBranch</c> arrives as
    /// <c>flickgit.primarybranch</c> — which is why every key here is matched case-insensitively and
    /// the remote name never is. A remote may itself contain dots, so the name is everything between
    /// the first and last separator rather than the second field.
    /// </summary>
    internal static IReadOnlyList<GitRemote> RemotesFrom(IReadOnlyList<ConfigEntry> entries)
    {
        var fetch = new Dictionary<string, string>(StringComparer.Ordinal);
        var push = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (ConfigEntry entry in entries)
        {
            if (!entry.Key.StartsWith("remote.", StringComparison.OrdinalIgnoreCase) || entry.Value is null)
                continue;

            if (NameBetween(entry.Key, ".url") is { } urlOf)
                fetch[urlOf] = entry.Value;
            else if (NameBetween(entry.Key, ".pushurl") is { } pushOf)
                push[pushOf] = entry.Value;
        }

        return fetch
            .Select(pair => new GitRemote(
                pair.Key,
                pair.Value,

                //Only when it differs. A pushurl equal to the fetch url is noise on the row, and a
                //remote with no pushurl at all pushes to the fetch url.
                push.TryGetValue(pair.Key, out string? pushUrl) && !string.Equals(pushUrl, pair.Value, StringComparison.Ordinal)
                    ? pushUrl
                    : null))
            .OrderByDescending(remote => string.Equals(remote.Name, "origin", StringComparison.Ordinal))
            .ThenBy(remote => remote.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The subsection of <c>remote.&lt;name&gt;&lt;suffix&gt;</c>, or null when the key is something else.</summary>
    private static string? NameBetween(string key, string suffix)
    {
        if (!key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;

        string name = key["remote.".Length..^suffix.Length];
        return name.Length == 0 ? null : name;
    }

    private static ConfigEntry? Entry(IReadOnlyList<ConfigEntry> entries, string key) =>
        entries.LastOrDefault(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The value of a key, or null when it is unset or empty.
    ///
    /// The <i>last</i> occurrence, because that is the one Git itself would report: a key repeated in
    /// one file is legal and the later line wins.
    /// </summary>
    private static string? Value(IReadOnlyList<ConfigEntry> entries, string key) =>
        NullIfEmpty(Entry(entries, key)?.Value ?? string.Empty);

    /// <summary>
    /// Git's boolean spelling, which is not <c>bool.TryParse</c>'s.
    ///
    /// <c>yes</c>, <c>on</c> and <c>1</c> are all true, and a key present with no value at all is
    /// true as well — that is how <c>[flickgit] allowUpstreamCreation</c> on its own line reads.
    /// </summary>
    private static bool? ParseBool(ConfigEntry? entry)
    {
        if (entry is null)
            return null;

        if (entry.Value is null)
            return true;

        return entry.Value.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" or "" => false,
            _ => null,
        };
    }

    private static string? NullIfEmpty(string value)
    {
        string trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}

/// <param name="Key">As Git reported it: section and final component lower-cased, subsection verbatim.</param>
/// <param name="Value">Null when the key was set with no value, which Git reads as true.</param>
internal sealed record ConfigEntry(string Key, string? Value);

/// <param name="Name">The remote, capitals and dots as configured.</param>
/// <param name="FetchUrl">Where <c>fetch</c> goes.</param>
/// <param name="PushUrl">Where <c>push</c> goes, only when it differs from <paramref name="FetchUrl"/>.</param>
public sealed record GitRemote(string Name, string FetchUrl, string? PushUrl);

/// <param name="LocalName">Set in this repository's own config, or null when inherited.</param>
/// <param name="LocalEmail">Set in this repository's own config, or null when inherited.</param>
/// <param name="EffectiveName">Who a commit would be attributed to, wherever that came from.</param>
/// <param name="EffectiveEmail">The address a commit would carry, wherever that came from.</param>
/// <param name="Remotes">Every configured remote, origin first.</param>
/// <param name="PrimaryBranch">This repository's primary-branch override, or null.</param>
/// <param name="AllowUpstreamCreation">The remembered upstream answer, or null when never asked.</param>
/// <param name="CurrentBranch">Null on a detached HEAD.</param>
/// <param name="TrackedRemote">The remote the current branch pushes to, or null when it has no upstream.</param>
public sealed record RepositoryConfig(
    string? LocalName,
    string? LocalEmail,
    string? EffectiveName,
    string? EffectiveEmail,
    IReadOnlyList<GitRemote> Remotes,
    string? PrimaryBranch,
    bool? AllowUpstreamCreation,
    string? CurrentBranch,
    string? TrackedRemote)
{
    /// <summary>True when this repository overrides the identity rather than inheriting it.</summary>
    public bool HasLocalIdentity => LocalName is not null || LocalEmail is not null;
}

/// <param name="Succeeded">The operation completed.</param>
/// <param name="GitError">Git's own words. Never paraphrased — CLAUDE.md, "Error Handling".</param>
public sealed record ConfigOutcome(bool Succeeded, string? GitError)
{
    public static readonly ConfigOutcome Ok = new(true, null);

    public static ConfigOutcome Failed(string error) => new(false, error);
}
