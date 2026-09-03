using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace FlickGit.App.Mac;

/// <summary>
/// The one libc call the resident service cannot do without.
///
/// <c>getpeereid</c> answers "which user is on the other end of this socket", which is the whole
/// security model of the local endpoint: a request on it becomes a process start through a
/// user-defined action, so the server has to know the peer is the same user rather than merely hope
/// the directory mode kept everyone else out. It is the BSD/macOS spelling of what Linux does with
/// <c>SO_PEERCRED</c> — a plain two-out-parameter function rather than a struct to marshal, which is
/// why the socket option is not used here.
/// </summary>
[SupportedOSPlatform("macos")]
internal static partial class Libc
{
    /// <summary>The effective user and group on the far end of a connected Unix socket.</summary>
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int getpeereid(int socket, out uint euid, out uint egid);

    /// <summary>This process's own real user id, to compare against.</summary>
    [LibraryImport("libc")]
    internal static partial uint getuid();
}
