using FlickGit.Ai;
using FlickGit.Logging;

namespace FlickGit.App.Settings;

/// <summary>
/// The settings and the stored key, folded into the one question everything else asks: may we, and
/// with what?
///
/// Separate from <see cref="FlickSettings"/> because one of the inputs is not a setting — the key is
/// in the operating system’s keystore — and separate from the generators because <c>FlickGit.Core</c> knows
/// nothing about either.
/// </summary>
public sealed class AiConfiguration(FlickSettings settings, ISecretStore keys, ILog log)
{
    public AiProvider Provider => Parse(settings.AiProvider);

    /// <summary>
    /// Whether this provider needs a credential at all.
    ///
    /// False for Ollama, which is the whole shape of the difference: it runs on the user's machine,
    /// so there is nobody to authenticate to and nothing to store.
    /// </summary>
    public bool RequiresKey => AiOptions.RequiresKey(Provider);

    /// <summary>Whether a key is stored. Always false for a provider that needs none.</summary>
    public bool HasKey => RequiresKey && keys.Has(SecretTargets.AiTarget(Provider));

    /// <summary>
    /// Both conditions, and they are the whole of it: <b>a provider with a key stored for it is the
    /// consent to send it a diff.</b> Nothing else is what an AI provider is configured for here —
    /// every message it writes is written from one.
    ///
    /// Ollama satisfies it with the provider alone, and that is not a hole in the argument: the
    /// consent a key stands for is consent to send code to <i>somebody else</i>, and a local model
    /// sends it nowhere. There is no credential that could be asked for.
    ///
    /// Anything less and the message box is an ordinary editable field with a one-line notice, which
    /// CLAUDE.md requires of every failure mode: "The AI is an accelerator, never a dependency."
    /// </summary>
    public bool IsUsable => Provider != AiProvider.Disabled && (!RequiresKey || HasKey);

    public AiOptions Options => new(
        Provider,
        settings.AiModel,
        settings.AiReasoningEffort,
        settings.AiMaxDiffBytes,
        settings.AiConventionalCommits)
    {
        OllamaUrl = settings.AiOllamaUrl,
    };

    /// <summary>The key, read on demand. Never held in a field — see <see cref="CredentialStore"/>.</summary>
    public string? ReadKey() => RequiresKey ? keys.Read(SecretTargets.AiTarget(Provider)) : null;

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
