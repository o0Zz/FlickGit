using FlickGit.Branches;
using FlickGit.Config;
using FlickGit.Models;
using FlickGit.Remotes;
using FlickGit.Repositories;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The <c>config --local --list -z</c> parser, and the arguments the repository window sends.
///
/// In scope under Hard Requirement 4 as <b>a parser and the pure functions beside it</b>: this is
/// the fourth machine-readable Git format the product reads, and a wrong split here shows the user
/// somebody else's identity or a remote pointing somewhere it does not. The <c>--unset</c> exit code
/// is here for the same reason the porcelain traps are: it is the one place where a non-zero exit is
/// the expected answer.
/// </summary>
public class RepositoryConfigTests
{
    private static readonly RepositoryInfo Repo =
        new(@"C:\dev\my repo", "my repo", HasSubmodules: false, IsBare: false, GitDirectory: @"C:\dev\my repo\.git");

    /// <summary>Records are NUL-terminated; the key ends at the first newline.</summary>
    private static string Stream(params string[] records) =>
        string.Concat(records.Select(record => record + '\0'));

    [Fact]
    public void A_record_splits_at_the_first_newline()
    {
        IReadOnlyList<ConfigEntry> entries = GitConfigList.ParseList(
            Stream("user.name\nThierry Quemerais", "user.email\nt.q@example.com"));

        Assert.Equal(2, entries.Count);
        Assert.Equal("user.name", entries[0].Key);
        Assert.Equal("Thierry Quemerais", entries[0].Value);
        Assert.Equal("t.q@example.com", entries[1].Value);
    }

    /// <summary>
    /// A value may contain newlines — <c>alias.lg</c> routinely does. Splitting on every newline
    /// rather than the first turns the tail of one value into a key of its own.
    /// </summary>
    [Fact]
    public void A_value_keeps_its_own_newlines()
    {
        IReadOnlyList<ConfigEntry> entries = GitConfigList.ParseList(
            Stream("alias.lg\nlog --oneline \\\n  --graph", "user.name\nThierry"));

        Assert.Equal("alias.lg", entries[0].Key);
        Assert.Equal("log --oneline \\\n  --graph", entries[0].Value);
        Assert.Equal("user.name", entries[1].Key);
    }

    /// <summary>
    /// A key on its own line with no <c>=</c> arrives with no newline and no value, and Git reads
    /// that as true. A parser that required a newline would drop the entry entirely.
    /// </summary>
    [Fact]
    public void A_key_with_no_value_is_read_as_true()
    {
        IReadOnlyList<ConfigEntry> entries = GitConfigList.ParseList(
            Stream("flickgit.allowupstreamcreation"));

        Assert.Null(entries[0].Value);
    }

    /// <summary>
    /// <c>--list</c> lower-cases the section and the final component and leaves the subsection
    /// alone. So a remote's capitals survive and must be used verbatim, while our own key has to be
    /// matched case-insensitively — spelled <c>flickgit.primaryBranch</c> and read back flat.
    /// </summary>
    [Fact]
    public void A_remote_keeps_its_case()
    {
        IReadOnlyList<ConfigEntry> entries = GitConfigList.ParseList(Stream(
            "remote.MyFork.url\ngit@github.com:o0Zz/FlickGit.git",
            "flickgit.primarybranch\ndevelop"));

        IReadOnlyList<GitRemote> remotes = RepositoryConfigService.RemotesFrom(entries);

        Assert.Equal("MyFork", Assert.Single(remotes).Name);
    }

    /// <summary>
    /// A remote name may itself contain dots, so the name is everything between the first separator
    /// and the last — not the second field.
    /// </summary>
    [Fact]
    public void A_dotted_remote_name_survives()
    {
        IReadOnlyList<GitRemote> remotes = RepositoryConfigService.RemotesFrom(
            GitConfigList.ParseList(Stream("remote.my.fork.url\nhttps://example.com/r.git")));

        Assert.Equal("my.fork", Assert.Single(remotes).Name);
    }

