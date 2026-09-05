using FlickGit.Ai;
using FlickGit.Forges;

namespace FlickGit.App.Settings;

/// <summary>
/// Asks the user for a secret, once, and hands it back.
///
/// <b>A seam because the question is a window and the callers are not.</b> Two pieces of logic need
/// to ask — the <c>ai key set</c> verb and the forge-credential chain — and both were sitting in the
/// Windows host for no better reason than that <c>SecretWindow</c> did. Neither is Windows-specific:
/// what is platform-specific is a password box, and that is exactly what this hides.
///
/// <b>Asynchronous, and that is forced by macOS.</b> WPF's <c>ShowDialog</c> blocks and returns an
/// answer; Avalonia has no synchronous modal at all — a window there shows and returns immediately.
/// A synchronous signature would therefore be implementable on one platform and not the other, so
/// the shared one is the honest shape and the Windows implementation simply completes at once.
///
/// <b>The secret is returned, never stored here.</b> This knows how to ask a question;
/// <see cref="ISecretStore"/> knows where secrets live. Nothing in between logs it, and the window
/// holds it only for as long as it is open.
/// </summary>
public interface ISecretPrompt
{
    /// <summary>
    /// Asks for an AI provider's API key. Null when the user declined.
    ///
    /// A window rather than a command-line argument, and that is the whole reason this exists:
    /// <c>flick ai key set &lt;key&gt;</c> would put the key in the shell's history and in the
    /// process list where any other process on the machine can read it.
    /// </summary>
    Task<string?> AskForApiKeyAsync(AiProvider provider);

    /// <summary>
    /// Asks for a token that opens pull requests on <paramref name="host"/>. Null when declined.
    ///
    /// The prompt names the service as well as the host, because what to create is different on each
    /// of the three and a user told only "a token for git.acme.io" has to guess which page of which
    /// settings screen.
    /// </summary>
    Task<string?> AskForForgeTokenAsync(ForgeKind kind, string host);
}
