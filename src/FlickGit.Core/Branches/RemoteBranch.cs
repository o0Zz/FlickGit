namespace FlickGit.Branches;

/// <summary>
/// A remote-tracking name split into the remote it names and the branch on it.
/// </summary>
public sealed record RemoteBranch(string Remote, string Branch)
{
    /// <summary>
    /// Splits a remote-tracking name such as <c>origin/feature/x</c> into its remote and its branch,
    /// <b>against the configured remotes, not at the first slash.</b>
    ///
    /// A branch may contain slashes, so <c>origin/feature/x</c> is remote <c>origin</c> branch
    /// <c>feature/x</c> only because <c>origin</c> is a configured remote. The longest match wins.
    ///
    /// Null when no configured remote prefixes the name -- which is what stops a deletion being
    /// pushed at a remote that does not exist, and what stops a push guessing where an upstream
    /// lives.
    ///
    /// A pure function of its arguments, hence static -- Hard Requirement 3's stated exception. Its
    /// two callers read <c>git remote</c> for their own reasons and share the matching rather than
    /// the process: <see cref="BranchService.ResolveRemoteBranchAsync"/> reads it to answer this
    /// question, and <c>PushService</c> already has the list in hand from its own guardrails.
    /// </summary>
    public static RemoteBranch? Match(IEnumerable<string> remoteNames, string remoteTrackingName)
    {
        string name = remoteTrackingName.Trim();

        return remoteNames
            .Select(remote => remote.Trim())
            .Where(remote => remote.Length > 0 && name.StartsWith(remote + "/", StringComparison.Ordinal))
            .OrderByDescending(remote => remote.Length)
            .Select(remote => new RemoteBranch(remote, name[(remote.Length + 1)..]))
            .FirstOrDefault();
    }
}
