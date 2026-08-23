using FlickGit.Models;

namespace FlickGit.Status;

/// <summary>
/// Parses <c>git diff --name-status -z</c>.
///
/// <c>--numstat</c> gives the counts and no letter; this gives the letter and no counts, so the
/// log window's file list is the two merged on path — the same shape <see cref="StatusService"/>
/// already uses, for the same reason.
///
/// It exists because there is no other way to get the letter for a <b>commit range</b>.
/// <see cref="PorcelainV2Parser"/> reads <c>status --porcelain=v2</c>, which is working-tree-only
/// by construction, and inferring the letter from the counts is wrong in both directions: a file
/// that only gained lines is not necessarily added, and a file emptied to nothing was not
/// necessarily deleted.
///
/// The <c>-z</c> traps, which are not the numstat ones:
///
/// <list type="bullet">
/// <item><description><b>The letter and the path are separate NUL fields</b> — there is no tab
/// between them the way there is in the non-<c>-z</c> format.</description></item>
/// <item><description><b>The similarity score is glued to the letter</b> with no separator:
/// <c>R100</c>, <c>C85</c>. Only the first character is the status.</description></item>
/// <item><description><b>A rename or copy consumes two extra fields</b>, pre-image then
/// post-image. A parser that reads one field per record treats the old path as the next record's
/// status letter, and every row after it is garbage.</description></item>
/// </list>
/// </summary>
public static class NameStatusParser
{
    public static IReadOnlyDictionary<string, NameStatusEntry> Parse(string stdout)
    {
        //Ordinal for the reason NumstatParser gives: Git paths are case-sensitive, and merging
        //README.md with readme.md would merge two files into one row.
        var entries = new Dictionary<string, NameStatusEntry>(StringComparer.Ordinal);

        var reader = new NulFieldReader(stdout);

        while (reader.TryRead(out string status))
        {
            if (status.Length == 0)
                continue;

            GitChangeType type = GitChangeTypeExtensions.FromStatusChar(status[0]);

            //R100 and R51 are both "renamed". Nothing displays the score.
            bool twoPaths = status[0] is 'R' or 'C';

            if (!reader.TryRead(out string first))
                break;

            string path = first;
            string? oldPath = null;

            if (twoPaths)
            {
                //Both fields or abandon the stream. A half-read rename leaves the cursor
                //mid-record and corrupts everything after it -- the rule NumstatParser follows for
                //its own rename form.
                if (!reader.TryRead(out string postImage))
                    break;

                oldPath = first;
                path = postImage;
            }

            if (path.Length == 0)
                continue;

            entries[path] = new NameStatusEntry(path, oldPath, type);
        }

        return entries;
    }
}

/// <param name="Path">The post-image path — the same key <see cref="NumstatParser"/> uses, so the two merge on a straight lookup.</param>
/// <param name="OldPath">The pre-image, for a rename or a copy only.</param>
/// <param name="Status">Mapped through <see cref="GitChangeTypeExtensions.FromStatusChar"/>, so an unknown letter is <see cref="GitChangeType.None"/> rather than an exception.</param>
public sealed record NameStatusEntry(string Path, string? OldPath, GitChangeType Status);
