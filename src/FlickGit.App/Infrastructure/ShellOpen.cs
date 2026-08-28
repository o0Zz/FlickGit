using System.Diagnostics;

namespace FlickGit.App.Infrastructure;

/// <summary>
/// Hands a folder or a URL to the Windows shell, so whatever the user has registered for it opens.
///
/// <b>Four copies of this existed.</b> Two <c>OpenFolder</c> methods, in
/// <c>SubmodulesWindow</c> and <c>SwitchBranchWindow</c>, identical down to the comment explaining
/// why <c>UseShellExecute</c> is required; and two <c>OnNavigate</c> handlers, in
/// <c>MarkdownFlow</c> and <c>SettingsWindow</c>, identical but for whether the failure was
/// reported. What differed between them was never the shell call -- it was which string the window
/// puts on screen afterwards, which is the window's business and stays there.
///
/// <b>Why this is a static.</b> Hard Requirement 3 turns behaviour-bearing statics into injected
/// services, and this one starts a process. It is the same named exception <see cref="ConsoleOutput"/>
/// is: the thinnest possible wrapper over a process-global OS facility -- the shell's file
/// association table -- of which there is exactly one, forever, and nothing to substitute.
/// </summary>
internal static class ShellOpen
{
    /// <summary>
    /// Opens <paramref name="path"/> in Explorer.
    ///
    /// <c>UseShellExecute</c> is the whole mechanism and it is required rather than incidental:
    /// without it this is an attempt to <i>execute</i> the directory.
    /// </summary>
    /// <returns>Null when the shell took it, or the failure message.</returns>
    public static string? Folder(string path) => Start(new ProcessStartInfo
    {
        FileName = path,
        UseShellExecute = true,
    });

    /// <summary>
    /// Opens <paramref name="uri"/> in whatever the user browses with.
    ///
    /// <b>This does not validate the scheme</b>, because only the caller knows where the URL came
    /// from. A URL built from a repository's remote is checked for <c>http</c>/<c>https</c> before it
    /// reaches here -- see <c>PullRequestWindow</c> -- and a link in a Markdown page the user owns is
    /// theirs to click.
    /// </summary>
    /// <returns>Null when the shell took it, or the failure message.</returns>
    public static string? Uri(string uri) => Start(new ProcessStartInfo(uri) { UseShellExecute = true });

    /// <summary>
    /// <b>Catches everything, on purpose.</b> This is a best-effort hand-off to a table of file
    /// associations FlickGit does not own: a machine with no handler registered for <c>http</c>, or a
    /// folder that went away between the list being read and the row being clicked, are both real
    /// configurations rather than faults. Every caller is a click handler, and none of them has
    /// anything to roll back -- so the honest outcome is a message, never an exception unwinding
    /// through an <c>async void</c> and taking the process with it.
    /// </summary>
    private static string? Start(ProcessStartInfo startInfo)
    {
        try
        {
            //Disposing the handle, not the process. The point is to stop holding a handle to something
            //that outlives this window by design.
            using Process? started = Process.Start(startInfo);
            _ = started;

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
