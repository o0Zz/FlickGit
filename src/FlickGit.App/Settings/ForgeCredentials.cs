using System.Windows;
using FlickGit.App.Views;
using FlickGit.Forges;
using FlickGit.Logging;
using FlickGit.Models;

namespace FlickGit.App.Settings;

/// <summary>
/// Finds a token that opens pull requests on a host, in the order that asks the user the fewest
/// questions.
///
/// <list type="number">
/// <item><description><b>A token FlickGit already stored</b> for this host. First, not last: the
/// only reason one exists is that the user stored it, and they did that because what came out of the
/// credential helper did not work. Trying the helper again ahead of it would re-break the thing they
/// fixed.</description></item>
/// <item><description><b>Git's own credential helper.</b> This is the case that needs no setup at
/// all — a developer who can <c>git push</c> to github.com has Git Credential Manager holding a
/// token for it, and that token is what the REST API wants. Non-interactive, so a helper with
/// nothing stored answers with nothing rather than opening a sign-in window.</description></item>
/// <item><description><b>Ask, once, and store it.</b> A prompt naming the service and the host, and
/// the answer goes into Credential Manager under this host — where the user can see and delete it in
/// Windows' own UI, and where step one will find it next time.</description></item>
/// </list>
///
/// Nothing here refreshes, validates or inspects a token. That is the rule <c>Clone</c> already sets
/// about credentials, and the service is the only thing that can actually answer whether one works —
/// which is why <c>PullRequestFlow</c> asks again with <c>forcePrompt</c> when it gets a 401 rather
/// than anything here trying to be clever in advance.
/// </summary>
public sealed class ForgeCredentials(CredentialStore store, GitCredentialFill helper, ILog log)
{
    /// <summary>
    /// A token for <paramref name="forge"/>, or null when the user declined to supply one.
    /// </summary>
    /// <param name="forcePrompt">
    /// Skip both stored sources and ask. Passed after a service has refused a credential: whatever
    /// is on the machine has just been shown not to work, so offering it again would loop.
    /// </param>
    /// <param name="owner">
    /// The window the question belongs to, so the prompt cannot open behind it. Null from a surface with
    /// no window. This class already reaches for SecretWindow, so carrying the owner makes an existing
    /// dependency honest rather than adding one.
    /// </param>
    public async Task<string?> AcquireAsync(
        RepositoryInfo repository,
        ForgeRepository forge,
        bool forcePrompt,
        Window? owner,
        CancellationToken cancellationToken)
    {
        if (!forcePrompt
            && await FindAsync(repository, forge, cancellationToken).ConfigureAwait(true) is { Length: > 0 } found)
        {
            return found;
        }

        //A window, not a console prompt and never a command-line argument -- the same argument
        //SecretWindow carries for the AI key, and it applies unchanged to a forge token.
        if (SecretWindow.AskForForgeToken(owner, forge.Kind, forge.Host) is not { Length: > 0 } typed)
            return null;

        //Stored even if it turns out not to work. The alternative is validating it with a request of
        //our own before saving, which is a second round trip to learn what the create is about to
        //say anyway -- and a rejected token is cleared by the next prompt overwriting it.
        store.Write(CredentialStore.ForgeTarget(forge.Host), typed);

        return typed;
    }

    /// <summary>
    /// A token that is already on the machine, or null. <b>Never asks.</b>
    ///
    /// Its own method because one caller must not be allowed to prompt: the window checks for an
    /// already-open request as it opens, and demanding a credential for a check the user did not ask
    /// for would be the wrong first thing this feature ever did.
    /// </summary>
    public async Task<string?> FindAsync(
        RepositoryInfo repository,
        ForgeRepository forge,
        CancellationToken cancellationToken)
    {
        if (store.Read(CredentialStore.ForgeTarget(forge.Host)) is { Length: > 0 } stored)
            return stored;

        if (await helper.ReadAsync(repository, forge.ApiBase, cancellationToken).ConfigureAwait(true)
            is { Length: > 0 } fromGit)
        {
            log.Debug($"Using the credential Git already holds for {forge.Host}.");
            return fromGit;
        }

        return null;
    }
}
