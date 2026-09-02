namespace FlickGit.Tests;

/// <summary>
/// Absolute paths the running platform will actually resolve.
///
/// Most of the suite's <c>C:\dev\repo</c> literals are inert: they are handed to
/// <see cref="FakeGitRunner"/> and the assertion is on the argument array, so the string never
/// reaches <c>Path.GetFullPath</c> and any spelling does. These are for the tests that <i>resolve</i>
/// a path, where a Windows literal is unportable in two separate ways -- <c>C:\dev\repo</c> is not
/// rooted off Windows, and a backslash there is an ordinary character in a file name, so
/// <c>..\outside</c> names one file inside the repository rather than climbing out of it.
///
/// That second one is why these tests are worth converting rather than skipping: written the Windows
/// way, a containment assertion off Windows does not merely fail, it <b>inverts</b> -- the path the
/// test calls an escape resolves inside, and a guard that is working still reports the wrong refusal.
/// </summary>
internal static class PlatformPaths
{
    /// <summary>The separator the platform resolves with.</summary>
    public static char Separator => Path.DirectorySeparatorChar;

    /// <summary>An absolute repository root.</summary>
    public static string Root { get; } = OperatingSystem.IsWindows() ? @"C:\dev\repo" : "/dev/repo";

    /// <summary>How Git spells <see cref="Root"/>: forward slashes, on every platform.</summary>
    public static string GitRoot { get; } = Root.Replace('\\', '/');

    /// <summary>The Git directory inside <see cref="Root"/>.</summary>
    public static string GitDirectory { get; } = Under(".git");

    /// <summary>An absolute path outside the repository.</summary>
    public static string Outside { get; } = OperatingSystem.IsWindows() ? @"C:\somewhere\else" : "/somewhere/else";

    /// <summary>Another volume on Windows; simply somewhere unrelated off it.</summary>
    public static string OtherVolume { get; } = OperatingSystem.IsWindows() ? @"D:\repo" : "/mnt/other/repo";

    /// <summary><paramref name="segments"/> below <see cref="Root"/>.</summary>
    public static string Under(params string[] segments) => Join(Root, segments);

    /// <summary><paramref name="segments"/> below <paramref name="root"/>.</summary>
    public static string Join(string root, params string[] segments) =>
        root + Separator + string.Join(Separator, segments);

    /// <summary>A sibling of <see cref="Root"/> whose name starts the same.</summary>
    public static string Sibling(string suffix) => Root + suffix;

    /// <summary>A directory beside <see cref="Root"/>, sharing its parent.</summary>
    public static string Beside(params string[] segments) => Join(Path.GetDirectoryName(Root)!, segments);

    /// <summary>A relative path that climbs out, spelled the way the platform reads it.</summary>
    public static string Up(params string[] segments) => ".." + Separator + string.Join(Separator, segments);
}
