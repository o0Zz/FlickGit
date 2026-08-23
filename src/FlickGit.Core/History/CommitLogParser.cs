using System.Globalization;
using FlickGit.Status;

namespace FlickGit.History;

/// <summary>
/// Parses the <c>git log --format=</c> stream this product asks for.
///
/// CLAUDE.md forbids parsing human-readable Git output, so the format is chosen here and the
/// parser is its other half. Three decisions in <see cref="Format"/> are load-bearing, and all
/// three are about a commit message being arbitrary user text:
///
/// <list type="bullet">
/// <item><description><b>%B is last, and it is the only free-text field.</b> The split is bounded
/// at the field count, so a message containing a newline, a separator byte or anything else lands
/// in the final slot verbatim and cannot shift a field.</description></item>
/// <item><description><b>Records are NUL-terminated</b>, which is the one byte a commit message
/// cannot contain — the same reason every <c>-z</c> parser in the product splits on it.</description></item>
/// <item><description><b>%aI, not %ad.</b> %ad is reshaped by <c>--date</c> and by the user's
/// <c>log.date</c>; %aI is strict ISO 8601 whatever the configuration says.</description></item>
/// </list>
/// </summary>
public static class CommitLogParser
{
    /// <summary>Full sha, short sha, parents, author, date, refs, message.</summary>
    public const string Format = "%H%x1f%h%x1f%P%x1f%an%x1f%aI%x1f%D%x1f%B%x00";

    private const char FieldSeparator = '\x1f';
    private const int FieldCount = 7;

    public static IReadOnlyList<LogCommit> Parse(string stdout)
    {
        var commits = new List<LogCommit>();
        var reader = new NulFieldReader(stdout);

        while (reader.TryRead(out string record))
        {
            //A --format containing placeholders behaves as `tformat:`, so Git appends a newline
            //after every record -- *after* the NUL this format ends with. Every record but the
            //first therefore arrives with that newline in front of it, and the stream ends with a
            //record that is nothing else. Without this the sha of every commit after the first
            //begins with "\n" and nothing matches.
            record = record.TrimStart('\n', '\r');

            if (record.Length == 0)
                continue;

            string[] fields = record.Split(FieldSeparator, FieldCount);

            if (fields.Length < FieldCount)
                continue;

            if (!DateTimeOffset.TryParse(
                    fields[4],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset when))
            {
                when = default;
            }

            commits.Add(new LogCommit
            {
                Sha = fields[0],
                ShortSha = fields[1],

                //RemoveEmptyEntries is the whole root-commit case: %P is empty there, and a plain
                //Split reports one parent whose sha is the empty string -- which would become a
                //base spec of "" and turn the repository's first commit into a Git error.
                Parents = fields[2].Split(' ', StringSplitOptions.RemoveEmptyEntries),

                Author = fields[3],
                When = when,
                Refs = fields[5],

                //%B always ends with a newline of its own.
                Message = fields[6].TrimEnd('\n', '\r'),
            });
        }

        return commits;
    }
}
