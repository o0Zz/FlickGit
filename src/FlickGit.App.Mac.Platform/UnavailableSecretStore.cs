using FlickGit.App.Settings;
using FlickGit.Logging;

namespace FlickGit.App.Mac;

/// <summary>
/// <see cref="ISecretStore"/> until Keychain is written.
///
/// Answers "nothing stored" to every read and "could not" to every write, which is the shape the
/// interface already has for a keystore that is locked or refusing — so every caller handles it
/// today. The visible consequence is that <c>AiConfiguration.HasKey</c> is false and the AI is
/// unavailable, exactly as it would be on a Mac where the user has not stored a key yet. That is
/// what lets the commit window be built and exercised before the interop exists.
///
/// <b>It logs a write.</b> A silent false would look like the keystore had refused, and the reason
/// it refused is worth having in the log while this is the state of things.
/// </summary>
public sealed class UnavailableSecretStore(ILog log) : ISecretStore
{
    public bool Has(string target) => false;

    public string? Read(string target) => null;

    public bool Write(string target, string secret)
    {
        log.Warn($"Cannot store {target}: the macOS Keychain store is not implemented yet.");

        return false;
    }

    /// <summary>True: there is reliably nothing there, which is what the caller asked for.</summary>
    public bool Clear(string target) => true;
}
