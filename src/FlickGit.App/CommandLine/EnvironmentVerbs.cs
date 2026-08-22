using FlickGit.App.Localization;
using FlickGit.App.Resident;
using FlickGit.App.Settings;
using FlickGit.Ai;
using FlickGit.App.Ai;
using FlickGit.App.Shell;
using FlickGit.Actions;
using FlickGit.App.Trigger;
using System.Diagnostics;
using FlickGit.App.Views;
using FlickGit.Cli;
using FlickGit.Diagnostics;
using FlickGit.Git;
using FlickGit.Logging;
using FlickGit.Models;

namespace FlickGit.App.CommandLine;

/// <summary>
/// The verbs that answer about the installation rather than about a repository: help, version,
/// shell integration, autostart, and the two <c>diag</c> commands.
///
/// Split from <see cref="RepositoryVerbs"/> because <c>doctor</c> is the one thing that asks every
/// environment question at once, and gathering those in the class that also runs `git push` gave a
/// constructor that told you nothing. Here the dependency list *is* what doctor reports.
/// </summary>
public sealed class EnvironmentVerbs(
    ShellIntegration shell,
    Autostart autostart,
    ResidentService resident,
    TriggerService trigger,
    CommitMessageService messages,
    AiConfiguration ai,
    ApiKeyStore keys,
    ActionCatalog catalog,
    GitExecutable git,
    IGitProcessRunner runner,
    FlickSettings settings,
    OperationTimings timings)
{
    public VerbResult Help(VerbOutput output)
    {
        output.Line(Verb.HelpText);
        return VerbResult.Exit(ExitCodes.Success);
    }

    public VerbResult Version(VerbOutput output)
    {
        output.Line($"FlickGit {App.Version}");
        return VerbResult.Exit(ExitCodes.Success);
    }

    /// <summary>`flick install-shell` / `flick uninstall-shell`.</summary>
    public VerbResult ContextMenu(VerbOutput output, bool install)
    {
        InstallResult result = install ? shell.Install() : shell.Uninstall();
        return output.Report(Strings.Get("app.name"), result.Succeeded, result.Message);
    }

    /// <summary>
    /// `flick autostart [on|off]` — the logon task, and what it currently is.
    ///
    /// A verb rather than a settings row, because there is no settings window at all
    /// and a logon task registered silently by some other command would be exactly the kind of
    /// surprise this product should not spring.
    /// </summary>
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
                //No argument: say what it is now. A status query never changes anything.
                output.Line(Strings.Get(autostart.IsEnabled() ? "autostart.enabled" : "autostart.disabled"));
                return VerbResult.Exit(ExitCodes.Success);

            default:
                output.Fail(Strings.Get("app.name"), Strings.Get("autostart.usage"));
                return VerbResult.Exit(ExitCodes.ConfigurationError);
        }
    }

    /// <summary>
    /// `flick ai` — what the AI is configured to do, and `flick ai key [set|clear]`.
    ///
    /// A verb rather than a settings row for the same reason as `flick autostart`: the settings
    /// files are the interface, and a key stored by some other command would be exactly the kind of
    /// surprise this product should not spring.
    /// </summary>
    public async Task<VerbResult> AiAsync(VerbOutput output, string? subcommand, string? action)
    {
        switch (subcommand?.Trim().ToLowerInvariant())
        {
            case null or "":
                return await ReportAiAsync(output).ConfigureAwait(true);

            case "key":
                return AiKey(output, action);

            default:
                output.Fail(Strings.Get("app.name"), Strings.Get("ai.usage"));
                return VerbResult.Exit(ExitCodes.ConfigurationError);
        }
    }

    private VerbResult AiKey(VerbOutput output, string? action)
    {
        AiProvider provider = ai.Provider;

        if (provider == AiProvider.Disabled)
        {
            output.Fail(Strings.Get("app.name"), Strings.Get("ai.key.noprovider"));
            return VerbResult.Exit(ExitCodes.ConfigurationError);
        }

        switch (action?.Trim().ToLowerInvariant())
        {
            case "clear":
                return output.Report(Strings.Get("app.name"), keys.Clear(provider), Strings.Get("ai.key.cleared", provider.ToString()));

            case "set":
            {
                //A window, not an argument. A key on a command line is in the shell's history and
                //visible in the process list -- see ApiKeyWindow for the whole argument.
                string? typed = ApiKeyWindow.Ask(provider);

                if (typed is null)
                    return output.Report(Strings.Get("app.name"), false, Strings.Get("ai.key.cancelled"));

                bool stored = keys.Write(provider, typed);

                return output.Report(
                    Strings.Get("app.name"),
                    stored,
                    stored ? Strings.Get("ai.key.saved", provider.ToString()) : Strings.Get("ai.key.failed"));
            }

            case null or "":
                //A status query never changes anything, and never prints the key.
                output.Line(Strings.Get(
                    keys.Has(provider) ? "ai.key.stored" : "ai.key.missing",
                    provider.ToString(),
                    ApiKeyStore.TargetFor(provider)));

                return VerbResult.Exit(ExitCodes.Success);

            default:
                output.Fail(Strings.Get("app.name"), Strings.Get("ai.usage"));
                return VerbResult.Exit(ExitCodes.ConfigurationError);
        }
    }

    private async Task<VerbResult> ReportAiAsync(VerbOutput output)
    {
        AiProvider provider = ai.Provider;

        output.Line($"provider     {provider.ToString().ToLowerInvariant()}");

        if (provider == AiProvider.Disabled)
        {
            output.Line();
            output.Line(Strings.Get("ai.disabled.hint", FlickSettings.FilePath));
            return VerbResult.Exit(ExitCodes.Success);
        }

        output.Line($"model        {ai.Options.ResolvedModel}");
        output.Line($"api key      {(ai.HasKey ? $"stored ({ApiKeyStore.TargetFor(provider)})" : "not set — store one with `flick ai key set`")}");
        output.Line($"diffs        {(ai.DiffsMayLeave ? "may leave this machine" : "may NOT leave this machine — asked once on first use")}");
        output.Line($"max diff     {ai.Options.MaxDiffBytes / 1024} KB (hard ceiling {DiffPayload.TokenCeilingBytes / 1024} KB of payload)");

        //Only worth a round trip when a request could actually be made.
        if (messages.IsUsable)
        {
            AiProbe probe = await messages.ProbeAsync(CancellationToken.None).ConfigureAwait(true);

            output.Line(probe.Reachable
                ? $"endpoint     reachable in {probe.Elapsed.TotalMilliseconds:F0} ms"
                : $"endpoint     unreachable — {probe.Error}");
        }

        if (messages.LastFailure is { } failure)
            output.Line($"failures     {messages.ConsecutiveFailures} consecutive — {failure}");

        return VerbResult.Exit(ExitCodes.Success);
    }

    /// <summary>
    /// `flick language` — what the interface languages are, and which one is in use.
    /// `flick language fr` switches to one; `flick language auto` goes back to following Windows.
    ///
    /// A verb rather than a settings window, for the same reason as `flick autostart` and
    /// `flick ai key`: CLAUDE.md Phase 5 dropped the window, so the settings files are the
    /// interface and a verb is what answers "where do I change this?" in one command. This one
    /// earns its place over hand-editing <c>settings.json</c> because the user has to know the
    /// code before they can type it, and only the exe knows which files are embedded.
    /// </summary>
    public VerbResult Language(VerbOutput output, string? code)
    {
        string requested = code?.Trim() ?? string.Empty;

        if (requested.Length == 0)
        {
            ListLanguages(output);
            return VerbResult.Exit(ExitCodes.Success);
        }

        //"auto" is the empty setting spelled out. A user cannot type nothing on a command line, and
        //`flick language ""` is not a thing anyone would guess.
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

        //The applied name, not the requested code: "auto" has to resolve through Windows to say
        //anything useful, and an embedded file is the only place the name lives.
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

    /// <summary>
    /// The embedded languages, one per line, with the one in use marked.
    ///
    /// Names are shown as each language writes its own, never translated: someone looking for their
    /// language in an interface they cannot read is looking for "Français", not for "French".
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

    /// <summary>`flick diag doctor` — what is installed, and where things live.</summary>
    public async Task<VerbResult> DoctorAsync(VerbOutput output)
    {
        output.Line($"FlickGit {App.Version}");
        output.Line();

        if (!git.IsAvailable)
        {
            output.Line("git.exe          NOT FOUND");
            output.Line();
            output.Line("Install Git for Windows, or set the path in settings.json.");
            return VerbResult.Exit(ExitCodes.ConfigurationError);
        }

        output.Line($"git.exe          {git.Path}");

        GitResult version = await runner
            .ReadAsync(null, ["--version"], CancellationToken.None)
            .ConfigureAwait(true);

        output.Line($"git version      {version.StdOut.Trim()}");
        output.Line($"context menu     {(shell.IsInstalled() ? "installed" : "not installed")}");
        output.Line($"start at logon   {(autostart.IsEnabled() ? "enabled" : "disabled")}");
        output.Line($"resident service {(resident.IsRunning() ? "running" : "not running")}");
        output.Line($"trigger          {trigger.Describe()}");
        output.Line($"palette          {trigger.DescribePalette()}");
        output.Line($"palette roots    {DescribeScanRoots()}");
        output.Line($"actions          {DescribeActions()}");
        output.Line($"ai               {DescribeAi()}");
        output.Line($"language         {DescribeLanguage()}");
        output.Line($"settings         {FlickSettings.FilePath}");
        output.Line($"logs             {FileLog.DefaultDirectory}");
        output.Line();

        //CLAUDE.md, "Repository Palette": suggest core.fsmonitor for large repositories, where it
        //takes `git status` from ~300 ms to a few milliseconds on Windows.
        output.Line("For a large repository, consider:  git config core.fsmonitor true");

        return VerbResult.Exit(ExitCodes.Success);
    }

    /// <summary>
    /// The catalog's state, for `diag doctor`.
    ///
    /// Names the load failure when there is one. A custom action that silently stopped appearing is
    /// otherwise unanswerable, and this is where people look.
    /// </summary>
    private string DescribeActions()
    {
        int custom = catalog.All.Count(a => !a.IsBuiltIn);
        int hidden = catalog.All.Count(a => a.Hidden);

        string summary = $"{catalog.All.Count} ({custom} custom, {hidden} hidden)";

        return catalog.LoadError is { Length: > 0 } error ? $"{summary} - {error}" : summary;
    }

    /// <summary>
    /// Where the palette looks for repositories, for `diag doctor`.
    ///
    /// "none" is not a fault: the palette fills itself from the most-recently-used list as soon as
    /// the tool has been used once. Saying so is what stops an empty palette from reading as broken.
    /// </summary>
    private string DescribeScanRoots() =>
        settings.PaletteScanRoots.Count == 0
            ? "none configured (recent repositories only)"
            : string.Join(", ", settings.PaletteScanRoots);

    /// <summary>
    /// The one-line AI summary for `diag doctor`. The detail lives in `flick ai`, which is where a
    /// user who wants it will look — and doctor stays one screen.
    /// </summary>
    private string DescribeAi()
    {
        AiProvider provider = ai.Provider;

        if (provider == AiProvider.Disabled)
            return "disabled";

        string name = provider.ToString().ToLowerInvariant();

        if (!ai.HasKey)
            return $"{name} (no key)";

        return ai.DiffsMayLeave ? $"{name} ({ai.Options.ResolvedModel})" : $"{name} (diffs not allowed to leave)";
    }

    /// <summary>
    /// The language in use, for `diag doctor`.
    ///
    /// Names the requested code when it is not the one in use, because "I set it to sv and nothing
    /// changed" is otherwise unanswerable: there is no sv.lang, and this is the line that says so.
    /// </summary>
    private string DescribeLanguage()
    {
        string requested = settings.Language.Trim();
        string current = Strings.CurrentCode;

        if (requested.Length == 0)
            return $"{current} (following Windows)";

        return requested.Equals(current, StringComparison.OrdinalIgnoreCase)
            ? current
            : $"{current} - no language file for '{requested}'";
    }

    /// <summary>`flick diag timings` — recent latency measurements.</summary>
    public VerbResult Timings(VerbOutput output)
    {
        IReadOnlyList<OperationTimings.Summary> summaries = timings.Summarise();

        if (summaries.Count == 0)
        {
            //Honest about the limitation rather than printing an empty table: measurements live in
            //the process that took them, so a one-shot launch has only its own.
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
    /// `flick settings`. Reports where the two files are, and opens the folder holding them.
    ///
    /// <b>There is no settings window, by choice</b> — see CLAUDE.md, Phase 5. Its largest section
    /// would have been a graphical front end for a file that is documented and hand-editable, so
    /// <c>settings.json</c> and <c>actions.json</c> are the interface. This verb is what makes that a
    /// decision rather than a gap: it answers "where do I change this?" in one command.
    /// </summary>
    public VerbResult Settings(VerbOutput output)
    {
        output.Line(Strings.Get("settings.location"));
        output.Line();
        output.Line($"  {FlickSettings.FilePath}");
        output.Line($"  {FlickSettings.ActionsFilePath}");
        output.Line();
        output.Line(Strings.Get("settings.reload"));
        output.Line(Strings.Get("settings.language"));

        try
        {
            //The folder rather than either file: a .json with no handler registered would fail, and
            //explorer.exe opening a directory always works.
            using Process? opened = Process.Start(new ProcessStartInfo
            {
                FileName = FlickSettings.DirectoryPath,
                UseShellExecute = true,
            });

            _ = opened;
        }
        catch (Exception ex)
        {
            //The paths are already printed, which is the part that answers the question.
            output.Line();
            output.Line(Strings.Get("settings.openfailed", ex.Message));
        }

        return VerbResult.Exit(ExitCodes.Success);
    }
}
