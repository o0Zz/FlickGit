using FlickGit.Models;

namespace FlickGit.Status;

/// <summary>
/// Parses <c>git status --porcelain=v2 --branch -z</c>.
///
/// Porcelain v2 rather than v1 for two reasons: it carries the branch, upstream and
/// ahead/behind counts in the same invocation the file list comes from — so drawing the
/// commit window header costs no extra process — and its format is documented as stable
/// for machine consumption. CLAUDE.md: "Never parse human-readable `git status` output."
///
/// Record kinds, each one NUL-terminated:
/// <code>
/// # branch.oid &lt;commit&gt; | (initial)
/// # branch.head &lt;branch&gt; | (detached)
/// # branch.upstream &lt;upstream&gt;
/// # branch.ab +&lt;ahead&gt; -&lt;behind&gt;
/// 1 &lt;XY&gt; &lt;sub&gt; &lt;mH&gt; &lt;mI&gt; &lt;mW&gt; &lt;hH&gt; &lt;hI&gt; &lt;path&gt;
/// 2 &lt;XY&gt; &lt;sub&gt; &lt;mH&gt; &lt;mI&gt; &lt;mW&gt; &lt;hH&gt; &lt;hI&gt; &lt;X&gt;&lt;score&gt; &lt;path&gt;    then &lt;origPath&gt; as its own field
/// u &lt;XY&gt; &lt;sub&gt; &lt;m1&gt; &lt;m2&gt; &lt;m3&gt; &lt;mW&gt; &lt;h1&gt; &lt;h2&gt; &lt;h3&gt; &lt;path&gt;
/// ? &lt;path&gt;
/// ! &lt;path&gt;
/// </code>
///
/// The trap is the <c>2</c> record. In <c>-z</c> mode the original path is a *separate*
/// NUL-terminated field rather than being appended after a TAB, so a parser that reads
/// one record per field silently treats the old path as the next entry.
/// </summary>
public static class PorcelainV2Parser
{
    public static PorcelainStatus Parse(string stdout)
    {
        var files = new List<GitFileChange>();
        string? branch = null;
        string? upstream = null;
        string? headCommit = null;
        int ahead = 0;
        int behind = 0;
        bool detached = false;
        bool unborn = false;

        var reader = new NulFieldReader(stdout);

        while (reader.TryRead(out string record))
        {
            if (record.Length == 0)
                continue;

            switch (record[0])
            {
                case '#':
                    ReadHeader(record, ref branch, ref upstream, ref headCommit,
                               ref ahead, ref behind, ref detached, ref unborn);
                    break;

                case '1':
                    if (ParseOrdinary(record) is { } ordinary)
                        files.Add(ordinary);
                    break;

                case '2':
                    //The extra field. Consumed here, inside the '2' case, which is the
                    //only place it is legal -- read it anywhere else and the whole
                    //remainder of the stream shifts by one.
                    reader.TryRead(out string originalPath);
                    if (ParseRenamed(record, originalPath) is { } renamed)
                        files.Add(renamed);
                    break;

                case 'u':
                    if (ParseUnmerged(record) is { } unmerged)
                        files.Add(unmerged);
                    break;

                case '?':
                    //"? " -- everything after the single space is the path, spaces and all.
                    if (record.Length > 2)
                        files.Add(Untracked(record[2..], GitChangeType.Untracked));
                    break;

                case '!':
                    if (record.Length > 2)
                        files.Add(Untracked(record[2..], GitChangeType.Ignored));
                    break;

                default:
                    //An unknown record kind from a future Git. Skipping one entry beats
                    //throwing away the list.
                    break;
            }
        }

        return new PorcelainStatus
        {
            Branch = branch,
            Upstream = upstream,
            HeadCommit = headCommit,
            Ahead = ahead,
            Behind = behind,
            IsDetachedHead = detached,
            IsUnborn = unborn,
            Files = files,
        };
    }

