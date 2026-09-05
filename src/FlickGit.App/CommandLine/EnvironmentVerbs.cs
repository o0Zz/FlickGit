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
    AiConfiguration ai,
    PromptStore prompts,
    CredentialStore keys,
    ActionCatalog catalog,
    GitExecutable git,
    IGitProcessRunner runner,
    FlickSettings settings,
    EnvironmentReports reports) : IEnvironmentVerbs
{
    /// <summary>
    /// The settings window while it is open, so a second request activates it rather than opening a
    /// second one. Null whenever there is none -- the Closed handler is what keeps that true.
    /// </summary>
    private SettingsWindow? _settingsWindow;

    public VerbResult Help(VerbOutput output) => reports.Help(output);

    public VerbResult Version(VerbOutput output) => reports.ReportVersion(output);

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
    public VerbResult Autostart(VerbOutput output, string? switchTo) =>
        reports.Autostart(output, switchTo);

    /// <summary>
    /// `flick ai`, and `flick ai key [set|clear]`.
    ///
    /// Straight through to <see cref="EnvironmentReports"/>: every line of this verb is portable
    /// except the password box, and that went behind <see cref="ISecretPrompt"/> so both hosts
    /// answer it out of one place.
    /// </summary>
    public Task<VerbResult> AiAsync(VerbOutput output, string? subcommand, string? action) =>
        reports.AiAsync(output, subcommand, action);

    /// <summary>
    /// `flick language [code|auto]`. The settings window has the same picker; this stays for the
    /// reason `flick autostart` does. Both read <see cref="Strings.Available"/> rather than a list of
    /// codes, so neither can offer a language the exe was not built with.
    /// </summary>
    public VerbResult Language(VerbOutput output, string? code) => reports.Language(output, code);

    /// <summary>`flick diag doctor` -- what is installed, and where things live.</summary>
    public async Task<VerbResult> DoctorAsync(VerbOutput output)
    {
        output.Line($"FlickGit {EnvironmentReports.Version}");
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
        output.Line($"logs             {FlickSettings.LogsDirectoryPath}");
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
    public VerbResult Timings(VerbOutput output) => reports.Timings(output);

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
