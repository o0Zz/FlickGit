using FlickGit.Diff;
using FlickGit.History;
using FlickGit.Models;

namespace FlickGit.Stashes;

/// <summary>
/// One entry in <c>git stash list</c> -- a snapshot of the working tree that was put away.
///
/// <b><see cref="Sha"/> is the field that makes this record safe to act on.</b> A stash is
/// addressed by a reflog selector, and a reflog selector is a <i>position</i>: dropping
/// <c>stash@{1}</c> drops whatever is second in the list at the moment the command runs, which is
/// not necessarily what was second when the window painted it. So the sha travels with the
/// reference, and every operation checks the two still agree before Git is asked anything. See
/// <c>StashService</c>.
/// </summary>
/// <param name="Reference">
/// <c>stash@{0}</c>, exactly as <c>%gd</c> reported it. <b>Never assembled from an index here</b> --
/// the same rule blame follows for its <c>previous</c> step: the spelling Git gave is the spelling
/// Git is handed back.
/// </param>
/// <param name="Sha">
/// The stash commit. Unique, stable, and the only thing about a stash that does not move when
/// another one is pushed or popped.
/// </param>
/// <param name="Branch">
/// The branch the stash was made on, parsed out of the reflog subject. <c>(no branch)</c> is Git's
/// own wording for a stash made with HEAD detached, and it is kept verbatim rather than blanked:
/// "which branch was I on" is the first question asked of a stash, and "not one" is an answer.
/// </param>
/// <param name="Message">
/// What the row shows. The message the user typed, or -- when they typed none -- Git's own
/// <c>&lt;sha&gt; &lt;subject&gt;</c> naming the commit the stash was made on top of.
/// </param>
/// <param name="Created">
/// From <c>%cI</c>, so it is strict ISO 8601 whatever the user's <c>log.date</c> says. Kept as a
/// <see cref="DateTimeOffset"/> rather than as Git's string because a stash made an hour ago and
/// one made last month are told apart by the time of day, which a date alone loses.
/// </param>
/// <param name="Parents">
/// The stash commit's parents, in Git's order, from <c>%P</c> — which costs nothing, being another
/// placeholder on a read that was happening anyway.
///
/// <b>A stash is not one commit but two or three</b>, and that is what makes its contents
/// viewable: the first parent is the commit HEAD was on, the second is the index at the time, and
/// the third — present only when the stash was made with untracked files — is a commit holding
/// nothing but those. So <see cref="BaseSha"/> ↔ <see cref="Sha"/> is the tracked half of the
/// stash, and the empty tree ↔ <see cref="UntrackedSha"/> is the rest of it.
/// </param>
public sealed record GitStash(
    string Reference,
    string Sha,
    string Branch,
    string Message,
    DateTimeOffset Created,
    IReadOnlyList<string> Parents)
{
    /// <summary>
    /// The commit the stash was made on: the left side of the stash's own diff, and exactly what
    /// <c>git stash show</c> compares against.
    ///
    /// Null only for a parentless stash commit, which is not a state Git can produce — it refuses to
    /// stash before the initial commit exists — so this is a guard against a malformed read rather
    /// than a case with behaviour of its own.
    /// </summary>
    public string? BaseSha => Parents.Count > 0 ? Parents[0] : null;

    /// <summary>
    /// The commit holding the untracked files that went into the stash, or null when none did.
    ///
    /// <b>These files are in no other tree.</b> They are absent from the stash commit itself, which
    /// is why a plain <see cref="BaseSha"/> ↔ <see cref="Sha"/> diff — <c>git stash show</c>'s own
    /// blind spot — silently omits them.
    /// </summary>
    public string? UntrackedSha => Parents.Count > 2 ? Parents[2] : null;

    /// <summary>
    /// The two sides of this stash's tracked changes, labelled for the viewer's header, or null when
    /// <see cref="BaseSha"/> is missing.
    /// </summary>
    public DiffRange? TrackedRange => BaseSha is { } parent
        ? new DiffRange(parent, Sha, $"{Short(parent)} ↔ {Reference}")
        : null;

    /// <summary>
    /// The two sides of the untracked files in this stash, or null when it holds none. The base is
    /// the empty tree, so every one of them reads as an addition — which is what it is.
    /// </summary>
    public DiffRange? UntrackedRange => UntrackedSha is { } untracked
        ? new DiffRange(CommitRange.EmptyTree, untracked, $"⌀ ↔ {Reference}")
        : null;

    /// <summary>Git's own abbreviation length, and the one the log window's labels use.</summary>
    private static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;
}

/// <summary>
/// One file in a stash, and which two revisions its diff is between.
///
/// <b>The range travels per file rather than per stash</b>, because a stash made with untracked
/// files spans two: its tracked changes are the stash commit against the commit it was made on, and
/// its untracked files are a separate commit against the empty tree. One range for the whole list
/// would have to drop one half of it.
/// </summary>
public sealed record StashChange(GitFileChange File, DiffRange Range);

/// <summary>
/// Why a stash operation did nothing. Both values mean the repository is exactly as it was.
/// </summary>
public enum StashRefusal
{
    None,

    /// <summary>
    /// There was nothing to put away.
    ///
    /// The one value here established <i>after</i> the command rather than before it, because the
    /// only thing Git offers on the way in is the sentence "No local changes to save" -- and
    /// matching Git's English is what CLAUDE.md rules out. <c>StashService.PushAsync</c> reads the
    /// list either side of the push instead. Nothing was stashed either way.
    /// </summary>
    NothingToStash,

    /// <summary>
    /// The reference no longer names the stash it named when the list was read, so nothing was
    /// asked of Git.
    ///
    /// <b>This is the refusal the feature exists around.</b> A stash list is renumbered by every
    /// push and every pop, including ones made in a terminal, by an IDE, or by FlickGit's own
    /// stash-switch-restore while this window sat open. Acting on the stale reference would pop or
    /// drop a stash the user never pointed at, and for a drop there is nothing here that could
    /// bring it back.
    /// </summary>
    Moved,
}

/// <param name="Refusal">
/// Set when nothing happened. <see cref="StashRefusal.Moved"/> means no command ran at all.
/// </param>
public sealed record StashOutcome(bool Succeeded, string? GitError, StashRefusal Refusal = StashRefusal.None)
{
    public static StashOutcome Ok { get; } = new(true, null);

    public static StashOutcome Failed(string error) => new(false, error);

    public static StashOutcome Refused(StashRefusal refusal) => new(false, null, refusal);
}

/// <summary>
/// How far a batch drop got.
///
/// <see cref="Dropped"/> is the field that makes a partial failure reportable. Dropping is
/// one command per stash, so a batch can stop in the middle — and the difference between "nothing
/// happened" and "three of your five are gone" is the whole of what the user needs to be told,
/// over a list that has already changed under them.
/// </summary>
/// <param name="Dropped">How many were actually dropped, whatever <paramref name="Outcome"/> says.</param>
public sealed record StashDropOutcome(int Dropped, StashOutcome Outcome);
