using FlickGit.Ai;

namespace FlickGit.App.Settings;

/// <summary>
/// Where a secret lives, which is never a file FlickGit writes.
///
/// CLAUDE.md is unconditional about it: API keys go to the operating system's own keystore —
/// Credential Manager on Windows, Keychain on macOS — and into no file of ours, so a settings file
/// somebody pastes into a bug report cannot carry one.
///
/// <b>Every method answers rather than throws.</b> A keystore is a system service and it can be
/// locked, absent or refused; the caller's response to all of those is the same, which is to say the
/// key is not available and offer to store one. <see cref="Read"/> returning null and
/// <see cref="Write"/> returning false are that answer, and the implementation logs the reason so
/// the sentence the user sees does not have to carry it.
/// </summary>
public interface ISecretStore
{
    /// <summary>Whether a secret is stored, without reading it out.</summary>
    bool Has(string target);

    /// <summary>The secret, or null when there is none or it could not be read.</summary>
    string? Read(string target);

    /// <summary>Stores or replaces the secret. False when the keystore refused.</summary>
    bool Write(string target, string secret);

    /// <summary>Removes the secret. True when it is gone, including when it was never there.</summary>
    bool Clear(string target);
}

/// <summary>
/// The names secrets are filed under, in one place so the two hosts cannot disagree.
///
/// A pure function of its arguments, which is one of the three kinds of static Hard Requirement 3
/// keeps. It moved out of the Windows <c>CredentialStore</c> because the *name* is not a Windows
/// fact — a key stored on one platform is looked up by the same string on the other, and two copies
/// of this formatting would be a key that silently cannot be found.
/// </summary>
public static class SecretTargets
{
    /// <summary>
    /// One target per AI provider, so switching provider does not throw away the other key.
    ///
    /// The <c>FlickGit</c> prefix is what a user searching Credential Manager or Keychain Access for
    /// this tool will find, which is the whole reason to have a naming convention at all.
    /// </summary>
    public static string AiTarget(AiProvider provider) => $"FlickGit:{provider.ToString().ToLowerInvariant()}";

    /// <summary>
    /// One target per forge <b>host</b>, not per service and not per repository.
    ///
    /// Per host because that is what a credential is actually scoped to: one token opens pull
    /// requests on every repository on <c>github.com</c>, and a company with both
    /// <c>dev.azure.com</c> and an internal GitLab needs two. Per repository would ask the same
    /// question once per clone; per service would break the moment a second instance appeared.
    ///
    /// Lower-cased, because a host name is case-insensitive and a keystore's target is not —
    /// <c>GitHub.com</c> typed into a remote once would otherwise file a second, invisible token.
    /// </summary>
    public static string ForgeTarget(string host) => $"FlickGit:forge:{host.ToLowerInvariant()}";
}
