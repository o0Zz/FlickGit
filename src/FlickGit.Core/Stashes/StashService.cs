using System.Globalization;
using FlickGit.Git;
using FlickGit.Models;
using FlickGit.Repositories;
using FlickGit.Status;

namespace FlickGit.Stashes;

/// <summary>
/// The stash: listing it, putting work away, taking it back, and throwing one out.
///
/// <b>Every operation on an existing stash re-reads the list first and refuses if the reference no
/// longer names the same commit.</b> That check is the whole reason this service exists rather than
/// four calls made from a window. <c>SwitchService</c> already states the rule it protects --
/// "<c>stash@{0}</c> is whatever was stashed most recently, which on a busy working tree is not
/// necessarily ours, and restoring the wrong stash is indistinguishable from losing the user's
/// work" -- and escapes it by finding its own stash by a generated message. A window that
/// <i>lists</i> stashes cannot do that: the user points at a row, so the row has to be
/// addressable. Verifying the sha is how the same guarantee is kept from the other direction.
///
/// Four subcommands, and the ones that are absent are absent on purpose:
///
/// <list type="bullet">
/// <item><description><b>No <c>clear</c>.</b> One click that destroys every saved change in the
/// repository, with no per-stash intent behind any of it. <c>drop</c> is the operation that has a
/// user pointing at a thing, and it is the only one offered.</description></item>
/// <item><description><b>No <c>apply</c>.</b> It is <c>pop</c> that keeps the entry -- a second
/// spelling of one operation, for a case (replaying one stash onto two branches) that Hard
/// Requirement 2 says waits for somebody to ask.</description></item>
/// <item><description><b>No <c>stash branch</c>, no <c>--all</c>, no <c>--keep-index</c>, no
/// <c>--staged</c>, and no <c>--force</c> in any spelling.</b> <c>--include-untracked</c> is the
/// only optional flag in the file.</description></item>
/// </list>
/// </summary>
public sealed class StashService(IGitProcessRunner git, RepositoryService repositories)
{
    /// <summary>
    /// Reference, stash commit, creation date, reflog subject.
    ///
    /// The same three decisions <see cref="History.CommitLogParser"/> argues, for the same reasons:
    /// <c>%gs</c> is last because it is the only free-text field, records are NUL-terminated because
    /// NUL is the one byte a message cannot contain, and the date is <c>%cI</c> rather than
    /// <c>%cd</c> so the user's <c>log.date</c> cannot reshape it.
    /// </summary>
    private const string Format = "%gd%x1f%H%x1f%cI%x1f%gs%x00";

    private const char FieldSeparator = '\x1f';

    private const int FieldCount = 4;

    /// <summary>The two subjects Git writes for a stash, and the only two this parses.</summary>
    private const string WipPrefix = "WIP on ";

    private const string OnPrefix = "On ";

    /// <summary>Every stash, newest first -- which is the order the reflog is in.</summary>
    public async Task<IReadOnlyList<GitStash>> ListAsync(
        RepositoryInfo repository,
        CancellationToken cancellationToken)
    {
        //An explicit --format, because the default `git stash list` output is shaped for a terminal
        //and CLAUDE.md forbids parsing that. A repository with no stash answers with nothing and
        //exit 0, which parses to an empty list rather than an error.
        GitResult result = await git.ReadAsync(
            repository.Root,
            ["stash", "list", "--format=" + Format],
            cancellationToken).ConfigureAwait(false);

        return result.Succeeded ? Parse(result.StdOut) : [];
    }

