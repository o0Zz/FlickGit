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
    /// Applies an edit to one remote: a rename, a re-point, or both — <b>and the rename goes
    /// first</b>.
    ///
    /// <c>set-url</c> takes the remote's name, so doing it the other way round points the <i>old</i>
    /// name at the new URL and then renames it. That works, right up until the rename fails: then it
    /// has left a remote nobody asked for pointing somewhere new, and the window reports a failure
    /// while the repository holds half an edit.
    ///
    /// <b>Here rather than in the window that has the two text boxes.</b> This is a sequence whose
    /// order is the whole of its correctness, and "the steps ran the wrong way round" is exactly the
    /// bug clicking does not reveal — both orders look identical on every attempt that succeeds.
    /// </summary>
    /// <param name="from">The remote's current name, as the last read gave it.</param>
    /// <param name="name">The name it should have. Equal to <paramref name="from"/> for no rename.</param>
    /// <param name="url">The fetch URL it should have. Equal to <paramref name="currentUrl"/> for no re-point.</param>
    public async Task<RemoteSave> SaveAsync(
        RepositoryInfo repository,
        string from,
        string name,
        string currentUrl,
        string url,
        CancellationToken cancellationToken)
    {
        bool renaming = !string.Equals(name, from, StringComparison.Ordinal);
        bool repointing = !string.Equals(url, currentUrl, StringComparison.Ordinal);

        if (renaming)
        {
            ConfigOutcome renamed = await RenameAsync(repository, from, name, cancellationToken)
                .ConfigureAwait(false);

            //Stopped here, and the re-point is not attempted. The name it would have been given is the
            //one that just failed to exist.
            if (!renamed.Succeeded)
                return RemoteSave.Failed(renamed.GitError);
        }

        if (repointing)
        {
            //The new name, because the rename above has already happened.
            ConfigOutcome pointed = await SetUrlAsync(repository, name, url, cancellationToken)
                .ConfigureAwait(false);

            if (!pointed.Succeeded)
                return RemoteSave.Failed(pointed.GitError);
        }

        return new RemoteSave(true, null, renaming, repointing);
    }

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

/// <summary>
/// What <see cref="RemoteService.SaveAsync"/> did. The two flags are there so the caller can name
/// the step in its own words without re-deriving which one ran from the text boxes it started with.
/// </summary>
/// <param name="Renamed">The rename ran and succeeded.</param>
/// <param name="Repointed">The re-point ran and succeeded.</param>
public sealed record RemoteSave(bool Succeeded, string? GitError, bool Renamed, bool Repointed)
{
    public static RemoteSave Failed(string? error) => new(false, error, false, false);
}