    /// <summary>
    /// <c>remote.&lt;name&gt;.fetch</c> is a refspec, not a URL, and there is one on every remote.
    /// Treating it as one would show the user a row whose "URL" is <c>+refs/heads/*:...</c>.
    /// </summary>
    [Fact]
    public void Only_urls_become_remotes_and_a_matching_pushurl_is_dropped()
    {
        IReadOnlyList<GitRemote> remotes = RepositoryConfigService.RemotesFrom(
            GitConfigList.ParseList(Stream(
                "remote.origin.url\nhttps://example.com/r.git",
                "remote.origin.fetch\n+refs/heads/*:refs/remotes/origin/*",
                "remote.origin.pushurl\nhttps://example.com/r.git",
                "remote.fork.url\nhttps://example.com/f.git",
                "remote.fork.pushurl\nssh://example.com/f.git")));

        //origin first, whatever order the file was in.
        Assert.Equal(new[] { "origin", "fork" }, remotes.Select(remote => remote.Name).ToArray());
        Assert.Null(remotes[0].PushUrl);
        Assert.Equal("ssh://example.com/f.git", remotes[1].PushUrl);
    }

    /// <summary>
    /// The whole window from one read, plus the two the effective identity needs and the one that
    /// names the current branch. The identity has to distinguish "set here" from "inherited", which
    /// is why <c>--local</c> is not enough on its own.
    /// </summary>
    [Fact]
    public async Task The_read_separates_a_local_identity_from_an_inherited_one()
    {
        var git = new FakeGitRunner()
            .Returns(["config", "--local", "--list"], Stream(
                "remote.origin.url\nhttps://example.com/r.git",
                "branch.main.remote\norigin",

                //Both of ours come back flattened, primaryBranch and allowUpstreamCreation
                //alike, which is the half of the case rule that decides whether they are
                //found at all.
                "flickgit.primarybranch\ndevelop",
                "flickgit.allowupstreamcreation\nfalse"))
            .Returns(["config", "--get", "user.name"], "Global Person\n")
            .Returns(["config", "--get", "user.email"], "global@example.com\n")
            .Returns(["symbolic-ref"], "main\n");

        RepositoryConfig config = await new RepositoryConfigService(git).ReadAsync(Repo, CancellationToken.None);

        Assert.Null(config.LocalName);
        Assert.False(config.HasLocalIdentity);
        Assert.Equal("Global Person", config.EffectiveName);
        Assert.Equal("main", config.CurrentBranch);
        Assert.Equal("origin", config.TrackedRemote);
        Assert.Equal("develop", config.PrimaryBranch);
        Assert.False(config.AllowUpstreamCreation);

        //Every one of the four is a read, so every one carries --no-optional-locks.
        Assert.All(git.Invocations, invocation => Assert.True(invocation.ReadOnly));
    }

    /// <summary>
    /// Exit 5 is Git's "you tried to unset an option which does not exist" — the ordinary answer for
    /// "use the global identity" on a repository that never overrode it. Reporting it would put a
    /// Git error in front of a user whose request was already satisfied.
    /// </summary>
    [Fact]
    public async Task Unsetting_a_key_that_was_never_set_succeeds()
    {
        var git = new FakeGitRunner().Returns(["config", "--local", "--unset"], exitCode: 5);

        ConfigOutcome outcome = await new RepositoryConfigService(git)
            .UnsetAsync(Repo, RepositoryConfigService.UserNameKey, CancellationToken.None);

        Assert.True(outcome.Succeeded);
    }