    /// <summary>
    /// Puts the working tree away.
    ///
    /// Nothing to confirm: the work is recoverable by definition -- it is going into the list this
    /// window is showing, and <see cref="PopAsync"/> is one right-click away. That is the whole
    /// difference between this and a <c>restore</c>.
    /// </summary>
    /// <param name="message">
    /// The user's description, or null/blank to let Git write its own.
    /// </param>
    /// <param name="includeUntracked">
    /// Files Git has never seen. Offered as a choice rather than always on, because it is the one
    /// decision here with a real cost either way: excluded, a brand-new file is left behind by an
    /// operation the user read as "put my work away"; included, it leaves the working tree.
    /// </param>
    public async Task<StashOutcome> PushAsync(
        RepositoryInfo repository,
        string? message,
        bool includeUntracked,
        CancellationToken cancellationToken)
    {
        //The top of the list before, so that the top of the list afterwards answers "was anything
        //actually stashed". `git stash push` with a clean working tree exits 0, does nothing, and
        //says "No local changes to save" -- and matching that sentence is what CLAUDE.md rules out,
        //so the list answers instead. Null when the repository has no stash at all yet.
        string? before = (await ListAsync(repository, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault()?.Sha;

        var args = new List<string> { "stash", "push" };

        //Not --all, which is a different operation wearing a similar name: --all takes *ignored*
        //files too, so a repository with an un-fetched node_modules would put a gigabyte into a
        //stash the user thought was their afternoon's work.
        if (includeUntracked)
            args.Add("--include-untracked");

        if (message is { } text && text.Trim().Length > 0)
        {
            //-m consumes the next argument whatever it begins with, so a message starting with a
            //dash cannot be read as an option and needs no `--` in front of it. Omitted altogether
            //when blank rather than passed empty: `-m ""` produces a subject that reads "On main: "
            //and stops, where leaving it out lets Git name the commit the stash sits on.
            args.Add("-m");
            args.Add(text.Trim());
        }

        GitResult result = await git.RunAsync(repository.Root, args, cancellationToken).ConfigureAwait(false);

        //A push rewrites the working tree, and .gitmodules is a file in it.
        repositories.Invalidate(repository.Root);

        if (!result.Succeeded)
            return StashOutcome.Failed(result.ErrorText);

        string? after = (await ListAsync(repository, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault()?.Sha;

        //Nothing new on top. Not a failure -- a clean working tree is the ordinary way to get here
        //-- but reporting success would have the window say "Stashed" over an unchanged list.
        return after is null || string.Equals(after, before, StringComparison.Ordinal)
            ? StashOutcome.Refused(StashRefusal.NothingToStash)
            : StashOutcome.Ok;
    }

    /// <summary>
    /// Takes a stash back: applies it and, on success, removes the entry.
    ///
    /// <c>pop</c> rather than <c>apply</c> for the reason <c>SwitchService</c> gives -- on success
    /// the stash should be gone, and on failure Git keeps it, which is what makes the failure
    /// recoverable. <b>That is unconditional</b>: Git applies and only then drops, so a pop that
    /// conflicts or is refused always leaves the stash exactly where it was, and the caller can say
    /// so without checking.
    ///
    /// Not confirmed. It restores work rather than discarding any, and Git refuses outright rather
    /// than overwriting a file that is in the way.
    /// </summary>
    public async Task<StashOutcome> PopAsync(
        RepositoryInfo repository,
        GitStash stash,
        CancellationToken cancellationToken)
    {
        if (!await StillThereAsync(repository, stash, cancellationToken).ConfigureAwait(false))
            return StashOutcome.Refused(StashRefusal.Moved);

        //No `--`: `git stash pop` documents none, and the reference is not a value the user typed --
        //it came out of `%gd`, so it begins with `stash@{`.
        GitResult result = await git.RunAsync(
            repository.Root,
            ["stash", "pop", stash.Reference],
            cancellationToken).ConfigureAwait(false);

        repositories.Invalidate(repository.Root);

        return result.Succeeded ? StashOutcome.Ok : StashOutcome.Failed(result.ErrorText);
    }

    /// <summary>
    /// Throws a stash away without applying it.
    ///
    /// <b>The one operation in this file that destroys something, and the reason
    /// <see cref="StashRefusal.Moved"/> is checked before it.</b> A stash has no reflog of its own,
    /// so once the entry is gone there is nothing in FlickGit that finds it again -- the same
    /// argument <c>ActionSafety</c> makes for <c>tag -d</c>. The caller confirms first, in words
    /// naming the stash.
    /// </summary>
    public async Task<StashOutcome> DropAsync(
        RepositoryInfo repository,
        GitStash stash,
        CancellationToken cancellationToken)
    {
        if (!await StillThereAsync(repository, stash, cancellationToken).ConfigureAwait(false))
            return StashOutcome.Refused(StashRefusal.Moved);

        GitResult result = await git.RunAsync(
            repository.Root,
            ["stash", "drop", stash.Reference],
            cancellationToken).ConfigureAwait(false);

        //No invalidation: a drop touches the reflog and nothing in the working tree, so none of the
        //facts RepositoryInfo caches can have changed.
        return result.Succeeded ? StashOutcome.Ok : StashOutcome.Failed(result.ErrorText);
    }

    /// <summary>
    /// True when <paramref name="stash"/>'s reference still names <paramref name="stash"/>'s commit.
    ///
    /// One extra read before every pop and every drop. It buys the only guarantee that matters
    /// here: the stash the user was shown is the stash the command reaches. A pop of the wrong
    /// stash is a merge nobody asked for; a drop of the wrong stash is somebody's work gone.
    /// </summary>
    private async Task<bool> StillThereAsync(
        RepositoryInfo repository,
        GitStash stash,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<GitStash> current = await ListAsync(repository, cancellationToken).ConfigureAwait(false);

        return current.Any(candidate =>
            string.Equals(candidate.Reference, stash.Reference, StringComparison.Ordinal)
            && string.Equals(candidate.Sha, stash.Sha, StringComparison.Ordinal));
    }

    /// <summary>
    /// The <see cref="Format"/> stream. A record short of its fields is dropped rather than thrown
    /// over: a truncated read should cost the last row, not the list.
    /// </summary>
    internal static IReadOnlyList<GitStash> Parse(string stdout)
    {
        var stashes = new List<GitStash>();
        var reader = new NulFieldReader(stdout);

        while (reader.TryRead(out string record))
        {
            //A --format holding placeholders behaves as `tformat:`, so Git appends a newline after
            //every record -- *after* the NUL this one ends with. Every record but the first arrives
            //with that newline in front of it. Without this the reference of every stash after the
            //first begins with "\n".
            record = record.TrimStart('\n', '\r');

            if (record.Length == 0)
                continue;

            string[] fields = record.Split(FieldSeparator, FieldCount);

            if (fields.Length < FieldCount || fields[0].Length == 0)
                continue;

            if (!DateTimeOffset.TryParse(
                    fields[2],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset created))
            {
                created = default;
            }

            (string branch, string message) = ParseSubject(fields[3]);

            stashes.Add(new GitStash(
                Reference: fields[0],
                Sha: fields[1],
                Branch: branch,
                Message: message,
                Created: created));
        }

        return stashes;
    }

    /// <summary>
    /// Splits a reflog subject into the branch and the description.
    ///
    /// Git writes one of two: <c>On &lt;branch&gt;: &lt;message&gt;</c> when the user gave one, and
    /// <c>WIP on &lt;branch&gt;: &lt;sha&gt; &lt;subject&gt;</c> when they did not.
    ///
    /// <b>Cut at the first <c>": "</c>, and that is exact rather than a guess:</b>
    /// <c>check-ref-format</c> refuses a colon in a ref name, so the first colon after the prefix
    /// always ends the branch however many the message then contains. A subject matching neither
    /// prefix is returned whole with no branch -- it is still the only description the stash has,
    /// and dropping it to keep a tidy parse would blank the row.
    /// </summary>
    internal static (string Branch, string Message) ParseSubject(string subject)
    {
        string text = subject.Trim();

        string rest =
            text.StartsWith(WipPrefix, StringComparison.Ordinal) ? text[WipPrefix.Length..]
            : text.StartsWith(OnPrefix, StringComparison.Ordinal) ? text[OnPrefix.Length..]
            : string.Empty;

        if (rest.Length == 0)
            return (string.Empty, text);

        int separator = rest.IndexOf(": ", StringComparison.Ordinal);

        return separator < 0
            ? (string.Empty, text)
            : (rest[..separator], rest[(separator + 2)..]);
    }
}
