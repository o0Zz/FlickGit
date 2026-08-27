using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using FlickGit.Shared;
using FlickGit.App.Localization;
using FlickGit.Logging;
using Microsoft.Win32;

namespace FlickGit.App.Shell;

/// <summary>
/// How many overlay handlers are registered on this machine, and where FlickGit sorts among them.
/// </summary>
/// <param name="Registered">Every handler key name, in the order Windows sorts them.</param>
/// <param name="Position">
/// FlickGit's one-based position in that order, or null when it is not registered at all.
/// </param>
/// <param name="Limit">How many of them Windows actually loads.</param>
public sealed record OverlaySlots(IReadOnlyList<string> Registered, int? Position, int Limit)
{
    /// <summary>
    /// Whether a registered FlickGit would actually be loaded. Null when it is not registered.
    ///
    /// The distinction matters because the failure is silent: past the limit the key is present, the
    /// DLL is fine, and nothing is ever drawn.
    /// </summary>
    public bool? WithinLimit => Position is { } position ? position <= Limit : null;
}

/// <summary>
/// Registers and removes the icon overlay Explorer draws on a repository folder.
///
/// <b>A sibling of <see cref="ShellIntegration"/>, not part of it.</b> The two write different keys,
/// under different hives, at different privilege levels, and only this one can ever need
/// administrator rights. Folding the overlay into the context menu's install would put a UAC prompt
/// in the path of every ordinary registration -- including the installer's, which is per-user and
/// must stay that way.
///
/// <b>Two halves, and only one of them needs elevation.</b>
/// <code>
/// HKCU\Software\Classes\CLSID\{overlay}                 InprocServer32, FlickGit.OverlayIcon
/// HKLM\...\Explorer\ShellIconOverlayIdentifiers\ FlickGit   = the CLSID
/// </code>
/// The second is one string value, and it is the only thing in the product written outside
/// <c>HKCU</c>. Everything the handler needs to <i>work</i> is in the first, because
/// <c>CoCreateInstance</c> reads the user hive before the machine hive -- so the elevated half knows
/// nothing except a GUID.
///
/// <b>Registering does not take effect until Explorer restarts.</b> Overlay handlers are enumerated
/// once, at Explorer startup, and there is no notification that changes that -- <c>SHChangeNotify</c>
/// does not reload them. Every message this class produces says so.
/// </summary>
public sealed class OverlayIntegration(ILog log)
{
    private const string ClassesPath = @"Software\Classes";
    private const string ClsidPath = "CLSID";

    /// <summary>The .ico Explorer draws, beside the per-action ones the menu uses.</summary>
    private const string IconFileName = "overlay.ico";

    /// <summary>
    /// `ERROR_CANCELLED`. What <c>ShellExecute</c> reports when the user answers No to the UAC
    /// prompt -- which is a decision, not a failure, and gets exit code 3 rather than 4.
    /// </summary>
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// Both halves, prompting for administrator rights if this process does not already have them.
    ///
    /// <b>Asynchronous because of the prompt.</b> The UAC dialog sits there for as long as the user
    /// takes to read it, and this runs on the resident service's UI thread -- the one also serving the
    /// tray icon and the pipe. Waiting synchronously would freeze both for the duration.
    /// </summary>
    public async Task<InstallResult> InstallAsync()
    {
        if (WriteUserHalf() is { Succeeded: false } failure)
            return failure;

        //Already elevated: an admin running `flick install-overlay` from an elevated terminal, or the
        //elevated child started below. Either way there is nothing to prompt for.
        if (IsElevated)
            return InstallSystem();

        return await ElevateAsync(install: true).ConfigureAwait(true);
    }

    /// <summary>
    /// Both halves removed, prompting for administrator rights the same way.
    ///
    /// The machine half goes <b>first</b>: it is the one that can fail for want of rights, and
    /// removing the user half first would leave a machine key pointing at a CLSID that no longer
    /// resolves -- exactly the orphan this is trying to avoid.
    /// </summary>
    public async Task<InstallResult> UninstallAsync()
    {
        InstallResult system = IsElevated
            ? UninstallSystem()
            : await ElevateAsync(install: false).ConfigureAwait(true);

        if (!system.Succeeded)
            return system;

        return RemoveUserHalf();
    }

