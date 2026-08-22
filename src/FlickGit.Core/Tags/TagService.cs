using System.Text.RegularExpressions;
using FlickGit.Git;
using FlickGit.Models;

namespace FlickGit.Tags;

/// <summary>
/// Listing, creating and deleting tags, locally and on a remote.
///
/// The sequence rules live here rather than in the window, for the reason CLAUDE.md gives about
/// <c>CommitFlow</c>: "anything with an order that matters ... goes in <c>FlickGit.Core</c> and gets
/// tests", because a view model can only be exercised by clicking and the wrong order is exactly the
/// bug clicking does not reveal. There is one order that matters here and it is
/// <see cref="DeleteAsync"/>: the remote goes first.
///
/// <b>Nothing here asks the network what tags exist.</b> Listing is <c>for-each-ref</c> over
/// <c>refs/tags</c>, which is local. Knowing whether a tag is also on the remote would need
/// <c>git ls-remote</c> on every window open, and a picker that takes a round trip before it paints
/// is a picker nobody uses — so the window offers "and on the remote" as a choice rather than as a
/// fact it claims to know.
/// </summary>
public sealed class TagService(IGitProcessRunner git)
{
    /// <summary>
    /// The separator between fields of one <c>for-each-ref</c> record, spelled the way a Git format
    /// string spells a byte.
    ///
    /// A NUL rather than a tab, because <c>%(contents:subject)</c> is arbitrary user text and a tag
    /// message containing a tab would otherwise split into a field that was never there. The record
    /// separator stays the newline <c>for-each-ref</c> emits: <c>contents:subject</c> is the first
    /// line of the message and nothing else, so it cannot contain one.
    /// </summary>
    private const string FieldFormat = "%00";

    private const char FieldSeparator = '\0';

    /// <summary>
    /// A tag name Git will reject, caught before any command runs.
    ///
    /// The same two-stage validation as <c>BranchService</c>: this is the cheap half, for live
    /// feedback as the user types, and <see cref="ValidateAsync"/> then asks Git itself before
    /// anything is created. Deliberately its own pattern rather than the branch one reused — a tag
    /// may be called <c>HEAD</c> and a branch may not, so sharing would mean one regex answering two
    /// questions.
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

