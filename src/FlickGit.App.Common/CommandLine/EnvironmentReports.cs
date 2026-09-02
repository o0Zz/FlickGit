using System.Reflection;
using FlickGit.App.Localization;
using FlickGit.App.Settings;
using FlickGit.Cli;
using FlickGit.Diagnostics;

namespace FlickGit.App.CommandLine;

/// <summary>
/// The environment verbs that read the same on every platform.
///
/// <see cref="IEnvironmentVerbs"/> covers ten verbs and roughly half of them are a platform to the
/// bone — the registry, the Task Scheduler, a settings window. These five are not, and they were
/// sitting in the Windows implementation only because that is where the class happened to live:
/// <c>help</c> and <c>version</c> are facts about the build, <c>language</c> is the embedded string
/// table, <c>timings</c> is a Core counter, and <c>autostart</c> became portable the moment
/// <see cref="IAutostart"/> existed.
///
/// <b>Shared as a class both hosts delegate to, rather than as four more interfaces.</b> The
/// alternative was seams for the shell, the settings window and the key prompt so that one
/// <c>EnvironmentVerbs</c> could serve both — which is a lot of indirection to let a class that is
/// genuinely half platform-specific pretend it is not. Each host keeps its own thin
/// <c>IEnvironmentVerbs</c> and calls in here for the half that is shared.
/// </summary>
public sealed class EnvironmentReports(
    FlickSettings settings,
    IAutostart autostart,
    OperationTimings timings)
{
    /// <summary>
    /// The running build.
    ///
    /// The <i>entry</i> assembly, not this one: the answer wanted is the version of the host the
    /// user launched — <c>FlickGit.exe</c> or <c>flick</c> — and asking the executing assembly would
    /// report this shared library instead, which is a different number the moment the two are built
    /// separately.
    /// </summary>
    public static string Version =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0.0";

    public VerbResult Help(VerbOutput output)
    {
        output.Line(Verb.HelpText);
        return VerbResult.Exit(ExitCodes.Success);
    }

    public VerbResult ReportVersion(VerbOutput output)
    {
        output.Line($"FlickGit {Version}");
        return VerbResult.Exit(ExitCodes.Success);
    }

    public VerbResult Autostart(VerbOutput output, string? switchTo)
    {
        switch (switchTo?.Trim().ToLowerInvariant())
        {
            case "on":
            {
                (bool ok, string message) = autostart.Enable();
                return output.Report(Strings.Get("app.name"), ok, message);
            }

            case "off":
            {
                (bool ok, string message) = autostart.Disable();
                return output.Report(Strings.Get("app.name"), ok, message);
            }

            case null or "":
                output.Line(Strings.Get(autostart.IsEnabled() ? "autostart.enabled" : "autostart.disabled"));
                return VerbResult.Exit(ExitCodes.Success);

            default:
                output.Fail(Strings.Get("app.name"), Strings.Get("autostart.usage"));
                return VerbResult.Exit(ExitCodes.ConfigurationError);
        }
    }

    public VerbResult Language(VerbOutput output, string? code)
    {
        string requested = code?.Trim() ?? string.Empty;

        if (requested.Length == 0)
        {
            ListLanguages(output);
            return VerbResult.Exit(ExitCodes.Success);
        }

        //"auto" is the empty setting spelled out. A user cannot type nothing on a command line.
        bool automatic = requested.Equals("auto", StringComparison.OrdinalIgnoreCase);

        if (!automatic && !Strings.Has(requested))
        {
            output.Fail(Strings.Get("app.name"), Strings.Get("language.unknown", requested));
            output.Line();
            ListLanguages(output);
            return VerbResult.Exit(ExitCodes.ConfigurationError);
        }

        settings.Language = automatic ? string.Empty : requested.ToLowerInvariant();
        settings.Save();

        //The applied name, not the requested code: "auto" has to resolve through the operating system
        //to say anything useful.
        Strings.Use(settings.Language);

        //A struct, so FirstOrDefault cannot answer "not found" with null -- the pattern is what
        //distinguishes a real row from the default one.
        string name = Strings.Available.FirstOrDefault(language => language.Code == Strings.CurrentCode)
            is { Name.Length: > 0 } found
                ? found.Name
                : Strings.CurrentCode;

        output.Line(Strings.Get("language.set", name));
        output.Line(Strings.Get("language.restart"));

        return VerbResult.Exit(ExitCodes.Success);
    }

    public VerbResult Timings(VerbOutput output)
    {
        IReadOnlyList<OperationTimings.Summary> summaries = timings.Summarise();

        if (summaries.Count == 0)
        {
            //Honest about the limitation rather than printing an empty table: measurements live in the
            //process that took them, so a one-shot launch has only its own.
            output.Line("No measurements in this process.");
            output.Line("Timings accumulate in the resident service — start it with `flick autostart on`.");
            return VerbResult.Exit(ExitCodes.Success);
        }

        output.Line($"{"operation",-32} {"n",4} {"median",8} {"max",8}");

        foreach (OperationTimings.Summary summary in summaries)
        {
            output.Line(
                $"{summary.Operation,-32} {summary.Count,4} {summary.MedianMs,8:F1} {summary.MaxMs,8:F1}");
        }

        return VerbResult.Exit(ExitCodes.Success);
    }

    /// <summary>
    /// The embedded languages, one per line, with the one in use marked. Names as each language writes
    /// its own, never translated: someone looking for their language in an interface they cannot read
    /// is looking for "Francais", not "French".
    /// </summary>
    private void ListLanguages(VerbOutput output)
    {
        bool following = settings.Language.Length == 0;

        foreach (Strings.Language language in Strings.Available)
        {
            bool inUse = language.Code == Strings.CurrentCode;

            string marker = inUse ? "*" : " ";
            string note = inUse && following ? $"  ({Strings.Get("language.auto")})" : string.Empty;

            output.Line($"{marker} {language.Code,-4} {language.Name}{note}");
        }

        output.Line();
        output.Line(Strings.Get("language.usage"));
    }
}
