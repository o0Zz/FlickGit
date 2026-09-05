using System.Reflection;
using FlickGit.Ai;
using FlickGit.App.Ai;
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
/// <b>Shared as a class both hosts delegate to, rather than as more interfaces.</b> Each host keeps
/// its own thin <c>IEnvironmentVerbs</c> and calls in here for the half that is shared.
///
/// <b><c>ai</c> joined them once <see cref="ISecretPrompt"/> existed</b>, and it is the clearest
/// case of the rule: every line of that verb is portable except the password box, which is the one
/// thing the seam hides. Two copies of it would have been two answers to what `flick ai` reports
/// about privacy — the sentence naming what leaves the machine — and one of them would have gone
/// stale.
/// </summary>
public sealed class EnvironmentReports(
    FlickSettings settings,
    IAutostart autostart,
    OperationTimings timings,
    AiConfiguration ai,
    AiTextService messages,
    PromptStore prompts,
    ISecretStore keys,
    ISecretPrompt prompt)
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

    /// <summary>
    /// One prompt, named for a report: the file it came from, or the built-in and why.
    ///
    /// A deleted file is not a fault — deleting one is how a user goes back to the built-in — so it
    /// reads as a statement rather than as a problem, and only a file that exists and could not be
    /// used carries a reason.
    ///
    /// Public because <c>diag doctor</c> says the same thing in one line and must not say it
    /// differently.
    /// </summary>
    public static string DescribePrompt(ResolvedPrompt prompt, string fileName) =>
        prompt.Source
            ?? (prompt.Error is { Length: > 0 } error
                ? $"built-in — {error}"
                : $"built-in ({fileName} is not there; it is written at startup)");

    /// <summary>`flick ai`, and `flick ai key [set|clear]`.</summary>
    public async Task<VerbResult> AiAsync(VerbOutput output, string? subcommand, string? action)
    {
        switch (subcommand?.Trim().ToLowerInvariant())
        {
            case null or "":
                return await ReportAiAsync(output).ConfigureAwait(true);

            case "key":
                return await AiKeyAsync(output, action).ConfigureAwait(true);

            default:
                output.Fail(Strings.Get("app.name"), Strings.Get("ai.usage"));

                return VerbResult.Exit(ExitCodes.ConfigurationError);
        }
    }

    private async Task<VerbResult> AiKeyAsync(VerbOutput output, string? action)
    {
        AiProvider provider = ai.Provider;

        if (provider == AiProvider.Disabled)
        {
            output.Fail(Strings.Get("app.name"), Strings.Get("ai.key.noprovider"));

            return VerbResult.Exit(ExitCodes.ConfigurationError);
        }

        if (!AiOptions.RequiresKey(provider))
        {
            //Refused rather than stored. A key filed for Ollama would be read by nothing, and
            //accepting one would suggest the local provider is somehow half configured until you do.
            output.Fail(Strings.Get("app.name"), Strings.Get("ai.key.notneeded", provider.ToString()));

            return VerbResult.Exit(ExitCodes.ConfigurationError);
        }

        switch (action?.Trim().ToLowerInvariant())
        {
            case "clear":
                return output.Report(
                    Strings.Get("app.name"),
                    keys.Clear(SecretTargets.AiTarget(provider)),
                    Strings.Get("ai.key.cleared", provider.ToString()));

            case "set":
            {
                //A window, not an argument. A key on a command line is in the shell's history and
                //visible in the process list.
                string? typed = await prompt.AskForApiKeyAsync(provider).ConfigureAwait(true);

                if (typed is null)
                    return output.Report(Strings.Get("app.name"), false, Strings.Get("ai.key.cancelled"));

                bool stored = keys.Write(SecretTargets.AiTarget(provider), typed);

                return output.Report(
                    Strings.Get("app.name"),
                    stored,
                    stored ? Strings.Get("ai.key.saved", provider.ToString()) : Strings.Get("ai.key.failed"));
            }

            case null or "":
                //A status query never changes anything, and never prints the key.
                output.Line(Strings.Get(
                    keys.Has(SecretTargets.AiTarget(provider)) ? "ai.key.stored" : "ai.key.missing",
                    provider.ToString(),
                    SecretTargets.AiTarget(provider)));

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

        //Named rather than left empty, because "no model" is the one configuration error Ollama can
        //have and the whole of its fix is one `ollama list` away.
        output.Line($"model        {(ai.Options.ResolvedModel is { Length: > 0 } model ? model : "not set — required for Ollama; run `ollama list`")}");

        if (provider == AiProvider.Ollama)
        {
            output.Line($"endpoint     {ai.Options.OllamaUrl}");
            output.Line("api key      not needed — Ollama runs locally");

            //The reason to run it, said plainly here because this verb is where the privacy question
            //is answered for every other provider.
            output.Line(ai.Options.OllamaUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)
                    || ai.Options.OllamaUrl.Contains("127.0.0.1", StringComparison.Ordinal)
                ? "diffs        stay on this machine"
                : "diffs        are sent to the Ollama host named above");
        }
        else
        {
            output.Line($"api key      {(ai.HasKey ? $"stored ({SecretTargets.AiTarget(provider)})" : "not set — store one with `flick ai key set`")}");
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