    /// <summary>Every tag in the repository, newest version first.</summary>
    public async Task<IReadOnlyList<GitTag>> ListAsync(
        RepositoryInfo repository,
        CancellationToken cancellationToken)
    {
        //for-each-ref, not `git tag --list`: `tag` is a porcelain command whose output is shaped for
        //a terminal, and CLAUDE.md forbids parsing that. This also gets the target, the date and the
        //message in the same process rather than one `git show` per row.
        //
        //-v:refname sorts 1.10 after 1.9, which is the order anybody reading a version list expects
        //and the order a plain string sort gets wrong.
        GitResult result = await git.ReadAsync(
            repository.Root,
            [
                "for-each-ref",
                "--sort=-v:refname",
                "--format=%(refname:short)" + FieldFormat
                    + "%(objecttype)" + FieldFormat
                    + "%(objectname:short)" + FieldFormat
                    + "%(creatordate:short)" + FieldFormat
                    + "%(contents:subject)",
                "refs/tags",
            ],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
            return [];

        var tags = new List<GitTag>();

        foreach (string line in result.StdOut.Split('\n'))
        {
            string record = line.TrimEnd('\r');

            if (record.Length == 0)
                continue;

            string[] fields = record.Split(FieldSeparator);

            if (fields.Length < 4 || fields[0].Length == 0)
                continue;

            tags.Add(new GitTag(
                Name: fields[0],

                //An annotated tag is its own object, so refs/tags/<name> points at a `tag` rather
                //than straight at a `commit`. That is the only difference the user can see and it is
                //worth showing: a lightweight tag carries no message of its own.
                IsAnnotated: string.Equals(fields[1], "tag", StringComparison.Ordinal),
                Target: fields[2],
                Date: fields[3],
                Subject: fields.Length > 4 ? fields[4] : string.Empty));
        }

        return tags;
    }

    /// <summary>
    /// Creates a tag at <paramref name="commit"/>, or at HEAD when that is null.
    ///
    /// Annotated when <paramref name="message"/> has content, lightweight otherwise. That is the
    /// choice rather than a separate flag, because an annotated tag with an empty message is a thing
    /// Git refuses and a distinction the user should not have to learn in order to avoid.
    /// </summary>
    public async Task<TagOutcome> CreateAsync(
        RepositoryInfo repository,
        string name,
        string? message,
        string? commit,
        CancellationToken cancellationToken)
    {
        TagOutcome validation = await ValidateAsync(repository, name, cancellationToken).ConfigureAwait(false);

        if (!validation.Succeeded)
            return validation;

        string tag = name.Trim();
        var args = new List<string> { "tag" };

        if (message is { } text && text.Trim().Length > 0)
        {
            args.Add("-a");
            args.Add("-m");
            args.Add(text.Trim());
        }

        args.Add(tag);

        if (commit is { Length: > 0 })
            args.Add(commit);

        GitResult result = await git.RunAsync(repository.Root, args, cancellationToken).ConfigureAwait(false);

        //No --force, and no offer of one anywhere above this: moving a tag that has been pushed is
        //how two people end up with different commits under one version number. Git's own "already
        //exists" is both the safe answer and the clearer one.
        return result.Succeeded ? TagOutcome.Ok : TagOutcome.Failed(result.ErrorText);
    }

    /// <summary>
    /// Deletes a tag: on the remote first when asked, then locally.
    ///
    /// <b>The order is the safety property.</b> Deleting locally first and then failing to reach the
    /// remote leaves the tag published and invisible from this machine — the user can no longer see
    /// the thing they still have to delete. Remote first means a failure leaves everything as it was,
    /// which is what CLAUDE.md's "when an operation fails midway, preserve repository state" asks for.
    /// </summary>
    /// <param name="remote">The remote to delete from as well, or null for local only.</param>
    public async Task<TagOutcome> DeleteAsync(
        RepositoryInfo repository,
        string name,
        string? remote,
        CancellationToken cancellationToken)
    {
        string tag = name.Trim();

        if (remote is { Length: > 0 })
        {
            //`refs/tags/<name>`, never the bare name. `git push origin --delete v1.0` is ambiguous
            //when a *branch* called v1.0 also exists, and a fully qualified ref cannot be misread as
            //a branch — so the deletion can never land on the wrong ref.
            GitResult pushed = await git.RunAsync(
                repository.Root,
                ["push", remote, "--delete", $"refs/tags/{tag}"],
                cancellationToken).ConfigureAwait(false);

            if (!pushed.Succeeded)
                return TagOutcome.Failed(pushed.ErrorText);
        }

        GitResult local = await git.RunAsync(
            repository.Root,
            ["tag", "-d", tag],
            cancellationToken).ConfigureAwait(false);

        return local.Succeeded ? TagOutcome.Ok : TagOutcome.Failed(local.ErrorText);
    }

    /// <summary>Publishes one tag, by its fully qualified ref for the reason <see cref="DeleteAsync"/> gives.</summary>
    public async Task<TagOutcome> PushAsync(
        RepositoryInfo repository,
        string name,
        string remote,
        CancellationToken cancellationToken)
    {
        GitResult result = await git.RunAsync(
            repository.Root,
            ["push", remote, $"refs/tags/{name.Trim()}"],
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded ? TagOutcome.Ok : TagOutcome.Failed(result.ErrorText);
    }

    /// <summary>
    /// The remote to publish to: <c>origin</c> when it exists, otherwise the only other one.
    ///
    /// The same resolution <c>PushService</c> makes, and for the same reason it gives: picking the
    /// first of several remotes would publish a version number somewhere other people read. Null
    /// means the caller must not offer a remote at all.
    /// </summary>
    public async Task<string?> ResolveRemoteAsync(RepositoryInfo repository, CancellationToken cancellationToken)
    {
        GitResult result = await git.ReadAsync(repository.Root, ["remote"], cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
            return null;

        string[] remotes = result.StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(remote => remote.Trim())
            .Where(remote => remote.Length > 0)
            .ToArray();

        return remotes.Contains("origin", StringComparer.Ordinal) ? "origin"
            : remotes.Length == 1 ? remotes[0]
            : null;
    }

    /// <summary>
    /// Asks Git whether a tag name is acceptable, before creating anything.
    ///
    /// <c>check-ref-format</c> on the full <c>refs/tags/</c> path rather than <c>--branch</c>, which
    /// applies the branch rules: those forbid a name Git accepts perfectly well as a tag.
    /// </summary>
    public async Task<TagOutcome> ValidateAsync(
        RepositoryInfo repository,
        string name,
        CancellationToken cancellationToken)
    {
        string tag = name.Trim();

        if (ObviouslyInvalid.IsMatch(tag))
            return TagOutcome.Failed($"'{tag}' is not a valid tag name.");

        GitResult result = await git.ReadAsync(
            repository.Root,
            ["check-ref-format", $"refs/tags/{tag}"],
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded
            ? TagOutcome.Ok
            : TagOutcome.Failed($"Git rejected '{tag}' as a tag name.\n\n{result.ErrorText}");
    }

    /// <summary>Fast, offline check used for live feedback while typing.</summary>
    public static bool LooksValid(string name) => !ObviouslyInvalid.IsMatch(name.Trim());
}

/// <param name="Name">The tag, as Git reports it.</param>
/// <param name="IsAnnotated">True when the ref points at a tag object rather than straight at a commit.</param>
/// <param name="Target">The abbreviated object the tag resolves to.</param>
/// <param name="Date">Creation date, <c>yyyy-MM-dd</c>. The tag's own when annotated, the commit's otherwise.</param>
/// <param name="Subject">
/// First line of the tag message.
///
/// <b>For a lightweight tag this is the tagged commit's subject instead</b>, because
/// <c>contents:subject</c> falls through to the object the ref points at when there is no tag object
/// of its own. That is why the window shows it only when <see cref="IsAnnotated"/>: presenting a
/// commit message as a tag message would be inventing an annotation that is not there.
/// </param>
public sealed record GitTag(string Name, bool IsAnnotated, string Target, string Date, string Subject);

/// <param name="Succeeded">The operation completed.</param>
/// <param name="GitError">Git's own words. Never paraphrased — CLAUDE.md, "Error Handling".</param>
public sealed record TagOutcome(bool Succeeded, string? GitError)
{
    public static readonly TagOutcome Ok = new(true, null);

    public static TagOutcome Failed(string error) => new(false, error);
}
