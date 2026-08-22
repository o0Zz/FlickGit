namespace FlickGit.Status;

/// <summary>
/// Parses <c>git diff --numstat -z</c>.
///
/// `status --porcelain=v2` gives the status letters but no line counts, so the commit
/// window's <c>+42 -17</c> columns come from here — one call for the working tree
/// against the index, one for the index against HEAD, both run in parallel with the
/// status call.
///
/// Two traps, both from CLAUDE.md, "Parsing traps":
///
/// <list type="bullet">
/// <item><description><b>-z changes the rename format.</b> A regular entry is
/// <c>added TAB removed TAB path NUL</c>. A rename or copy is
/// <c>added TAB removed TAB NUL oldPath NUL newPath NUL</c> — the third tab-field is
/// *empty* and the two paths follow as separate fields. There is no <c>old =&gt; new</c>
/// arrow to split on, which is exactly why a path containing a literal <c>=&gt;</c>
/// parses correctly here and would not under the non-<c>-z</c> format.</description></item>
/// <item><description><b>Binary files report <c>-</c> for both counts</b>, not 0. That
/// becomes <c>null</c> counts and <c>IsBinary</c>, so the UI can print "bin" instead of
/// a meaningless "+0 -0".</description></item>
/// </list>
/// </summary>
public static class NumstatParser
{
    public static IReadOnlyDictionary<string, NumstatEntry> Parse(string stdout)
    {
        //Ordinal, not OrdinalIgnoreCase: Git paths are case-sensitive, and a repository
        //can legitimately hold README.md and readme.md at once. Matching them case-
        //insensitively would merge two files' counts into one row.
        var entries = new Dictionary<string, NumstatEntry>(StringComparer.Ordinal);

        var reader = new NulFieldReader(stdout);

        while (reader.TryRead(out string record))
        {
            if (record.Length == 0)
                continue;

            //Exactly two tabs in a well-formed record: after added, after removed. The
            //remainder is the path, which may itself contain a tab -- hence the bounded
            //split rather than a plain Split('\t').
            string[] parts = record.Split('\t', 3);
            if (parts.Length < 3)
                continue;

            int? added = ParseCount(parts[0]);
            int? removed = ParseCount(parts[1]);
            bool isBinary = added is null || removed is null;

            string path = parts[2];
            string? oldPath = null;

            if (path.Length == 0)
            {
                //The rename form. Two more fields follow: pre-image then post-image.
                //Read both or discard the record -- a half-read rename would leave the
                //cursor mid-entry and corrupt everything after it.
                if (!reader.TryRead(out string preImage) || !reader.TryRead(out string postImage))
                    break;

                oldPath = preImage;
                path = postImage;
            }

            if (path.Length == 0)
                continue;

            entries[path] = new NumstatEntry(path, oldPath, added, removed, isBinary);
        }

        return entries;
    }

    /// <summary>
    /// A count, or null for <c>-</c> (binary). Anything unparseable is treated as
    /// binary too: "we could not count this" is the honest display, and it is the same
    /// display.
    /// </summary>
    private static int? ParseCount(string text) =>
        int.TryParse(text, out int value) ? value : null;
}

/// <param name="Path">Post-image path — the file as it exists now.</param>
/// <param name="OldPath">Pre-image path for a rename or copy, else null.</param>
/// <param name="Added">Lines added, or null when binary.</param>
/// <param name="Removed">Lines removed, or null when binary.</param>
/// <param name="IsBinary">Git reported <c>-</c> for both counts.</param>
public sealed record NumstatEntry(
    string Path,
    string? OldPath,
    int? Added,
    int? Removed,
    bool IsBinary);
