using FlickGit.Git;
using FlickGit.Logging;
using FlickGit.Models;

namespace FlickGit.Forges;

/// <summary>
/// Asks Git's own credential helper for the token it already holds for a host.
///
/// This is what makes the common case need no setup at all. A developer who can <c>git push</c> to
/// github.com has Git Credential Manager holding an OAuth token for it, and that token is exactly
/// what the REST API wants — so the first pull request FlickGit opens costs no visit to a settings
/// page and no personal access token pasted from anywhere.
///
/// It follows the rule <c>Clone</c> already sets: <b>do not implement credential handling.</b>
/// Nothing here prompts, stores, refreshes or knows what kind of token came back. It runs one Git
/// command and reads one field.
///
/// <b>Non-interactive, deliberately.</b> <c>credential.interactive=false</c> means a helper with
/// nothing stored answers with nothing rather than opening a sign-in window — so a click on Create
/// either finds a credential immediately or falls through to FlickGit's own prompt, and never
/// silently turns into somebody else's browser tab. <c>GIT_TERMINAL_PROMPT=0</c> is already set by
/// the process runner, which closes the other way this could block.
/// </summary>
public sealed class GitCredentialFill(IGitProcessRunner git, ILog log)
{
    /// <summary>
    /// The stored secret for <paramref name="url"/>, or null when the helper has none.
    ///
    /// <paramref name="url"/> is the API base rather than the remote: they are the same host, which
    /// is all the helper keys on, and passing the one already resolved avoids a second opinion about
    /// what the host is.
    /// </summary>
    public async Task<string?> ReadAsync(
        RepositoryInfo repository,
        Uri url,
        CancellationToken cancellationToken)
    {
        //The helper protocol: key=value lines, then a blank one. Written rather than passed as
        //arguments because that is the interface `credential fill` has -- and because a URL on a
        //command line is a URL in a process list.
        string request = $"protocol={url.Scheme}\nhost={url.Host}\n\n";

        GitResult result = await git.RunWithInputAsync(
            repository.Root,
            ["-c", "credential.interactive=false", "credential", "fill"],
            request,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            //Ordinary on a machine with no helper configured, so this is a debug line and not a
            //warning. The caller's next move is to ask the user, which is a better outcome than a
            //notification about a credential helper they may not have.
            log.Debug($"git credential fill found nothing for {url.Host}.");
            return null;
        }

        return Password(result.StdOut);
    }

    /// <summary>
    /// The <c>password</c> line out of the helper's answer.
    ///
    /// Split at the <b>first</b> <c>=</c>, because a token can contain one — several forges issue
    /// base64 that ends in padding. Splitting on every <c>=</c> would hand back a truncated
    /// credential, which fails as a 401 that looks exactly like an expired one.
    ///
    /// The <c>username</c> line is read by nothing. All three APIs here authenticate with the secret
    /// alone: two as a Bearer token, and Azure DevOps as Basic with an empty user name.
    /// </summary>
    private static string? Password(string standardOutput)
    {
        foreach (string line in standardOutput.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            int separator = trimmed.IndexOf('=');

            if (separator > 0 && trimmed[..separator] == "password" && separator + 1 < trimmed.Length)
                return trimmed[(separator + 1)..];
        }

        return null;
    }
}
