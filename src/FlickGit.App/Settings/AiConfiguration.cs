using FlickGit.Ai;
using FlickGit.Logging;

namespace FlickGit.App.Settings;

/// <summary>
/// The settings and the stored key, folded into the one question everything else asks: may we, and
/// with what?
///
/// Separate from <see cref="FlickSettings"/> because one of the inputs is not a setting — the key is
/// in Credential Manager — and separate from the generators because <c>FlickGit.Core</c> knows
/// nothing about either.
/// </summary>
public sealed class AiConfiguration(FlickSettings settings, CredentialStore keys, ILog log)
{
    public AiProvider Provider => Parse(settings.AiProvider);

    public bool HasKey => Provider != AiProvider.Disabled && keys.Has(CredentialStore.AiTarget(Provider));

    /// <summary>
    /// Both conditions, and they are the whole of it: <b>a provider with a key stored for it is the
    /// consent to send it a diff.</b> Nothing else is what an AI provider is configured for here —
    /// every message it writes is written from one.
    ///
    /// Anything less and the message box is an ordinary editable field with a one-line notice, which
    /// CLAUDE.md requires of every failure mode: "The AI is an accelerator, never a dependency."
    /// </summary>
    public bool IsUsable => Provider != AiProvider.Disabled && HasKey;

    public AiOptions Options => new(
        Provider,
        settings.AiModel,
        settings.AiReasoningEffort,
        settings.AiMaxDiffBytes,
        settings.AiConventionalCommits);

    /// <summary>The key, read on demand. Never held in a field — see <see cref="CredentialStore"/>.</summary>
    public string? ReadKey() => Provider == AiProvider.Disabled ? null : keys.Read(CredentialStore.AiTarget(Provider));

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
