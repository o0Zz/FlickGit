using FlickGit.Models;

namespace FlickGit.Commits;

/// <summary>
/// A commit message prepared ahead of time in <c>&lt;git dir&gt;/SQUASH_MSG</c> and
/// <c>&lt;git dir&gt;/MERGE_MSG</c>.
///
/// <b>The files are Git's, not ours, and that is the whole point.</b> <c>git commit</c> builds
/// <c>COMMIT_EDITMSG</c> from the first source it finds, and these two sit above
/// <c>commit.template</c> in that order -- so they prefill a bare <c>git commit</c> in a terminal
/// and this window alike, and whichever of the two commits, Git unlinks them afterwards. That is why
/// nothing here writes or deletes: a second owner of the files is how they would go stale.
///
/// <b>No <c>git.exe</c>.</b> Like <see cref="Merges.MergeStateService"/>, this is a
/// <c>File.Exists</c> over a directory <see cref="RepositoryInfo.GitDirectory"/> already names, which
/// is what lets <see cref="Status.StatusService"/> carry the answer on every status read without
/// adding a process to the path CLAUDE.md budgets at 60 ms.
///
/// <b>Nothing here throws.</b> Every failure degrades to null, which means "no prepared message" and
/// leaves the window exactly as it behaves without one.
/// </summary>
public sealed class PreparedMessageService
{
    /// <summary>
    /// The prepared message, or null when there is none, when it holds nothing but comments, or when
    /// it could not be read.
    ///
    /// <b>Both files, concatenated, in Git's own order.</b> A squashed merge that conflicted leaves
    /// both behind, and <c>git commit</c> then prepends <c>SQUASH_MSG</c> -- which carries the
    /// subjects being squashed -- to <c>MERGE_MSG</c>, falling back to whichever exists on its own.
    /// Reading them in that order and joining is that rule with no branches in it. Reading only
    /// <c>MERGE_MSG</c> would miss the case the file was written for: <c>merge --squash</c> leaves no
    /// <c>MERGE_HEAD</c>, so it is the one prepared message this window's own commit sends.
    /// </summary>
    public string? Read(RepositoryInfo repository)
    {
        string gitDirectory = repository.GitDirectory;

        if (gitDirectory.Length == 0)
            return null;

        try
        {
            string raw = ReadFile(gitDirectory, "SQUASH_MSG") + ReadFile(gitDirectory, "MERGE_MSG");

            return raw.Length == 0 ? null : Clean(raw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            //A file being written as we read it, or one we may not open. Neither is worth failing a
            //status read over.
            return null;
        }
    }

    /// <summary>
    /// One file's text, or an empty string when it is not there -- empty rather than null so that the
    /// two concatenate to whichever of them exists.
    /// </summary>
    private static string ReadFile(string gitDirectory, string name)
    {
        string path = Path.Combine(gitDirectory, name);

        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    /// <summary>
    /// Drops comment lines and normalises the rest.
    ///
    /// <b>The <c>#</c> lines have to go.</b> When Git itself writes this file for a conflicted merge
    /// it appends a <c># Conflicts:</c> block, and Git strips those on the way out of the editor
    /// (<c>commit.cleanup=default</c>). We do not go out through an editor -- the message reaches
    /// <c>git commit -F</c>, which strips nothing -- so stripping here is what keeps that block out
    /// of the commit.
    ///
    /// Returns null rather than an empty string for a file with no message left in it, so the caller
    /// falls through to the behaviour it has without a prepared message instead of putting an empty
    /// one in the box.
    /// </summary>
    public static string? Clean(string raw)
    {
        //Line endings are normalised because a commit message is LF text by the time it reaches Git,
        //and the file may well have been written by an editor on either convention.
        IEnumerable<string> kept = raw
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !IsComment(line));

        string message = string.Join('\n', kept).Trim();

        return message.Length == 0 ? null : message;
    }

    /// <summary>
    /// A comment is <c>#</c> as the first non-blank character of a line. A <c>#</c> anywhere else on
    /// the line is ordinary text -- an issue number, or a colour.
    /// </summary>
    private static bool IsComment(string line)
    {
        foreach (char c in line)
        {
            if (!char.IsWhiteSpace(c))
                return c == '#';
        }

        return false;
    }
}