    private static void ReadHeader(
        string record,
        ref string? branch,
        ref string? upstream,
        ref string? headCommit,
        ref int ahead,
        ref int behind,
        ref bool detached,
        ref bool unborn)
    {
        //"# branch.head main" -> key "branch.head", value "main". Split on the first two
        //spaces only: a branch name cannot contain a space, but staying conservative
        //here costs nothing.
        string[] parts = record.Split(' ', 3);
        if (parts.Length < 3)
            return;

        string key = parts[1];
        string value = parts[2];

        switch (key)
        {
            case "branch.oid":
                //"(initial)" is an unborn HEAD: a fresh repository with no commit, where
                //the left side of every diff is empty and there is nothing to diff
                //against.
                if (value == "(initial)")
                    unborn = true;
                else
                    headCommit = value;
                break;

            case "branch.head":
                if (value == "(detached)")
                    detached = true;
                else
                    branch = value;
                break;

            case "branch.upstream":
                upstream = value;
                break;

            case "branch.ab":
                //"+2 -0". Absent entirely when the branch has no upstream, which is why
                //Ahead and Behind default to 0 rather than to null.
                foreach (string token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.Length < 2 || !int.TryParse(token[1..], out int count))
                        continue;

                    if (token[0] == '+')
                        ahead = count;
                    else if (token[0] == '-')
                        behind = count;
                }

                break;
        }
    }

    /// <summary>
    /// <c>1 &lt;XY&gt; &lt;sub&gt; &lt;mH&gt; &lt;mI&gt; &lt;mW&gt; &lt;hH&gt; &lt;hI&gt; &lt;path&gt;</c>
    ///
    /// Eight fixed fields then the path, so the split is bounded at 9 — the path keeps
    /// its spaces because it is whatever is left.
    /// </summary>
    private static GitFileChange? ParseOrdinary(string record)
    {
        string[] parts = record.Split(' ', 9);
        if (parts.Length < 9 || parts[1].Length < 2)
            return null;

        return new GitFileChange
        {
            Path = parts[8],
            IndexStatus = GitChangeTypeExtensions.FromStatusChar(parts[1][0]),
            WorkTreeStatus = GitChangeTypeExtensions.FromStatusChar(parts[1][1]),
            IsStaged = parts[1][0] != '.',
        };
    }

    /// <summary>
    /// <c>2 &lt;XY&gt; &lt;sub&gt; &lt;mH&gt; &lt;mI&gt; &lt;mW&gt; &lt;hH&gt; &lt;hI&gt; &lt;X&gt;&lt;score&gt; &lt;path&gt;</c>
    /// plus <paramref name="originalPath"/>, which arrived as the following field.
    /// </summary>
    private static GitFileChange? ParseRenamed(string record, string originalPath)
    {
        string[] parts = record.Split(' ', 10);
        if (parts.Length < 10 || parts[1].Length < 2)
            return null;

        return new GitFileChange
        {
            Path = parts[9],
            OldPath = originalPath.Length > 0 ? originalPath : null,
            IndexStatus = GitChangeTypeExtensions.FromStatusChar(parts[1][0]),
            WorkTreeStatus = GitChangeTypeExtensions.FromStatusChar(parts[1][1]),
            IsStaged = parts[1][0] != '.',
        };
    }

    /// <summary>
    /// <c>u &lt;XY&gt; &lt;sub&gt; &lt;m1&gt; &lt;m2&gt; &lt;m3&gt; &lt;mW&gt; &lt;h1&gt; &lt;h2&gt; &lt;h3&gt; &lt;path&gt;</c>
    ///
    /// Reported as Conflicted on both sides regardless of the XY letters. The letters do
    /// distinguish "both modified" from "deleted by them", but every one of those states
    /// means the same thing to this tool: not safe to edit, stage or commit.
    /// </summary>
    private static GitFileChange? ParseUnmerged(string record)
    {
        string[] parts = record.Split(' ', 11);
        if (parts.Length < 11)
            return null;

        return new GitFileChange
        {
            Path = parts[10],
            IndexStatus = GitChangeType.Conflicted,
            WorkTreeStatus = GitChangeType.Conflicted,
            IsStaged = false,
        };
    }

    private static GitFileChange Untracked(string path, GitChangeType type) =>
        new()
        {
            Path = path,
            WorkTreeStatus = type,
            IndexStatus = GitChangeType.None,
            IsUntracked = true,
            IsStaged = false,

            //Unchecked by default. CLAUDE.md, "Staging Defaults": this one rule is what
            //keeps .env, appsettings.Development.json and stray dumps out of a hurried
            //commit.
            IsSelected = false,
        };
}

/// <summary>What one porcelain v2 invocation yielded, before numstat counts are merged in.</summary>
public sealed record PorcelainStatus
{
    public string? Branch { get; init; }
    public string? Upstream { get; init; }
    public string? HeadCommit { get; init; }
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public bool IsDetachedHead { get; init; }
    public bool IsUnborn { get; init; }
    public IReadOnlyList<GitFileChange> Files { get; init; } = [];
}