    /// <summary>Any other failure is still a failure, in Git's own words.</summary>
    [Fact]
    public async Task A_real_config_failure_is_reported()
    {
        var git = new FakeGitRunner()
            .Returns(["config", "--local", "--unset"], exitCode: 4, stderr: "error: could not lock config file");

        ConfigOutcome outcome = await new RepositoryConfigService(git)
            .UnsetAsync(Repo, RepositoryConfigService.UserNameKey, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("could not lock", outcome.GitError);
    }

    /// <summary>
    /// The identity and the remote edits are written with <c>--local</c> and nothing else. A missing
    /// <c>--local</c> makes <c>git config</c> write to whichever file it defaults to, and on a
    /// repository that is one thing while on a bare invocation it is the user's global file — an
    /// identity set for one repository silently applied to every one of them.
    /// </summary>
    [Fact]
    public async Task Writes_are_local_and_never_global()
    {
        var git = new FakeGitRunner().Returns(["config"]);
        var config = new RepositoryConfigService(git);

        await config.WriteAsync(Repo, RepositoryConfigService.UserEmailKey, "me@example.com", CancellationToken.None);
        await config.WriteUpstreamAnswerAsync(Repo, allowed: true, CancellationToken.None);

        Assert.All(git.Invocations, invocation => Assert.Contains("--local", invocation.Args));
        Assert.True(git.NeverCalledWith("--global"));
        Assert.True(git.NeverCalledWith("--system"));
    }

    /// <summary>
    /// A remote edit is a config edit and nothing more: no <c>fetch</c>, no <c>ls-remote</c>, and no
    /// <c>push</c>. A window that reached the network before it would let a button be pressed is a
    /// window nobody uses, and CLAUDE.md forbids one on the Explorer path outright.
    /// </summary>
    [Fact]
    public async Task Remote_edits_touch_the_network_never()
    {
        var git = new FakeGitRunner().Returns(["remote"]);
        var remotes = new RemoteService(git, new RepositoryService(git));

        await remotes.AddAsync(Repo, "fork", "https://example.com/f.git", CancellationToken.None);
        await remotes.RenameAsync(Repo, "fork", "mine", CancellationToken.None);
        await remotes.SetUrlAsync(Repo, "mine", "ssh://example.com/f.git", CancellationToken.None);
        await remotes.RemoveAsync(Repo, "mine", CancellationToken.None);

        Assert.True(git.NeverCalledWith("fetch"));
        Assert.True(git.NeverCalledWith("ls-remote"));
        Assert.True(git.NeverCalledWith("push"));

        //And no destructive spelling has crept in beside them.
        Assert.True(git.NeverCalledWith("--force"));
        Assert.True(git.NeverCalledWith("--prune"));
    }

    /// <summary>
    /// Non-ASCII bytes in a value, and a remote whose name contains a space.
    ///
    /// In scope under Hard Requirement 4's parser bullet, which asks for spaces and non-ASCII bytes in
    /// every parser. Both are ordinary here rather than exotic: <c>user.name</c> is whatever the user
    /// typed, and <c>config --list</c> lower-cases the section and the final component while leaving
    /// the <i>subsection</i> alone -- so a remote called "mon dépôt" is a key with a space, a capital
    /// and a multi-byte character in the one part of it that must survive untouched.
    /// </summary>
    [Fact]
    public void NonAsciiValuesAndASubsectionWithASpaceSurvive()
    {
        IReadOnlyList<ConfigEntry> entries = GitConfigList.ParseList(Stream(
            "user.name\nThomas Quémerais",
            "user.email\nthomas@dépôt.example",
            "remote.mon dépôt.url\nhttps://example.com/dépôt.git"));

        Assert.Equal(3, entries.Count);
        Assert.Equal("Thomas Quémerais", entries[0].Value);
        Assert.Equal("thomas@dépôt.example", entries[1].Value);

        //The subsection keeps its space, its capital and its accent; only the section and the final
        //component are lower-cased by Git itself.
        Assert.Equal("remote.mon dépôt.url", entries[2].Key);
        Assert.Equal("https://example.com/dépôt.git", entries[2].Value);
    }

    /// <summary>
    /// A rename and a re-point in one press run <b>rename first</b>.
    ///
    /// In scope under Hard Requirement 4 as <b>a sequence</b>: <c>set-url</c> takes the remote's
    /// name, so the other order points the old name at the new URL and only then renames it. Both
    /// orders look identical on every attempt that succeeds, which is why clicking cannot find this
    /// and a test has to.
    /// </summary>
    [Fact]
    public async Task A_rename_and_a_repoint_run_the_rename_first()
    {
        var git = new FakeGitRunner().Returns(["remote"]);
        var remotes = new RemoteService(git, new RepositoryService(git));

        RemoteSave saved = await remotes.SaveAsync(
            Repo,
            from: "origin",
            name: "upstream",
            currentUrl: "https://example.com/old.git",
            url: "https://example.com/new.git",
            CancellationToken.None);

        Assert.True(saved.Succeeded);
        Assert.True(saved.Renamed);
        Assert.True(saved.Repointed);

        Assert.Equal(2, git.Invocations.Count);
        Assert.Equal(["remote", "rename", "origin", "upstream"], git.Invocations[0].Args);

        //The new name, because the rename has already happened by now.
        Assert.Equal(
            ["remote", "set-url", "upstream", "https://example.com/new.git"],
            git.Invocations[1].Args);
    }

    /// <summary>
    /// A failed rename stops the sequence, so the re-point is never attempted.
    ///
    /// In scope as <b>a sequence</b>, and it is the failure that makes the order matter at all: the
    /// wrong order leaves a remote nobody asked for pointing somewhere new while the window reports
    /// that nothing worked.
    /// </summary>
    [Fact]
    public async Task A_failed_rename_does_not_repoint()
    {
        var git = new FakeGitRunner()
            .Returns(["remote"])
            .Returns(["remote", "rename"], exitCode: 128, stderr: "error: remote upstream already exists.");

        var remotes = new RemoteService(git, new RepositoryService(git));

        RemoteSave saved = await remotes.SaveAsync(
            Repo,
            from: "origin",
            name: "upstream",
            currentUrl: "https://example.com/old.git",
            url: "https://example.com/new.git",
            CancellationToken.None);

        Assert.False(saved.Succeeded);
        Assert.Contains("already exists", saved.GitError);

        //The rename, and nothing after it.
        Assert.Single(git.Invocations);
        Assert.True(git.NeverCalledWith("set-url"));
    }

    /// <summary>
    /// Neither box changed, so nothing runs at all.
    ///
    /// In scope as <b>a sequence</b>: an unchanged Save that still issued a `remote rename` to the
    /// same name would fail in Git's words for no reason the user could act on.
    /// </summary>
    [Fact]
    public async Task An_unchanged_remote_runs_nothing()
    {
        var git = new FakeGitRunner().Returns(["remote"]);
        var remotes = new RemoteService(git, new RepositoryService(git));

        RemoteSave saved = await remotes.SaveAsync(
            Repo,
            from: "origin",
            name: "origin",
            currentUrl: "https://example.com/old.git",
            url: "https://example.com/old.git",
            CancellationToken.None);

        Assert.True(saved.Succeeded);
        Assert.False(saved.Renamed);
        Assert.False(saved.Repointed);
        Assert.Empty(git.Invocations);
    }

    /// <summary>
    /// The repository's own primary branch beats the global setting, and settles it without asking
    /// Git anything else.
    ///
    /// The more specific answer winning is the whole point of having a per-repository one: a user
    /// with "main" configured globally and one repository still on "develop" would otherwise be
    /// warned about the wrong branch on every commit -- which is the friction CLAUDE.md puts on the
    /// fast path deliberately, aimed at the wrong target.
    /// </summary>
    [Fact]
    public async Task The_repository_override_beats_the_global_setting()
    {
        var git = new FakeGitRunner()
            .Returns(["config", "--local", "--get", RepositoryConfigService.PrimaryBranchKey], "develop\n");

        var branches = new BranchService(git, new RepositoryConfigService(git));

        Assert.Equal(
            "develop",
            await branches.ResolvePrimaryBranchAsync(Repo, "main", CancellationToken.None));

        //Neither guess is reached, so a repository that has answered costs one read and no ref lookup.
        Assert.True(git.NeverCalledWith("symbolic-ref"));
        Assert.True(git.NeverCalledWith("rev-parse"));
    }
}
