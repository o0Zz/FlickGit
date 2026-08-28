using FlickGit.Branches;
using FlickGit.Git;
using FlickGit.Models;

namespace FlickGit.Tags;

/// <summary>
/// Listing, creating and deleting tags, locally and on a remote. There is one order that matters
/// and it is <see cref="DeleteAsync"/>: the remote goes first.
///
/// <b>Nothing here asks the network what tags exist.</b> Knowing whether a tag is also on the
/// remote would need <c>git ls-remote</c> on every window open, and a picker that takes a round
/// trip before it paints is a picker nobody uses -- so the window offers "and on the remote" as a
/// choice rather than as a fact it claims to know.
/// </summary>
public sealed class TagService(IGitProcessRunner git)
{
    /// <summary>
    /// The separator between fields of one <c>for-each-ref</c> record. A NUL rather than a tab,
    /// because <c>%(contents:subject)</c> is arbitrary user text and a tag message containing a tab
    /// would otherwise split into a field that was never there. The record separator stays the
    /// newline: <c>contents:subject</c> is the first line of the message and cannot contain one.
    /// </summary>
    private const string FieldFormat = "%00";

    private const char FieldSeparator = '\0';

    /// <summary>Every tag in the repository, newest version first.</summary>
    public async Task<IReadOnlyList<GitTag>> ListAsync(
        RepositoryInfo repository,
        CancellationToken cancellationToken)
    {
        //for-each-ref, not `git tag --list`, whose output is shaped for a terminal. This also gets the
        //target, the date and the message in the same process rather than one `git show` per row.
        //
        //-v:refname sorts 1.10 after 1.9, which a plain string sort gets wrong.
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

                //An annotated tag is its own object, so refs/tags/<name> points at a `tag` rather than straight
                //at a `commit`. That is the only difference the user can see, and a lightweight tag carries no
                //message of its own.
                IsAnnotated: string.Equals(fields[1], "tag", StringComparison.Ordinal),
                Target: fields[2],
                Date: fields[3],
                Subject: fields.Length > 4 ? fields[4] : string.Empty));
        }

        return tags;
    }

    /// <summary>
    /// Creates a tag at <paramref name="commit"/>, or at HEAD when that is null. Annotated when
    /// <paramref name="message"/> has content, lightweight otherwise -- that is the choice rather than
    /// a separate flag, because an annotated tag with an empty message is a thing Git refuses.
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

        //No --force, and no offer of one anywhere above this: moving a tag that has been pushed is how
        //two people end up with different commits under one version number.
        return result.Succeeded ? TagOutcome.Ok : TagOutcome.Failed(result.ErrorText);
    }

    /// <summary>
    /// Deletes a tag: on the remote first when asked, then locally.
    ///
    /// <b>The order is the safety property.</b> Deleting locally first and then failing to reach the
    /// remote leaves the tag published and invisible from this machine. Remote first means a failure
    /// leaves everything as it was.
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
            //`refs/tags/<name>`, never the bare name. `git push origin --delete v1.0` is ambiguous when a
            //*branch* called v1.0 also exists, and a fully qualified ref cannot be misread as a branch.
            GitResult pushed = await git.RunAsync(
                repository.Root,
                ["push", remote, "--delete", $"refs/tags/{tag}"],
                cancellationToken).ConfigureAwait(false);

            //A remote that has no such ref is this half's *success*, not its failure. Nothing here asks
            //the network what tags exist -- the window offers "and on the remote" as a choice rather
            //than as a fact -- so deleting a tag that was never pushed takes this path every time, and
            //treating it as a failure meant such a tag could not be deleted at all: the remote call
            //failed and the local delete below never ran.
            if (!pushed.Succeeded && !SaysTheRemoteHasNoSuchRef(pushed.ErrorText))
                return TagOutcome.Failed(pushed.ErrorText);
        }

        GitResult local = await git.RunAsync(
            repository.Root,
            ["tag", "-d", tag],
            cancellationToken).ConfigureAwait(false);

        return local.Succeeded ? TagOutcome.Ok : TagOutcome.Failed(local.ErrorText);
    }

    /// <summary>
    /// Git's refusal to delete a ref the remote does not have.
    ///
    /// Matched on Git's own wording because the exit code cannot tell this apart from any other push
    /// failure, and the difference decides whether the local tag is then deleted or left alone. Erring
    /// towards *not* matching is the safe direction: an unrecognised message stops the whole delete,
    /// which is where this method started.
    /// </summary>
    private static bool SaysTheRemoteHasNoSuchRef(string error) =>
        error.Contains("remote ref does not exist", StringComparison.OrdinalIgnoreCase);

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
    /// The remote to publish to: <c>origin</c> when it exists, otherwise the only other one. Picking
    /// the first of several would publish a version number somewhere other people read. Null means
    /// the caller must not offer a remote at all.
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
    /// Asks Git whether a tag name is acceptable. <c>check-ref-format</c> on the full
    /// <c>refs/tags/</c> path rather than <c>--branch</c>, whose rules forbid a name Git accepts
    /// perfectly well as a tag.
    /// </summary>
    public async Task<TagOutcome> ValidateAsync(
        RepositoryInfo repository,
        string name,
        CancellationToken cancellationToken)
    {
        string tag = name.Trim();

        if (!RefName.LooksValid(tag))
            return TagOutcome.Failed($"'{tag}' is not a valid tag name.");

        GitResult result = await git.ReadAsync(
            repository.Root,
            ["check-ref-format", $"refs/tags/{tag}"],
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded
            ? TagOutcome.Ok
            : TagOutcome.Failed($"Git rejected '{tag}' as a tag name.\n\n{result.ErrorText}");
    }

    /// <summary>
    /// Fast, offline check used for live feedback while typing. The same <see cref="RefName"/> pattern
    /// a branch name is checked against -- the two used to carry byte-identical copies of it.
    /// </summary>
    public static bool LooksValid(string name) => RefName.LooksValid(name);
}

/// <param name="Date">Creation date. The tag's own when annotated, the commit's otherwise.</param>
/// <param name="Subject">
/// First line of the tag message. <b>For a lightweight tag this is the tagged commit's subject
/// instead</b>, because <c>contents:subject</c> falls through to the object the ref points at.
/// That is why the window shows it only when <see cref="IsAnnotated"/>.
/// </param>
public sealed record GitTag(string Name, bool IsAnnotated, string Target, string Date, string Subject);

public sealed record TagOutcome(bool Succeeded, string? GitError)
{
    public static readonly TagOutcome Ok = new(true, null);

    public static TagOutcome Failed(string error) => new(false, error);
}
