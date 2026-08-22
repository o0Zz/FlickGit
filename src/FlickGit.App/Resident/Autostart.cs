using System.Diagnostics;
using System.IO;
using System.Text;
using FlickGit.Logging;

namespace FlickGit.App.Resident;

/// <summary>
/// Registers the resident service to start at logon, as a Scheduled Task.
///
/// A task rather than the <c>Run</c> key, for the reason CLAUDE.md gives: "Autostart via a Scheduled
/// Task at logon with a 30–60 s delay, so the tool never appears in boot-impact measurements." The
/// Run key has no delay, so a tray utility would be charged for slowing down every logon.
///
/// The task is defined by <b>XML</b>, not by <c>schtasks</c> command-line switches. That is not a
/// stylistic choice: <c>/TR</c> takes the program and its arguments as one string, so a path
/// containing a space has to be quoted inside a value that is itself being quoted — exactly the
/// nested-quoting problem this codebase refuses to have anywhere else. The XML has separate
/// <c>&lt;Command&gt;</c> and <c>&lt;Arguments&gt;</c> elements and no ambiguity.
///
/// Everything here is per-user and needs no elevation.
/// </summary>
public sealed class Autostart(ILog log)
{
    /// <summary>
    /// The task name. Under no folder, so it is visible where a user would look for it rather than
    /// buried in a vendor subtree they have to know to expand.
    /// </summary>
    private const string TaskName = "FlickGit";

    /// <summary>
    /// How long after logon to start.
    ///
    /// 45 s, in the middle of CLAUDE.md's 30–60 s range. Long enough to be outside the window
    /// Windows attributes to startup impact, short enough that the first right-click of the day is
    /// served by a warm service.
    /// </summary>
    private const string LogonDelay = "PT45S";

    public bool IsEnabled()
    {
        //Query by exact name. An exit code is the whole answer, so nothing is parsed.
        (int exitCode, _, _) = RunSchtasks(["/Query", "/TN", TaskName]);
        return exitCode == 0;
    }

    /// <summary>Registers the task, replacing any previous definition.</summary>
    public (bool Succeeded, string Message) Enable()
    {
        string? exePath = Environment.ProcessPath;

        if (exePath is null || !File.Exists(exePath))
            return (false, "FlickGit.exe could not be located, so no logon task was registered.");

        string xmlPath = Path.Combine(Path.GetTempPath(), $"flickgit-autostart-{Guid.NewGuid():N}.xml");

        try
        {
            //UTF-16 with a BOM: schtasks /XML refuses a UTF-8 file, which is the kind of thing that
            //produces a completely unrelated error message.
            File.WriteAllText(xmlPath, BuildXml(exePath!), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            //  /F replaces an existing definition, so this is idempotent and doubles as "repair".
            (int exitCode, _, string error) = RunSchtasks(["/Create", "/TN", TaskName, "/XML", xmlPath, "/F"]);

            if (exitCode != 0)
            {
                log.Warn($"Autostart registration failed ({exitCode}): {error}");
                return (false, $"The logon task could not be registered:\n\n{error.Trim()}");
            }

            log.Info("Autostart enabled.");
            return (true, $"FlickGit will start {LogonDelay[2..^1]} seconds after you log on.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"The logon task could not be registered:\n\n{ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(xmlPath);
            }
            catch (Exception)
            {
                //A temp file left in %TEMP% is not worth reporting over.
            }
        }
    }

    public (bool Succeeded, string Message) Disable()
    {
        if (!IsEnabled())
            return (true, "FlickGit was not set to start at logon.");

        (int exitCode, _, string error) = RunSchtasks(["/Delete", "/TN", TaskName, "/F"]);

        if (exitCode != 0)
        {
            log.Warn($"Autostart removal failed ({exitCode}): {error}");
            return (false, $"The logon task could not be removed:\n\n{error.Trim()}");
        }

        log.Info("Autostart disabled.");
        return (true, "FlickGit will no longer start at logon.");
    }

    /// <summary>
    /// The task definition.
    ///
    /// <c>tray</c> is the argument, which is the verb that means "go resident". Interactive-token
    /// logon type and the current user as principal: the service must run in the user's session with
    /// their desktop, or the tray icon has nowhere to appear.
    /// </summary>
    private static string BuildXml(string exePath) =>
        $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts FlickGit's resident service so Explorer actions open instantly.</Description>
            <URI>\{TaskName}</URI>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{System.Security.Principal.WindowsIdentity.GetCurrent().Name}</UserId>
              <Delay>{LogonDelay}</Delay>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{System.Security.Principal.WindowsIdentity.GetCurrent().Name}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
            <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
            <!-- No time limit: this is a service that stays up for the session, not a job. -->
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{exePath}</Command>
              <Arguments>tray</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    private (int ExitCode, string Output, string Error) RunSchtasks(string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        //ArgumentList, as everywhere else in this codebase. The task name and the XML path both go
        //through it untouched.
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        try
        {
            using Process process = Process.Start(startInfo)!;

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            log.Warn($"schtasks could not be started: {ex.Message}");
            return (-1, string.Empty, ex.Message);
        }
    }
}
