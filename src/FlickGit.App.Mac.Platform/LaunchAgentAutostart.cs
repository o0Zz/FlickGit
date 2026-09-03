using System.Diagnostics;
using System.Runtime.Versioning;
using System.Xml.Linq;
using FlickGit.App.Settings;
using FlickGit.Logging;

namespace FlickGit.App.Mac;

/// <summary>
/// Start with the session, as a <c>launchd</c> LaunchAgent.
///
/// The macOS answer to the Windows logon Scheduled Task, and the same shape of thing: a file
/// describing what to run, registered with a system service. What differs is that launchd owns the
/// process for the whole session — it will restart the agent if it exits, which is why
/// <c>KeepAlive</c> is deliberately <b>false</b> here. A resident service that crashed should stay
/// down until the user's next login rather than be respawned in a loop they cannot see.
///
/// <c>RunAtLoad</c> with no delay, unlike the Windows task's 45 seconds. That delay exists because
/// <c>Shell_NotifyIcon</c> fails while the notification area does not yet exist; launchd starts
/// agents after the session is up, and there is no menu bar item to place yet anyway.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class LaunchAgentAutostart(ILog log) : IAutostart
{
    /// <summary>
    /// Reverse-DNS, which is the convention launchd labels follow and what <c>launchctl list</c>
    /// shows. It is also the file name, because launchd requires the two to agree.
    /// </summary>
    private const string Label = "com.flickgit.agent";

    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library",
        "LaunchAgents",
        Label + ".plist");

    /// <summary>
    /// Read from the file system, not from a setting — the same rule the Windows implementation
    /// keeps, and for the same reason: a checkbox disagreeing with launchd is worse than none.
    /// </summary>
    public bool IsEnabled() => File.Exists(PlistPath);

    public (bool Succeeded, string Message) Enable()
    {
        string path = PlistPath;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            File.WriteAllText(path, Plist());

            //Registered as well as written: a plist that only exists on disk takes effect at the
            //next login, and the user asked for it now.
            (bool ok, string message) = Launchctl("bootstrap", $"gui/{Libc.getuid()}", path);

            return ok
                ? (true, $"FlickGit will start when you log in ({path}).")
                : (false, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warn($"Could not write {path}: {ex.Message}");

            return (false, $"Could not write the LaunchAgent:\n\n{ex.Message}");
        }
    }

    public (bool Succeeded, string Message) Disable()
    {
        string path = PlistPath;

        if (!File.Exists(path))
            return (true, "FlickGit was not set to start when you log in.");

        //Unregistered before the file goes, or launchd keeps the job for the rest of the session
        //with nothing on disk to explain it. A failure here is not fatal: deleting the plist still
        //stops it at the next login, which is what the user asked for.
        (bool ok, string message) = Launchctl("bootout", $"gui/{Libc.getuid()}", path);

        if (!ok)
            log.Warn($"launchctl bootout: {message}");

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"Could not remove the LaunchAgent:\n\n{ex.Message}");
        }

        return (true, "FlickGit will no longer start when you log in.");
    }

    /// <summary>
    /// The agent definition.
    ///
    /// Built with <c>XDocument</c> rather than a format string because the executable path is
    /// interpolated into it, and a path containing <c>&amp;</c> — legal on macOS — would produce a
    /// plist launchd refuses to parse, silently leaving autostart broken.
    /// </summary>
    private static string Plist()
    {
        string executable = Environment.ProcessPath ?? "flick";

        var dict = new XElement("dict",
            new XElement("key", "Label"), new XElement("string", Label),
            new XElement("key", "ProgramArguments"),
            new XElement("array",
                new XElement("string", executable),
                new XElement("string", "tray")),
            new XElement("key", "RunAtLoad"), new XElement("true"),

            //See the class remarks: launchd must not resurrect a service that gave up.
            new XElement("key", "KeepAlive"), new XElement("false"));

        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN",
                "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), dict));

        return document.ToString() + Environment.NewLine;
    }

    private (bool Succeeded, string Message) Launchctl(params string[] arguments)
    {
        var start = new ProcessStartInfo("/bin/launchctl")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        try
        {
            using Process? process = Process.Start(start);

            if (process is null)
                return (false, "launchctl could not be started.");

            string error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit();

            //launchctl exits 0 on success and reports the reason on stderr otherwise. Its own words,
            //per CLAUDE.md on error handling -- a paraphrase would lose the errno it names.
            return process.ExitCode == 0
                ? (true, string.Empty)
                : (false, error.Length > 0 ? error : $"launchctl exited with {process.ExitCode}.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Warn($"launchctl failed: {ex.Message}");

            return (false, ex.Message);
        }
    }
}
