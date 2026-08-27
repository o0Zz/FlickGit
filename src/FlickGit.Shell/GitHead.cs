
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
internal static unsafe class GitHead
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
                if (HasGitEntry(directory.FullName))
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
    /// Whether <paramref name="folder"/> is a repository root: a <c>.git</c> directly inside it.
    ///
    /// Both spellings count, which is why this is one <c>GetFileAttributesW</c> and not
    /// <c>Directory.Exists</c> followed by <c>File.Exists</c>. The question is whether the entry is
    /// there at all, and asking it once is both cheaper and closer to what is meant.
    ///
    /// <b>Allocation-free.</b> <c>OverlayHandler.IsMemberOf</c> calls this once per item Explorer
    /// draws, and <see cref="FindRepositoryRoot"/> calls it up to <see cref="MaxDepth"/> times per
    /// right-click -- so the candidate path is built in a caller's stack buffer rather than by
    /// <c>Path.Combine</c>, and this DLL's GC stays off the desktop's drawing path entirely.
    ///
    /// A path past <c>MAX_PATH</c> without a <c>\\?\</c> prefix answers false rather than true,
    /// because that is what the API does. For a badge that is the right way to be wrong: nothing is
    /// drawn, rather than something drawn on the wrong folder.
    /// </summary>
    public static bool HasGitEntry(ReadOnlySpan<char> folder)
    {
        //A trailing separator would build `C:\\.git`, which resolves but is worth not writing. `C:\`
        //trimmed to `C:` still builds `C:\.git`, so a drive root is not a special case.
        if (folder.Length > 0 && (folder[^1] == '\\' || folder[^1] == '/'))
            folder = folder[..^1];

        if (folder.IsEmpty)
            return false;

        //The folder, a separator, `.git`, and the NUL the API needs.
        int needed = folder.Length + GitEntry.Length + 1;

        //512 covers every path Explorer can actually display; the heap fallback is there so a longer
        //one is answered rather than skipped, and never runs in practice.
        if (needed > StackBuffer)
            return Probe(folder, new char[needed]);

        Span<char> buffer = stackalloc char[StackBuffer];
        return Probe(folder, buffer);
    }

    /// <summary>The entry appended to a folder. A separate constant so the length above cannot drift from it.</summary>
    private const string GitEntry = @"\.git";

    private const int StackBuffer = 512;

    private static bool Probe(ReadOnlySpan<char> folder, Span<char> buffer)
    {
        folder.CopyTo(buffer);
        GitEntry.CopyTo(buffer[folder.Length..]);

        int end = folder.Length + GitEntry.Length;

        //Explicit: everything past `end` is whatever was on the stack, and the API reads to the NUL.
        buffer[end] = '\0';

        fixed (char* path = buffer)
            return Com.GetFileAttributes(path) != Com.InvalidFileAttributes;
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
