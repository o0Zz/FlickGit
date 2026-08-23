namespace FlickGit.Shell;

/// <summary>What a folder turned out to be. <c>Unknown</c> is not <c>NotARepository</c>.</summary>
internal enum RepositoryVerdict
{
    /// <summary>Inside a repository, and <c>Branch</c> may name the checked-out branch.</summary>
    Repository,

    /// <summary>Definitely not inside one. The only verdict that may hide a menu entry.</summary>
    NotARepository,

    /// <summary>
    /// Could not be determined cheaply — a UNC path, or no folder at all.
    ///
    /// Distinct from <see cref="NotARepository"/> because the two lead to opposite behaviour:
    /// CLAUDE.md requires <c>GetState</c> to fall back to "show" rather than block, so an unknown
    /// folder keeps every entry visible and merely loses the branch in the label.
    /// </summary>
    Unknown,
}

internal readonly record struct RepositoryAnswer(RepositoryVerdict Verdict, string? Branch);

/// <summary>
/// The repository question, answered once per right-click instead of four times.
///
/// Explorer calls <c>GetState</c> and <c>GetTitle</c> on every registered handler while it builds one
/// menu, so the same folder is asked about four times in a row for two handlers. Without this that is
/// four directory walks and four file reads inside a 20 ms budget on the desktop's critical path.
///
/// A cache is machinery Hard Requirement 2 would otherwise argue against, and this is the case it
/// makes an exception for: there are genuinely four callers within a few milliseconds of each other,
/// and the alternative is repeating the only unbounded work this DLL does.
///
/// One entry, because one menu is being built at a time. A two-second lifetime, which is long enough
/// to cover a menu build and far too short to show a branch the user has since changed — and
/// switching branches from FlickGit itself does not go through this process at all.
/// </summary>
internal static class RepositoryLookup
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(2);
    private static readonly object Gate = new();

    private static string? _folder;
    private static RepositoryAnswer _answer;
    private static long _expiresAt;

    public static RepositoryAnswer For(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
            return new RepositoryAnswer(RepositoryVerdict.Unknown, null);

        //A network path. Every probe below could block for as long as the redirector takes to give
        //up, which is orders of magnitude past the 50 ms hard limit -- and blocking here freezes the
        //menu, not just this entry. Unknown keeps the entry visible without asking the question.
        if (folder.StartsWith(@"\\", StringComparison.Ordinal))
            return new RepositoryAnswer(RepositoryVerdict.Unknown, null);

        long now = Environment.TickCount64;

        lock (Gate)
        {
            if (now < _expiresAt && string.Equals(_folder, folder, StringComparison.OrdinalIgnoreCase))
                return _answer;
        }

        //Computed outside the lock: it touches the file system, and holding a lock across that would
        //make two Explorer windows wait on each other.
        RepositoryAnswer computed = Compute(folder);

        lock (Gate)
        {
            _folder = folder;
            _answer = computed;
            _expiresAt = now + (long)Lifetime.TotalMilliseconds;
        }

        return computed;
    }

    private static RepositoryAnswer Compute(string folder)
    {
        string? root = GitHead.FindRepositoryRoot(folder);

        return root is null
            ? new RepositoryAnswer(RepositoryVerdict.NotARepository, null)
            : new RepositoryAnswer(RepositoryVerdict.Repository, GitHead.ReadBranch(root));
    }
}
