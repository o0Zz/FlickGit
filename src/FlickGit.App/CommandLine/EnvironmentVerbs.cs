using FlickGit.App.Localization;
using FlickGit.App.Resident;
using FlickGit.App.Settings;
using FlickGit.Ai;
using FlickGit.App.Ai;
using FlickGit.App.Shell;
using FlickGit.Shared;
using FlickGit.Actions;
using FlickGit.App.Trigger;
using System.Windows;
using FlickGit.App.Views;
using FlickGit.Cli;
using FlickGit.Diagnostics;
using FlickGit.Git;
using FlickGit.Logging;
using FlickGit.Models;

namespace FlickGit.App.CommandLine;

/// <summary>
/// The verbs that answer about the installation rather than about a repository: help, version,
/// shell integration, autostart, and the two <c>diag</c> commands. Split from
/// <see cref="RepositoryVerbs"/> because <c>doctor</c> asks every environment question at once, so
/// here the dependency list <i>is</i> what doctor reports.
/// </summary>
public sealed class EnvironmentVerbs(
    ShellIntegration shell,
    OverlayIntegration overlay,
    Autostart autostart,
    ResidentService resident,
    TriggerService trigger,
    AiTextService messages,
    AiConfiguration ai,
    PromptStore prompts,
    CredentialStore keys,
    ActionCatalog catalog,
    GitExecutable git,
    IGitProcessRunner runner,
    FlickSettings settings,
    OperationTimings timings)
{
    /// <summary>
    /// The settings window while it is open, so a second request activates it rather than opening a
    /// second one. Null whenever there is none -- the Closed handler is what keeps that true.
    /// </summary>
    private SettingsWindow? _settingsWindow;

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
    /// `flick install-overlay [system]` / `flick uninstall-overlay [system]`.
    ///
    /// Bare, this is the whole operation: the user half, then the machine half behind a UAC prompt.
    /// With <c>system</c> it is the machine half alone, already elevated -- which is both what the
    /// prompt above starts, and how an administrator deploying to many machines writes that one key
    /// from a script without a prompt at all.
    ///
    /// <b>The <c>system</c> half never opens a window.</b> It answers with a line and an exit code,
    /// never through <see cref="VerbOutput.Report"/>, because <c>Report</c> falls through to a
    /// <c>NoticeWindow</c> when there is no console -- and the elevated child is started with
    /// <c>UseShellExecute</c>, so it has none. The half the user actually invoked is the half that
    /// reports.
    /// </summary>
    public async Task<VerbResult> OverlayAsync(VerbOutput output, bool install, string? scope)
    {
        bool systemOnly = string.Equals(scope?.Trim(), "system", StringComparison.OrdinalIgnoreCase);

        if (systemOnly)
        {
            InstallResult half = install ? overlay.InstallSystem() : overlay.UninstallSystem();

            output.Line(half.Message);
            return VerbResult.Exit(half.Succeeded ? ExitCodes.Success : ExitCodes.ConfigurationError);
        }

        //Anything else in that slot is a typo, and a typo that silently registered the overlay would
        //be a UAC prompt the user did not ask for.
        if (!string.IsNullOrWhiteSpace(scope))
        {
            output.Fail(Strings.Get("app.name"), Strings.Get("overlay.usage"));
            return VerbResult.Exit(ExitCodes.ConfigurationError);
        }

        InstallResult result = install
            ? await overlay.InstallAsync().ConfigureAwait(true)
            : await overlay.UninstallAsync().ConfigureAwait(true);

        if (result.Succeeded)
            return output.Report(Strings.Get("app.name"), true, result.Message);

        //Declining the UAC prompt is a decision, not a configuration error, and exit code 3 is what
        //every other surface uses for it.
        bool declined = result.Message == Strings.Get("overlay.declined");

        output.Fail(Strings.Get("app.name"), result.Message);
        return VerbResult.Exit(declined ? ExitCodes.UserCancelled : ExitCodes.ConfigurationError);
    }

    /// <summary>
    /// `flick autostart [on|off]`. A verb as well as the settings checkbox, because a logon task is
    /// something a script and an unattended install both want to set, and neither has a window to tick.
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
                output.Line(Strings.Get(autostart.IsEnabled() ? "autostart.enabled" : "autostart.disabled"));
                return VerbResult.Exit(ExitCodes.Success);

            default:
                output.Fail(Strings.Get("app.name"), Strings.Get("autostart.usage"));
                return VerbResult.Exit(ExitCodes.ConfigurationError);
        }
    }

    /// <summary>`flick ai`, and `flick ai key [set|clear]`.</summary>
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

        if (!AiOptions.RequiresKey(provider))
        {
            //Refused rather than stored. A key filed for Ollama would be read by nothing, and accepting one
            //would suggest the local provider is somehow half configured until you do.
            output.Fail(Strings.Get("app.name"), Strings.Get("ai.key.notneeded", provider.ToString()));
            return VerbResult.Exit(ExitCodes.ConfigurationError);
        }

        switch (action?.Trim().ToLowerInvariant())
        {
            case "clear":
                return output.Report(Strings.Get("app.name"), keys.Clear(CredentialStore.AiTarget(provider)), Strings.Get("ai.key.cleared", provider.ToString()));

            case "set":
            {
                //A window, not an argument. A key on a command line is in the shell's history and visible in the
                //process list.
                string? typed = SecretWindow.AskForApiKey(provider);

                if (typed is null)
                    return output.Report(Strings.Get("app.name"), false, Strings.Get("ai.key.cancelled"));

                bool stored = keys.Write(CredentialStore.AiTarget(provider), typed);

                return output.Report(
                    Strings.Get("app.name"),
                    stored,
                    stored ? Strings.Get("ai.key.saved", provider.ToString()) : Strings.Get("ai.key.failed"));
            }

            case null or "":
                //A status query never changes anything, and never prints the key.
                output.Line(Strings.Get(
                    keys.Has(CredentialStore.AiTarget(provider)) ? "ai.key.stored" : "ai.key.missing",
                    provider.ToString(),
                    CredentialStore.AiTarget(provider)));

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

        //Named rather than left empty, because "no model" is the one configuration error Ollama can have
        //and the whole of its fix is one `ollama list` away.
        output.Line($"model        {(ai.Options.ResolvedModel is { Length: > 0 } model ? model : "not set — required for Ollama; run `ollama list`")}");

        if (provider == AiProvider.Ollama)
        {
            output.Line($"endpoint     {ai.Options.OllamaUrl}");
            output.Line("api key      not needed — Ollama runs locally");

            //The reason to run it, said plainly here because this verb is where the privacy question is
            //answered for every other provider.
            output.Line(ai.Options.OllamaUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                    || ai.Options.OllamaUrl.Contains("127.0.0.1", StringComparison.Ordinal)
                ? "diffs        stay on this machine"
                : "diffs        are sent to the Ollama host named above");
        }
        else
        {
            output.Line($"api key      {(ai.HasKey ? $"stored ({CredentialStore.AiTarget(provider)})" : "not set — store one with `flick ai key set`")}");
            output.Line("diffs        the diff of the files being committed is sent to this provider");
        }
        output.Line($"max diff     {ai.Options.MaxDiffBytes / 1024} KB (hard ceiling {DiffPayload.TokenCeilingBytes / 1024} KB of payload)");

        ResolvedPrompt commit = prompts.ForCommit(ai.Options.ConventionalCommits);

        output.Line($"prompt       {DescribePrompt(commit, PromptStore.CommitFileName)}");

        //Only when it would otherwise look ignored. The setting is real and does nothing while a file
        //is in use, and a user who set it and saw no change has no other way to find that out.
        if (commit.Source is not null && ai.Options.ConventionalCommits)
            output.Line("             aiConventionalCommits is not consulted while that file exists");

        output.Line($"pr prompt    {DescribePrompt(prompts.ForPullRequest(), PromptStore.PullRequestFileName)}");
        output.Line($"changelog    {DescribePrompt(prompts.ForChangelog(), PromptStore.ChangelogFileName)}");

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
    /// `flick language [code|auto]`. The settings window has the same picker; this stays for the
    /// reason `flick autostart` does. Both read <see cref="Strings.Available"/> rather than a list of
    /// codes, so neither can offer a language the exe was not built with.
    /// </summary>
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

        //The applied name, not the requested code: "auto" has to resolve through Windows to say anything
        //useful.
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

    /// <summary>`flick diag doctor` -- what is installed, and where things live.</summary>
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
        output.Line($"folder overlay   {DescribeOverlay()}");
        output.Line($"start at logon   {(autostart.IsEnabled() ? "enabled" : "disabled")}");
        output.Line($"resident service {(resident.IsRunning() ? "running" : "not running")}");
        output.Line($"trigger          {trigger.Describe()}");
        output.Line($"palette          {trigger.DescribePalette()}");
        output.Line($"palette roots    {DescribeScanRoots()}");
        output.Line($"actions          {DescribeActions()}");
        output.Line($"ai               {DescribeAi()}");
        output.Line($"prompts          {DescribePrompts()}");
        output.Line($"language         {DescribeLanguage()}");
        output.Line($"settings         {FlickSettings.FilePath}");
        output.Line($"logs             {FileLog.DefaultDirectory}");
        output.Line();

        //core.fsmonitor takes `git status` from ~300 ms to a few milliseconds on Windows.
        output.Line("For a large repository, consider:  git config core.fsmonitor true");

        return VerbResult.Exit(ExitCodes.Success);
    }

    /// <summary>
    /// The overlay's state, and the slot arithmetic that decides whether it is drawn.
    ///
    /// <b>The position is the point.</b> Windows loads only the first
    /// <see cref="ShellCommandIds.OverlaySlotLimit"/> handlers, sorted by key name, and a
    /// registration past that is invisible in every other way -- the key is there, the DLL is fine,
    /// and nothing is ever drawn. Whether Explorer actually loaded ours cannot be answered from
    /// outside <c>explorer.exe</c>, so this reports the arithmetic instead of guessing.
    ///
    /// It also names the orphan case: a machine key with no user half behind it, which is what an
    /// uninstall that could not elevate leaves behind.
    /// </summary>
    private string DescribeOverlay()
    {
        OverlaySlots slots = overlay.Slots();

        if (slots.Position is not { } position)
            return "not installed";

        string where = $"slot {position} of {slots.Registered.Count} registered";

        if (slots.WithinLimit is false)
        {
            return $"registered but NOT DRAWN -- {where}, and Windows loads only " +
                   $"{slots.Limit}. Remove an overlay handler you do not use.";
        }

        return overlay.IsInstalled()
            ? $"installed, {where}"
            : $"ORPHANED -- {where}, but nothing is registered for it under HKCU. " +
              "Run `flick uninstall-overlay` as administrator to remove it.";
    }

    /// <summary>
    /// The catalog's state. Names the load failure when there is one: a custom action that silently
    /// stopped appearing is otherwise unanswerable, and this is where people look.
    /// </summary>
    private string DescribeActions()
    {
        int custom = catalog.All.Count(a => !a.IsBuiltIn);
        int hidden = catalog.All.Count(a => a.Hidden);

        string summary = $"{catalog.All.Count} ({custom} custom, {hidden} hidden)";

        return catalog.LoadError is { Length: > 0 } error ? $"{summary} - {error}" : summary;
    }

    /// <summary>
    /// Where the palette looks for repositories. "none" is not a fault -- the most-recently-used list
    /// fills it as soon as the tool has been used once, and saying so stops an empty palette from
    /// reading as broken.
    /// </summary>
    private string DescribeScanRoots() =>
        settings.PaletteScanRoots.Count == 0
            ? "none configured (recent repositories only)"
            : string.Join(", ", settings.PaletteScanRoots);

    /// <summary>The one-line AI summary. The detail lives in `flick ai`, so doctor stays one screen.</summary>
    private string DescribeAi()
    {
        AiProvider provider = ai.Provider;

        if (provider == AiProvider.Disabled)
            return "disabled";

        string name = provider.ToString().ToLowerInvariant();

        //A missing key is only a fault for a provider that needs one -- otherwise this reported a
        //perfectly configured Ollama as "no key".
        if (ai.RequiresKey && !ai.HasKey)
            return $"{name} (no key)";

        //The model is only optional for the three that have a default. Ollama has none.
        if (ai.Options.ResolvedModel is not { Length: > 0 } model)
            return $"{name} (no model — set aiModel; `ollama list` shows what is installed)";

        return $"{name} ({model})";
    }

    /// <summary>
    /// One prompt, for `flick ai`: the file it came from, or the built-in and why.
    ///
    /// A deleted file is not a fault -- deleting one is how a user goes back to the built-in -- so it
    /// reads as a statement rather than as a problem, and only a file that exists and could not be
    /// used carries a reason.
    /// </summary>
    private static string DescribePrompt(ResolvedPrompt prompt, string fileName) =>
        prompt.Source
            ?? (prompt.Error is { Length: > 0 } error
                ? $"built-in — {error}"
                : $"built-in ({fileName} is not there; it is written at startup)");

    /// <summary>
    /// Every prompt in one line, for doctor. Shaped like <see cref="DescribeActions"/>: a summary, and
    /// the reason appended when a file that exists was not used.
    /// </summary>
    private string DescribePrompts()
    {
        ResolvedPrompt commit = prompts.ForCommit(ai.Options.ConventionalCommits);
        ResolvedPrompt pullRequest = prompts.ForPullRequest();
        ResolvedPrompt changelog = prompts.ForChangelog();

        //"from file" rather than "custom": the files are seeded at first run, so every install would
        //read as customised and the word would carry no signal. What doctor is being asked is which
        //of them is actually in use.
        string summary =
            $"commit {(commit.Source is not null ? "from file" : "built-in")}, " +
            $"pr {(pullRequest.Source is not null ? "from file" : "built-in")}, " +
            $"changelog {(changelog.Source is not null ? "from file" : "built-in")}";

        string[] errors = [.. new[] { commit.Error, pullRequest.Error, changelog.Error }.OfType<string>()];

        return errors.Length > 0 ? $"{summary} - {string.Join("; ", errors)}" : summary;
    }

    /// <summary>
    /// The language in use. Names the requested code when it is not the one in use, because "I set it
    /// to sv and nothing changed" is otherwise unanswerable.
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

    /// <summary>`flick diag timings` -- recent latency measurements.</summary>
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

    /// <param name="tab">Which tab to open on. The tray's About entry is the only caller that picks.</param>
    public VerbResult Settings(VerbOutput output, SettingsTab tab = SettingsTab.General)
    {
        if (output.HasConsole)
        {
            output.Line(Strings.Get("settings.location"));
            output.Line();
            output.Line($"  {FlickSettings.FilePath}");
            output.Line($"  {FlickSettings.ActionsFilePath}");
        }

        //One window, reused while it is open. A second Settings click has to reach the one already on
        //screen, or the user ends up with two of them disagreeing about what the checkboxes say.
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(settings, shell, overlay, autostart, keys);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }
        else if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Select(tab);

        //The stub granted this process foreground rights before sending the request; without this the
        //window comes up behind whatever the user was looking at.
        _settingsWindow.Activate();

        return VerbResult.Stay();
    }
}
