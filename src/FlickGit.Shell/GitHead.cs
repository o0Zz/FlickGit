
namespace FlickGit.Shell;

/// <summary>
/// The repository root and the branch name, from the file system alone.
///
/// <b>No <c>git.exe</c>, and no pipe to the resident service.</b> CLAUDE.md gives
/// <c>IExplorerCommand::GetState</c> a 20 ms budget with a 50 ms hard limit, and it is called
/// synchronously while Explorer builds the menu — so a process launch is out by an order of
/// magnitude and an IPC round trip would put the desktop's responsiveness behind our own service
/// being alive.
///
/// What is left is the cheapest possible thing that is still true: walk up looking for
/// <c>.git</c>, then read the first line of <c>.git/HEAD</c>. That file is about fifty bytes and
/// says <c>ref: refs/heads/main</c>. It is also, unlike anything else here, the definition of
/// which branch is checked out rather than a cache of it.
///
/// <b>This is not "Git logic" in the sense CLAUDE.md forbids in this assembly.</b> The rule exists
/// so no Git *operation* runs inside <c>explorer.exe</c> — no index, no network, no process, nothing
/// that can write. This reads two paths and one small file, and could not modify a repository if it
/// tried.
/// </summary>
internal static class GitHead
{
    /// <summary>
    /// How far up to look for a repository root.
    ///
    /// A depth limit rather than "until the drive root", because this runs on every right-click and
    /// the walk is the only unbounded part of it. Sixteen levels is deeper than any checkout a person
    /// navigates by hand, and it bounds the worst case at sixteen metadata probes.
    /// </summary>
    private const int MaxDepth = 16;

    /// <summary>
    /// The repository root containing <paramref name="folder"/>, or null.
    ///
    /// Both spellings of <c>.git</c> count: a directory for an ordinary clone, and a file for a
    /// worktree or a submodule, where it holds <c>gitdir: ...</c> pointing elsewhere.
    /// </summary>
    public static string? FindRepositoryRoot(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
            return null;

        try
        {
            //The handler is registered on files as well as folders now, so this is genuinely handed
            //a file path. Starting at its directory rather than at the file is not just tidier: the
            //walk would otherwise spend its first probe looking for C:\repo\src\a.cs\.git, and
            //MaxDepth is a real limit rather than a formality on a deep tree.
            string start = Directory.Exists(folder) ? folder : Path.GetDirectoryName(folder) ?? folder;

            var directory = new DirectoryInfo(start);

            for (int depth = 0; depth < MaxDepth && directory is not null; depth++)
            {
                string candidate = Path.Combine(directory.FullName, ".git");

                if (Directory.Exists(candidate) || File.Exists(candidate))
                    return directory.FullName;

                directory = directory.Parent;
            }
        }
        catch
        {
            //A path this process cannot walk -- denied, malformed, or a device that went away. There
            //is no repository as far as anything here is concerned, which is the safe answer: the
            //caller shows the plain label rather than hiding an entry it could not evaluate.
        }

        return null;
    }

    /// <summary>
    /// The checked-out branch of the repository at <paramref name="repositoryRoot"/>, or null when
    /// it cannot be determined.
    /// </summary>
    /// <returns>
    /// The short branch name — <c>main</c>, <c>feature/storage-gw</c> — or the first seven characters
    /// of the commit when HEAD is detached, which is what the user would see in any other client. Null
    /// rather than a guess when the file is missing or says something unexpected, so the label falls
    /// back to having no branch in it at all.
    /// </returns>
    public static string? ReadBranch(string repositoryRoot)
    {
        try
        {
            string? gitDirectory = ResolveGitDirectory(repositoryRoot);

            if (gitDirectory is null)
                return null;

            string head = Path.Combine(gitDirectory, "HEAD");

            if (!File.Exists(head))
                return null;

            //The whole file. It is one short line; opening it to read a line costs the same and this
            //way there is no reader to dispose on a path that runs thousands of times.
            string contents = File.ReadAllText(head).Trim();

            return ParseHead(contents);
        }
        catch
        {
            //Locked mid-write by a Git operation, or unreadable. No branch, plain label.
            return null;
        }
    }

    /// <summary>
    /// Turns the contents of a <c>HEAD</c> file into a name.
    ///
    /// Two forms, and this is the whole grammar of that file:
    /// <list type="bullet">
    /// <item><description><c>ref: refs/heads/feature/storage-gw</c> — attached, and the branch name
    /// may itself contain slashes, so everything after the prefix is the name.</description></item>
    /// <item><description>A bare 40-character object id — detached, shown short.</description></item>
    /// </list>
    /// </summary>
    public static string? ParseHead(string contents)
    {
        const string refPrefix = "ref: refs/heads/";

        if (contents.StartsWith(refPrefix, StringComparison.Ordinal))
        {
            string name = contents[refPrefix.Length..].Trim();
            return name.Length > 0 ? name : null;
        }

        //A symbolic ref to something that is not a branch -- refs/tags, or a remote. Rare enough to
        //be worth showing as-is rather than inventing a name for.
        if (contents.StartsWith("ref: ", StringComparison.Ordinal))
        {
            string name = contents[5..].Trim();
            return name.Length > 0 ? name : null;
        }

        //Detached: a raw object id. Seven characters is what `--short` gives by default.
        if (contents.Length >= 7 && IsHex(contents))
            return contents[..7];

        return null;
    }

    /// <summary>
    /// The directory holding <c>HEAD</c>, which is not always <c>&lt;root&gt;\.git</c>.
    ///
    /// In a worktree or a submodule, <c>.git</c> is a file reading <c>gitdir: &lt;path&gt;</c>, and the
    /// path may be relative to the repository root. Following it is what makes the branch correct in a
    /// submodule rather than silently showing the superproject's.
    /// </summary>
    private static string? ResolveGitDirectory(string repositoryRoot)
    {
        string dotGit = Path.Combine(repositoryRoot, ".git");

        if (Directory.Exists(dotGit))
            return dotGit;

        if (!File.Exists(dotGit))
            return null;

        const string gitdirPrefix = "gitdir:";

        string contents = File.ReadAllText(dotGit).Trim();

        if (!contents.StartsWith(gitdirPrefix, StringComparison.Ordinal))
            return null;

        string target = contents[gitdirPrefix.Length..].Trim();

        if (target.Length == 0)
            return null;

        //Relative paths are relative to the directory holding the .git file, per gitrepository-layout.
        return Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(repositoryRoot, target));
    }

    private static bool IsHex(string value)
    {
        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
                return false;
        }

        return true;
    }
}
