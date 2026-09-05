using System.Diagnostics;
using System.Runtime.Versioning;
using FlickGit.Logging;

namespace FlickGit.App.Mac;

/// <summary>
/// The folder Finder is showing — the macOS counterpart of the Windows trigger's
/// <c>IShellWindows</c> walk, and the only thing that turns a global hotkey into "commit
/// <i>here</i>".
///
/// <b>Two steps, and CLAUDE.md is explicit that there is no third:</b> the selected item in the
/// active view if it is a folder, otherwise the folder that window is showing. A trigger with no
/// Finder folder behind it opens <i>nothing at all</i>, because the rule is never to act on a
/// repository the user is not looking at — so every failure below returns null rather than falling
/// back to the working directory.
///
/// <b>Finder is asked whether it is frontmost, rather than System Events being asked who is.</b>
/// That keeps this to one Automation target: the user is prompted once, for Finder, instead of twice.
/// Asking Finder for its front window while some other application is in front would answer with a
/// folder the user is not looking at, which is exactly the failure the two-step rule exists to avoid.
///
/// <b>Nothing is parsed but a path.</b> The script returns one POSIX path or the empty string;
/// AppleScript's own <c>POSIX path of</c> does the conversion, so no path arithmetic happens here.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class FinderFolder(ILog log)
{
    /// <summary>
    /// How long Finder gets to answer.
    ///
    /// A hotkey has a 120 ms budget to a painted window and this is on it, so a Finder that is busy
    /// or wedged must not hold the keystroke open. Generous against that budget on purpose: the
    /// first call of a session also carries the Automation permission prompt, and timing that out
    /// would train the user that the hotkey does nothing.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private const string Script = """
        tell application "Finder"
            if not frontmost then return ""
            if (count of Finder windows) is 0 then return ""
            set chosen to selection
            if (count of chosen) > 0 then
                set first_item to item 1 of chosen
                if class of first_item is folder then return POSIX path of (first_item as alias)
            end if
            return POSIX path of (target of front Finder window as alias)
        end tell
        """;

    /// <summary>The folder, or null when Finder is not in front, has no window, or did not answer.</summary>
    public string? Resolve()
    {
        try
        {
            var start = new ProcessStartInfo("/usr/bin/osascript")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            start.ArgumentList.Add("-e");
            start.ArgumentList.Add(Script);

            using Process? process = Process.Start(start);

            if (process is null)
                return null;

            //Read before waiting. osascript's output is one short line and could not fill a pipe, but
            //the order is the same rule GitProcessRunner keeps and getting it wrong here would be a
            //deadlock nobody could reproduce.
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(Timeout))
            {
                //Killed rather than abandoned: an osascript left waiting on an Automation prompt the
                //user never answers would otherwise outlive every hotkey press of the session.
                process.Kill(entireProcessTree: true);
                log.Warn("Finder did not answer which folder it is showing within the timeout.");

                return null;
            }

            if (process.ExitCode != 0)
            {
                //Almost always the Automation permission being declined, which is a configuration the
                //user chose rather than a fault. Logged once per press and never surfaced: the honest
                //outcome of "FlickGit may not ask Finder anything" is that the hotkey opens nothing.
                log.Warn($"Finder could not be asked which folder it is showing: {error.Trim()}");

                return null;
            }

            string path = output.Trim();

            return path.Length == 0 ? null : path;
        }
        catch (Exception ex)
        {
            log.Warn($"Resolving the Finder folder failed: {ex.Message}");

            return null;
        }
    }
}
