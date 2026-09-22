using System.IO;
using System.Runtime.InteropServices;
using FlickGit.App.Settings;
using FlickGit.Logging;

namespace FlickGit.App.Resident;

/// <summary>
/// Registers the resident service to start at logon, as a Scheduled Task.
///
/// A task rather than the <c>Run</c> key, for the reason CLAUDE.md gives: "Autostart via a Scheduled
/// Task at logon with a 30–60 s delay, so the tool never appears in boot-impact measurements." The
/// Run key has no delay, so a tray utility would be charged for slowing down every logon.
///
/// The task is defined by <b>XML</b>, handed to the Task Scheduler's COM API. The XML has separate
/// <c>&lt;Command&gt;</c> and <c>&lt;Arguments&gt;</c> elements, so a path containing a space needs
/// no nested quoting; <see cref="RootFolder"/> says why it is not <c>schtasks.exe</c>.
///
/// Everything here is per-user and needs no elevation.
/// </summary>
public sealed class Autostart(ILog log) : IAutostart
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

    /// <summary><c>TASK_CREATE_OR_UPDATE</c>.</summary>
    private const int CreateOrUpdate = 6;

    /// <summary><c>TASK_LOGON_INTERACTIVE_TOKEN</c>, matching the XML's principal.</summary>
    private const int InteractiveToken = 3;

    public bool IsEnabled()
    {
        try
        {
            RootFolder().GetTask(TaskName);
            return true;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            //ERROR_FILE_NOT_FOUND is the ordinary answer here, and any other failure to read the
            //task is equally "not registered" as far as a checkbox is concerned.
            return false;
        }
    }

    /// <summary>Registers the task, replacing any previous definition.</summary>
    public (bool Succeeded, string Message) Enable()
    {
        string? exePath = Environment.ProcessPath;

        if (exePath is null || !File.Exists(exePath))
            return (false, "FlickGit.exe could not be located, so no logon task was registered.");

        try
        {
            //TASK_CREATE_OR_UPDATE replaces an existing definition, so this is idempotent and doubles
            //as "repair". The principal comes from the XML: no user id and no password here.
            RootFolder().RegisterTask(TaskName, BuildXml(exePath), CreateOrUpdate, null, null, InteractiveToken, null);

            log.Info("Autostart enabled.");
            return (true, $"FlickGit will start {LogonDelay[2..^1]} seconds after you log on.");
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException)
        {
            log.Warn($"Autostart registration failed: {ex.Message}");
            return (false, $"The logon task could not be registered:\n\n{ex.Message}");
        }
    }

    public (bool Succeeded, string Message) Disable()
    {
        if (!IsEnabled())
            return (true, "FlickGit was not set to start at logon.");

        try
        {
            RootFolder().DeleteTask(TaskName, 0);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            log.Warn($"Autostart removal failed: {ex.Message}");
            return (false, $"The logon task could not be removed:\n\n{ex.Message}");
        }

        log.Info("Autostart disabled.");
        return (true, "FlickGit will no longer start at logon.");
    }

    /// <summary>
    /// The Task Scheduler's root folder, through its own COM API.
    ///
    /// Not <c>schtasks.exe</c>. An unsigned binary spawning <c>schtasks /Create /XML %TEMP%\... /F</c>
    /// is the textbook command line of malware installing persistence, and Defender's behavioural
    /// model quarantined both executables on an install that did it
    /// (<c>Behavior:Win32/Persistence.A!ml</c>).
    /// The API takes the same XML as a string: no child process, no temp file, and the task that
    /// results is identical.
    /// </summary>
    private static dynamic RootFolder()
    {
        Type type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service.GetFolder("\\");
    }

    /// <summary>
    /// The task definition.
    ///
    /// <c>tray</c> is the argument, which is the verb that means "go resident". Interactive-token
    /// logon type and the current user as principal: the service must run in the user's session with
    /// their desktop, or the tray icon has nowhere to appear.
    /// </summary>
    private static string BuildXml(string exePath)
    {
        //Escaped, not interpolated raw. Both values come from outside this file -- the install path and
        //the Windows account name -- and an `&` in either produces XML the Task Scheduler refuses,
        //which reads to the user as autostart simply not working.
        string command = Escape(exePath);
        string user = Escape(System.Security.Principal.WindowsIdentity.GetCurrent().Name);

        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Starts FlickGit's resident service so Explorer actions open instantly.</Description>
            <URI>\{TaskName}</URI>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{user}</UserId>
              <Delay>{LogonDelay}</Delay>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{user}</UserId>
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
              <Command>{command}</Command>
              <Arguments>tray</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;
    }

    private static string Escape(string value) => System.Security.SecurityElement.Escape(value) ?? value;
}
