using FlickGit.Ai;
using FlickGit.Logging;

namespace FlickGit.App.Settings;

/// <summary>
/// The settings and the stored key, folded into the one question everything else asks: may we, and
/// with what?
///
/// Separate from <see cref="FlickSettings"/> because two of the four inputs are not settings — the
/// key is in Credential Manager and the provider name has to be parsed — and separate from the
/// generators because <c>FlickGit.Core</c> knows nothing about either.
/// </summary>
public sealed class AiConfiguration(FlickSettings settings, ApiKeyStore keys, ILog log)
{
    public AiProvider Provider => Parse(settings.AiProvider);

    public bool HasKey => keys.Has(Provider);

    /// <summary>
    /// Whether the user has agreed that source code may be sent.
    ///
    /// CLAUDE.md: off by default, "shown once with a clear explanation on first use, not buried".
    /// </summary>
    public bool DiffsMayLeave => settings.AiAllowDiffsToLeaveMachine;

    public bool ConsentAsked => settings.AiDiffConsentShown;

    /// <summary>
    /// All three conditions. Anything less and the message box is an ordinary editable field with a
    /// one-line notice, which CLAUDE.md requires of every failure mode: "The AI is an accelerator,
    /// never a dependency."
    /// </summary>
    public bool IsUsable => Provider != AiProvider.Disabled && HasKey && DiffsMayLeave;

    public AiOptions Options => new(
        Provider,
        settings.AiModel,
        settings.AiReasoningEffort,
        settings.AiMaxDiffBytes,
        settings.AiConventionalCommits);

    /// <summary>The key, read on demand. Never held in a field — see <see cref="ApiKeyStore"/>.</summary>
    public string? ReadKey() => keys.Read(Provider);

    /// <summary>Records the answer to the one-time "diffs may leave this machine" question.</summary>
    public void RememberConsent(bool allowed)
    {
        settings.AiDiffConsentShown = true;
        settings.AiAllowDiffsToLeaveMachine = allowed;
        settings.Save();

        log.Info(allowed
            ? "Consent given: diffs may be sent to the configured AI provider."
            : "Consent declined: no diff will be sent.");
    }

    private AiProvider Parse(string name)
    {
        if (Enum.TryParse(name, ignoreCase: true, out AiProvider provider))
            return provider;

        //A typo in a hand-edited settings file must not silently pick a provider. Disabled is the
        //safe reading of "I do not recognise this".
        if (name.Length > 0)
            log.Warn($"'{name}' is not a known AI provider; treating it as disabled.");

        return AiProvider.Disabled;
    }
}
