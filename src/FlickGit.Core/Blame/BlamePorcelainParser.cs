using System.Globalization;

namespace FlickGit.Blame;

/// <summary>
/// Parses <c>git blame --porcelain</c>.
///
/// The format is <b>line-oriented</b>, not NUL-delimited, so this is the one parser in the product
/// that does not go through <c>NulFieldReader</c>. It is still machine-readable output rather than
/// the human form CLAUDE.md forbids parsing: `--porcelain` is a documented contract, where plain
/// `git blame` is a column layout that moves with the terminal width and the user's `blame.*`
/// settings.
///
/// <code>
/// &lt;sha&gt; &lt;origLine&gt; &lt;finalLine&gt; [&lt;linesInGroup&gt;]   header, before every line
/// author o0Zz                                       metadata, FIRST appearance of the sha only
/// author-time 1787382126
/// author-tz +0200
/// summary Remove pause/resume shell (Useless)
/// previous dc3e886… README.md                       the walk-back target, and the path there
/// filename README.md
/// \tthe line's text                                 content, always TAB-prefixed
/// </code>
///
/// Four traps, all of them load-bearing:
///
/// <list type="bullet">
/// <item><description><b>Metadata appears once per commit, not once per line.</b> Every later line
/// of the same commit carries the header alone, so commits are cached by sha and re-attached. A
/// parser that expects the block every time loses the author on every line but the first of each
/// run.</description></item>
/// <item><description><b>The content line is found by its leading TAB</b>, never by exhausting the
/// known metadata keys. A <c>summary</c> is arbitrary user text and a <c>filename</c> is an
/// arbitrary path — a commit message that reads like a header field would otherwise shift the
/// parse.</description></item>
/// <item><description><b><c>previous</c> and <c>boundary</c> are the feature.</b> They are why the
/// viewer can step back without appending <c>^</c> to anything, and why it follows a rename: Git
/// names both the commit and the path the file had there.</description></item>
/// <item><description><b>A sha of forty zeros is "not committed yet"</b>, which is an ordinary
/// result of blaming the working tree rather than a failure.</description></item>
/// </list>
/// </summary>
public static class BlamePorcelainParser
{
    public static IReadOnlyList<BlameLine> Parse(string stdout)
    {
        if (stdout.Length == 0)
            return [];

        var lines = new List<BlameLine>();

        //Commits, by sha. This is the cache that trap 1 is about: the entry is built from the block
        //after the first header and reused by every later line naming the same sha.
        var commits = new Dictionary<string, Pending>(StringComparer.Ordinal);

        string? sha = null;
        int number = 0;

        foreach (string raw in stdout.Split('\n'))
        {
            //Git terminates with \n; a CRLF-normalising layer anywhere upstream would leave the \r
            //on every header, and the sha would then never match the cache.
            string line = raw.TrimEnd('\r');

            if (line.Length == 0)
                continue;

            if (line[0] == '\t')
            {
                //The content line closes the record. Everything needed is already known.
                if (sha is null)
                    continue;

                Pending pending = commits.TryGetValue(sha, out Pending? found) ? found : new Pending(sha);

                lines.Add(new BlameLine(number, pending.ToCommit(), line[1..]));
                sha = null;
                continue;
            }

            //A header starts a record: "<sha> <orig> <final> [<count>]". The fourth field is only
            //present on the first line of a group, so it is read as optional rather than required.
            if (IsHeader(line, out string headerSha, out int finalLine))
            {
                sha = headerSha;
                number = finalLine;

                if (!commits.ContainsKey(headerSha))
                    commits[headerSha] = new Pending(headerSha);

                continue;
            }

            //Metadata for the record in progress.
            if (sha is null || !commits.TryGetValue(sha, out Pending? commit))
                continue;

            Apply(commit, line);
        }

        return lines;
    }

    /// <summary>
    /// A header is a 40-character hex sha followed by at least two numbers.
    ///
    /// Tested by shape rather than by "is not a known metadata key", because the set of metadata
    /// keys is Git's to extend — a future <c>author-signature</c> line must be skipped, not mistaken
    /// for a header.
    /// </summary>
    private static bool IsHeader(string line, out string sha, out int finalLine)
    {
        sha = string.Empty;
        finalLine = 0;

        string[] parts = line.Split(' ');

        if (parts.Length < 3 || parts[0].Length != 40)
            return false;

        foreach (char c in parts[0])
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        //The third field is the line number in the file being blamed, which is the one the viewer
        //shows. The second is where the line came from in the original commit, and nothing uses it.
        if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out finalLine))
            return false;

        sha = parts[0];
        return true;
    }

    private static void Apply(Pending commit, string line)
    {
        //Split once: every metadata value may contain spaces, and two of them -- summary and
        //filename -- may contain anything at all.
        int space = line.IndexOf(' ');
        string key = space < 0 ? line : line[..space];
        string value = space < 0 ? string.Empty : line[(space + 1)..];

        switch (key)
        {
            case "author":
                commit.Author = value;
                break;

            case "author-time":
                commit.Time = long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long seconds)
                    ? seconds
                    : commit.Time;
                break;

            case "author-tz":
                commit.Zone = value;
                break;

            case "summary":
                commit.Summary = value;
                break;

            case "filename":
                commit.Filename = value;
                break;

            case "boundary":
                //No value: the key is the whole line.
                commit.IsBoundary = true;
                break;

            case "previous":
                //"previous <sha> <path>". The path may contain spaces, so only the sha is split off.
                int gap = value.IndexOf(' ');

                if (gap > 0)
                {
                    commit.PreviousSha = value[..gap];
                    commit.PreviousPath = value[(gap + 1)..];
                }

                break;
        }
    }

    /// <summary>
    /// A commit being accumulated across its metadata block.
    ///
    /// Mutable and private, so <see cref="BlameCommit"/> can stay an immutable record: the lines of
    /// one group arrive before the block is complete only in the sense that the header precedes it,
    /// and <see cref="ToCommit"/> is called at the content line, by which point it is.
    /// </summary>
    private sealed class Pending(string sha)
    {
        public string Author = string.Empty;
        public long Time;
        public string Zone = "+0000";
        public string Summary = string.Empty;
        public string Filename = string.Empty;
        public string? PreviousSha;
        public string? PreviousPath;
        public bool IsBoundary;

        private BlameCommit? _built;

        public BlameCommit ToCommit() => _built ??= new BlameCommit
        {
            Sha = sha,
            Author = Author,
            When = Combine(Time, Zone),
            Summary = Summary,
            Filename = Filename,
            PreviousSha = PreviousSha,
            PreviousPath = PreviousPath,
            IsBoundary = IsBoundary,
        };

        /// <summary>
        /// Epoch seconds plus a separate "+0200", which is how the porcelain format spells a time.
        ///
        /// The zone is kept rather than discarded so a commit made in another timezone still reads
        /// as the hour its author saw, which is the hour they would name if you asked them about it.
        /// </summary>
        private static DateTimeOffset Combine(long seconds, string zone)
        {
            TimeSpan offset = TimeSpan.Zero;

            if (zone.Length == 5 && (zone[0] == '+' || zone[0] == '-')
                && int.TryParse(zone.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
                && int.TryParse(zone.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes))
            {
                offset = new TimeSpan(hours, minutes, 0);

                if (zone[0] == '-')
                    offset = -offset;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds).ToOffset(offset);
            }
            catch (ArgumentOutOfRangeException)
            {
                //A clock-skewed commit with an absurd timestamp is a real thing in old history, and
                //it must not take the whole blame down with it.
                return DateTimeOffset.UnixEpoch;
            }
        }
    }
}
