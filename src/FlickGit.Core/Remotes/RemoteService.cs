using FlickGit.Config;
using FlickGit.Git;
using FlickGit.Models;
using FlickGit.Repositories;

namespace FlickGit.Remotes;

/// <summary>
/// Adding, renaming, re-pointing and removing a remote.
///
/// <b>Writes only.</b> Listing lives in <see cref="RepositoryConfigService.ReadAsync"/>, which
/// already has every remote out of the one config read the window makes — a `ListAsync` here would
/// be a second answer to a question that is already answered.
///
/// <b>Nothing here touches the network.</b> No <c>fetch</c>, no <c>ls-remote</c>, and no attempt to
/// verify that a URL resolves: every one of these commands is a local config edit, and a window that
/// took a round trip before it would let the user press a button is a window nobody uses. Whether
/// the URL is right is answered by the next push, in Git's own words.
/// </summary>
public sealed class RemoteService(IGitProcessRunner git, RepositoryService repositories)
{
    public Task<ConfigOutcome> AddAsync(
        RepositoryInfo repository,
        string name,
        string url,
        CancellationToken cancellationToken) =>
        RunAsync(repository, ["remote", "add", name.Trim(), url.Trim()], cancellationToken);

    public Task<ConfigOutcome> SetUrlAsync(
        RepositoryInfo repository,
        string name,
        string url,
        CancellationToken cancellationToken) =>
        RunAsync(repository, ["remote", "set-url", name.Trim(), url.Trim()], cancellationToken);

    public Task<ConfigOutcome> RenameAsync(
        RepositoryInfo repository,
        string from,
        string to,
        CancellationToken cancellationToken) =>
        RunAsync(repository, ["remote", "rename", from.Trim(), to.Trim()], cancellationToken);

    /// <summary>
    /// Removes a remote, its remote-tracking branches and any branch upstream pointing at it.
    ///
    /// That is more than the row it deletes, which is why the caller confirms first: nothing in the
    /// working tree is touched and no commit is lost, but a branch that tracked this remote comes
    /// back with no upstream and the next push asks where to send it.
    /// </summary>
    public Task<ConfigOutcome> RemoveAsync(
        RepositoryInfo repository,
        string name,
        CancellationToken cancellationToken) =>
        RunAsync(repository, ["remote", "remove", name.Trim()], cancellationToken);

    private async Task<ConfigOutcome> RunAsync(
        RepositoryInfo repository,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        GitResult result = await git.RunAsync(repository.Root, args, cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
            return ConfigOutcome.Failed(result.ErrorText);

        //Every write path in the product does this. A cached RepositoryStatus still carries the
        //upstream this remote used to provide, and the push guardrails read it.
        repositories.Invalidate(repository.Root);
        return ConfigOutcome.Ok;
    }
}