    /// <summary>
    /// The machine half alone: one value under <see cref="ShellCommandIds.OverlayIdentifiersPath"/>.
    ///
    /// Public because it is also <c>flick install-overlay system</c>, which is how an administrator
    /// deploying to many machines writes this key from an elevated script without a prompt.
    /// </summary>
    public InstallResult InstallSystem()
    {
        if (!IsElevated)
            return new InstallResult(false, Strings.Get("overlay.needsAdmin"));

        try
        {
            using RegistryKey identifiers = Registry.LocalMachine.CreateSubKey(
                                                ShellCommandIds.OverlayIdentifiersPath, writable: true)
                                            ?? throw new InvalidOperationException(
                                                "Could not open the overlay identifiers key.");

            using RegistryKey ours = identifiers.CreateSubKey(ShellCommandIds.OverlayKeyName, writable: true)
                                     ?? throw new InvalidOperationException(
                                         "Could not create the FlickGit overlay key.");

            ours.SetValue(string.Empty, ShellCommandIds.OverlayHandlerClsid, RegistryValueKind.String);

            log.Info("Overlay handler registered machine-wide.");
            return new InstallResult(true, Strings.Get("overlay.system.installed"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            log.Error($"Overlay machine registration failed: {ex.Message}");
            return new InstallResult(false, $"{Strings.Get("overlay.system.failed")}\n\n{ex.Message}");
        }
    }

    /// <summary>The machine half removed. Never touches a key it did not create.</summary>
    public InstallResult UninstallSystem()
    {
        if (!IsElevated)
            return new InstallResult(false, Strings.Get("overlay.needsAdmin"));

        try
        {
            using RegistryKey? identifiers = Registry.LocalMachine.OpenSubKey(
                ShellCommandIds.OverlayIdentifiersPath, writable: true);

            //By name, never by enumerating and matching: every other handler under this key belongs
            //to somebody else, and this is the one place in the product writing to HKLM at all.
            identifiers?.DeleteSubKeyTree(ShellCommandIds.OverlayKeyName, throwOnMissingSubKey: false);

            log.Info("Overlay handler removed machine-wide.");
            return new InstallResult(true, Strings.Get("overlay.system.removed"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Error($"Overlay machine removal failed: {ex.Message}");
            return new InstallResult(false, $"{Strings.Get("overlay.system.failed")}\n\n{ex.Message}");
        }
    }

    /// <summary>
    /// True when both halves are in place. Read from the registry every time, never remembered:
    /// the settings checkbox showing something the registry disagrees with is worse than no checkbox.
    /// </summary>
    public bool IsInstalled() => UserHalfPresent() && SystemHalfPresent();

    /// <summary>
    /// What is registered machine-wide, and where we sort in it.
    ///
    /// The honest substitute for a check nothing can perform: whether Explorer <i>loaded</i> the
    /// handler is not knowable from outside <c>explorer.exe</c>, so <c>diag doctor</c> reports the
    /// arithmetic that decides it instead.
    /// </summary>
    public OverlaySlots Slots()
    {
        try
        {
            using RegistryKey? identifiers = Registry.LocalMachine.OpenSubKey(
                ShellCommandIds.OverlayIdentifiersPath, writable: false);

            //Ordinal, because that is how the shell sorts them -- which is the whole reason our key
            //name starts with a space.
            string[] names = identifiers?.GetSubKeyNames() ?? [];
            Array.Sort(names, StringComparer.Ordinal);

            int index = Array.IndexOf(names, ShellCommandIds.OverlayKeyName);

            return new OverlaySlots(names, index >= 0 ? index + 1 : null, ShellCommandIds.OverlaySlotLimit);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new OverlaySlots([], null, ShellCommandIds.OverlaySlotLimit);
        }
    }

    /// <summary>
    /// The user half: the CLSID, its server, and the icon path the DLL reads back.
    ///
    /// Refuses before writing anything when either file is missing, for the reason
    /// <see cref="ShellIntegration.Install"/> refuses: a registration pointing at a file that is not
    /// there is worse than none, because Explorer occupies the slot and draws nothing.
    /// </summary>
    private InstallResult WriteUserHalf()
    {
        string installDirectory = InstallDirectory;
        string dll = Path.Combine(installDirectory, ShellCommandIds.DllFileName);
        string icon = Path.Combine(installDirectory, "icons", IconFileName);

        if (!File.Exists(dll))
        {
            return new InstallResult(false,
                $"{ShellCommandIds.DllFileName} was not found in:\n\n{installDirectory}\n\n" +
                "Native AOT only builds it on publish, so a `dotnet build` working tree does not " +
                "have one.\n\nThe overlay was not registered.");
        }

        if (!File.Exists(icon))
        {
            return new InstallResult(false,
                $"{IconFileName} was not found in:\n\n{Path.Combine(installDirectory, "icons")}\n\n" +
                "The overlay was not registered.");
        }

        try
        {
            using RegistryKey clsid = Registry.CurrentUser.CreateSubKey(
                                          $@"{ClassesPath}\{ClsidPath}\{ShellCommandIds.OverlayHandlerClsid}",
                                          writable: true)
                                      ?? throw new InvalidOperationException(
                                          "Could not register the overlay handler.");

            clsid.SetValue(string.Empty, "FlickGit repository overlay", RegistryValueKind.String);
            clsid.SetValue(ShellCommandIds.ValueOverlayIcon, icon, RegistryValueKind.String);

            using (RegistryKey server = clsid.CreateSubKey("InprocServer32", writable: true)
                                        ?? throw new InvalidOperationException(
                                            "Could not register the overlay handler's server."))
            {
                server.SetValue(string.Empty, dll, RegistryValueKind.String);
                server.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
            }

            //Read back, for the reason ShellIntegration.Verify does: a write silently swallowed by
            //group policy must not be reported as success.
            if (!UserHalfPresent())
            {
                return new InstallResult(false,
                    "The overlay keys were written but could not be read back. Group policy or " +
                    "another tool may be blocking HKCU\\Software\\Classes.");
            }

            log.Info($"Overlay handler registered from {dll}.");
            return new InstallResult(true, string.Empty);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            log.Error($"Overlay user registration failed: {ex.Message}");
            return new InstallResult(false, $"The overlay could not be registered:\n\n{ex.Message}");
        }
    }

    private InstallResult RemoveUserHalf()
    {
        try
        {
            using RegistryKey? clsids = Registry.CurrentUser.OpenSubKey(
                $@"{ClassesPath}\{ClsidPath}", writable: true);

            clsids?.DeleteSubKeyTree(ShellCommandIds.OverlayHandlerClsid, throwOnMissingSubKey: false);

            log.Info("Overlay handler removed.");
            return new InstallResult(true, Strings.Get("overlay.removed"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Error($"Overlay removal failed: {ex.Message}");
            return new InstallResult(false, $"The overlay could not be removed:\n\n{ex.Message}");
        }
    }

    private static bool UserHalfPresent()
    {
        using RegistryKey? server = Registry.CurrentUser.OpenSubKey(
            $@"{ClassesPath}\{ClsidPath}\{ShellCommandIds.OverlayHandlerClsid}\InprocServer32",
            writable: false);

        return server?.GetValue(string.Empty) is string dll && File.Exists(dll);
    }

    private static bool SystemHalfPresent()
    {
        try
        {
            using RegistryKey? ours = Registry.LocalMachine.OpenSubKey(
                $@"{ShellCommandIds.OverlayIdentifiersPath}\{ShellCommandIds.OverlayKeyName}",
                writable: false);

            return ours?.GetValue(string.Empty) as string == ShellCommandIds.OverlayHandlerClsid;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            //Readable by everyone in practice; if it is not, the honest answer is "not registered"
            //rather than an exception out of a property the settings window reads on open.
            return false;
        }
    }

    /// <summary>
    /// Runs the machine half in a second, elevated process, and waits for it.
    ///
    /// <b><c>FlickGit.exe</c>, not <c>flick.exe</c>.</b> The stub forwards to the resident service
    /// over the pipe, and the resident service is <i>not</i> elevated -- so an elevated stub would
    /// hand the work straight back to a process that cannot do it. The App started with a verb runs
    /// it in-process and exits without going resident, which is exactly what is wanted here: no
    /// pipe, no mutex, no second tray icon.
    ///
    /// Only the exit code comes back. <c>UseShellExecute</c> is what <c>runas</c> requires and it
    /// rules out redirecting the child's output, which is why the <c>system</c> verb never opens a
    /// window of its own and this method composes the message.
    /// </summary>
    private async Task<InstallResult> ElevateAsync(bool install)
    {
        string app = Path.Combine(InstallDirectory, "FlickGit.exe");

        if (!File.Exists(app))
            return new InstallResult(false, $"FlickGit.exe was not found in:\n\n{InstallDirectory}");

        var startInfo = new ProcessStartInfo
        {
            FileName = app,
            UseShellExecute = true,
            Verb = "runas",
        };

        startInfo.ArgumentList.Add(install ? "install-overlay" : "uninstall-overlay");
        startInfo.ArgumentList.Add("system");

        try
        {
            using Process? elevated = Process.Start(startInfo);

            if (elevated is null)
                return new InstallResult(false, Strings.Get("overlay.system.failed"));

            await elevated.WaitForExitAsync().ConfigureAwait(true);

            if (elevated.ExitCode != 0)
            {
                log.Warn($"The elevated overlay step exited with {elevated.ExitCode}.");
                return new InstallResult(false, Strings.Get("overlay.system.failed"));
            }

            return new InstallResult(true,
                Strings.Get(install ? "overlay.installed" : "overlay.removed"));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            //Answering No to the UAC prompt. A decision, not a fault: nothing is logged as an error
            //and the caller turns this into exit code 3.
            log.Info("The overlay's elevated step was declined.");
            return new InstallResult(false, Strings.Get("overlay.declined"));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log.Error($"The elevated overlay step could not be started: {ex.Message}");
            return new InstallResult(false, $"{Strings.Get("overlay.system.failed")}\n\n{ex.Message}");
        }
    }

    /// <summary>Whether this process is running with administrator rights.</summary>
    private static bool IsElevated
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>
    /// Where the executables and <c>icons\</c> live: beside the running module, not the working
    /// directory, for the reason <see cref="ShellIntegration"/> says -- Explorer sets that to the
    /// clicked folder.
    /// </summary>
    private static string InstallDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
}
