using System.Text.RegularExpressions;

namespace FlickGit.Clone;

/// <summary>
/// Recognises a Git remote URL and derives a directory name from it.
///
/// This is the clipboard prefill, and CLAUDE.md calls it "the single biggest time saver here":
/// the user copies a URL from Azure DevOps or GitHub, right-clicks, and the field is already
/// filled.
///
/// The recogniser is deliberately conservative. Anything that is not clearly a remote URL
/// leaves the field empty, because a wrong prefill is worse than an empty one — the user would
/// have to notice and clear it, and the whole point was to save them typing. It also never
/// triggers anything: "Never execute a clone from the clipboard without the user pressing
/// Clone."
/// </summary>
public static partial class CloneUrl
{
    /// <summary>
    /// scp-style: <c>git@github.com:owner/repo.git</c>. No scheme, a colon separating host from
    /// path, and the path must look like a path rather than a port number — otherwise
    /// <c>host:8080</c> would parse as a repository.
    /// </summary>
    [GeneratedRegex(
        @"^(?<user>[A-Za-z0-9._-]+)@(?<host>[A-Za-z0-9.-]+):(?<path>(?!\d+$)[^\s]+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ScpStyle();

    /// <summary>Hosts whose paths are known to be repositories even without a <c>.git</c> suffix.</summary>
    private static readonly string[] KnownForges =
    [
        "github.com", "gitlab.com", "bitbucket.org", "dev.azure.com",
        "visualstudio.com", "codeberg.org", "sr.ht", "gitea.com",
    ];

    /// <summary>
    /// Parses <paramref name="text"/> as a remote URL, or returns null when it is not one.
    /// </summary>
    public static CloneTarget? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string candidate = text.Trim();

        //A clipboard holding a paragraph that happens to contain a URL is not a prefill: the
        //user copied prose, not a remote. One token only.
        if (candidate.Length > 2048 || candidate.AsSpan().ContainsAny(" \t\r\n"))
            return null;

        if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri))
        {
            bool schemeIsGitLike = uri.Scheme is "https" or "http" or "ssh" or "git";
            if (!schemeIsGitLike)
                return null;

            string path = uri.AbsolutePath.Trim('/');
            if (path.Length == 0)
                return null;

            //Either it says .git, or the host is a forge whose URLs are repository paths. A
            //bare https://example.com/docs/page is not something to offer to clone.
            bool looksLikeRepository =
                path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                || KnownForges.Any(forge => uri.Host.EndsWith(forge, StringComparison.OrdinalIgnoreCase));

            return looksLikeRepository
                ? new CloneTarget(candidate, DeriveDirectoryName(path))
                : null;
        }

        Match scp = ScpStyle().Match(candidate);
        if (!scp.Success)
            return null;

        string scpPath = scp.Groups["path"].Value.Trim('/');
        return scpPath.Length == 0 ? null : new CloneTarget(candidate, DeriveDirectoryName(scpPath));
    }

    /// <summary>
    /// A directory name for any clone source the user typed, including one
    /// <see cref="TryParse"/> deliberately refuses.
    ///
    /// The two are separate on purpose. <see cref="TryParse"/> guards the *clipboard prefill* and
    /// must stay strict, because a wrong prefill costs the user more than an empty one. But a local
    /// path or a UNC share is a perfectly good thing to clone from once they have typed it, and
    /// leaving them to invent a folder name for it is needless friction.
    /// </summary>
    public static string? DirectoryNameFor(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        string trimmed = source.Trim().TrimEnd('/', '\\');
        if (trimmed.Length == 0)
            return null;

        //A recognised remote URL takes the strict path, so the two agree wherever both apply.
        if (TryParse(trimmed) is { } target)
            return target.DirectoryName;

        //Otherwise the last segment of whatever it is, under either separator.
        int separator = trimmed.LastIndexOfAny(['/', '\\', ':']);
        string last = separator >= 0 && separator < trimmed.Length - 1 ? trimmed[(separator + 1)..] : trimmed;

        string name = DeriveDirectoryName(last);
        return name.Length > 0 ? name : null;
    }

    /// <summary>
    /// The last path segment with <c>.git</c> stripped, which is what `git clone` would have
    /// picked. Azure DevOps' <c>/_git/repo</c> form lands on the right segment for free.
    /// </summary>
    internal static string DeriveDirectoryName(string path)
    {
        string trimmed = path.Trim('/');

        int slash = trimmed.LastIndexOf('/');
        string last = slash < 0 ? trimmed : trimmed[(slash + 1)..];

        if (last.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            last = last[..^4];

        //Whatever is left has to be a legal directory name. A repository called "a:b" exists on
        //Linux and cannot be a folder here, and silently producing an unusable path would fail
        //later with a confusing error from the file system rather than a clear one from here.
        foreach (char invalid in Path.GetInvalidFileNameChars())
            last = last.Replace(invalid, '-');

        return last.Trim().Trim('.');
    }
}

/// <param name="Url">The URL exactly as the user supplied it. Never rewritten.</param>
/// <param name="DirectoryName">The suggested subdirectory name. Stays editable in the dialog.</param>
public sealed record CloneTarget(string Url, string DirectoryName);
