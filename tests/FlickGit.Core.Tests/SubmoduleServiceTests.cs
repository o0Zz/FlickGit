using FlickGit.Models;
using FlickGit.Repositories;
using FlickGit.Submodules;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Submodules: the <c>.gitmodules</c> parser, and the refusals.
///
/// In scope on two of Hard Requirement 4's five bullets. The parser belongs with the other four --
/// a submodule's name defaults to its path, so a path with a dot in it is a subsection with a dot
/// in it, and cutting the key at the wrong separator loses the row. The rest belong with the safety
/// rules: <c>-f</c> is never reached by this service deciding to reach it, a refusal is reported
/// rather than escalated, and a path that leaves the repository is refused before Git is asked.
/// </summary>
public class SubmoduleServiceTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\dev\repo", "repo", HasSubmodules: true, IsBare: false);

    private static SubmoduleService Create(FakeGitRunner git) => new(git, new RepositoryService(git));

    private static string Stream(params string[] records) =>
        string.Concat(records.Select(record => record + '\0'));

    /// <summary>
    /// The parser. Three traps in one fixture: a name that is not the path, a name containing a dot,
    /// and a declaration with keys we do not read.
    /// </summary>
    [Fact]
    public void Gitmodules_yields_a_path_and_a_url_per_submodule()
    {
        //As `git config -f .gitmodules --list -z` reports it: section and final component lower-cased,
        //the subsection verbatim.
        IReadOnlyList<DeclaredSubmodule> modules = SubmoduleService.ParseModules(Stream(
            "submodule.libs/protocol.path\nlibs/protocol",
            "submodule.libs/protocol.url\ngit@github.com:acme/protocol.git",

            //A name with a dot of its own. Split on '.' this becomes "libs/proto" and the row is lost.
            "submodule.libs/proto.v2.path\nlibs/proto.v2",
            "submodule.libs/proto.v2.url\nhttps://example.com/proto.git",

            //Keys that are Git's business, not ours -- and a capital that must survive.
            "submodule.Vendor.path\nvendor/spdlog",
            "submodule.Vendor.url\nhttps://github.com/gabime/spdlog.git",
            "submodule.Vendor.branch\nv1.x",
            "submodule.Vendor.shallow\ntrue"));

        Assert.Equal(3, modules.Count);

        //Declaration order, so adding one does not reshuffle the rows above it.
        Assert.Equal("libs/protocol", modules[0].Path);
        Assert.Equal("git@github.com:acme/protocol.git", modules[0].Url);

        Assert.Equal("libs/proto.v2", modules[1].Name);
        Assert.Equal("libs/proto.v2", modules[1].Path);

        Assert.Equal("Vendor", modules[2].Name);
        Assert.Equal("vendor/spdlog", modules[2].Path);
        Assert.Equal("https://github.com/gabime/spdlog.git", modules[2].Url);
    }

    /// <summary>
    /// Removing runs <c>deinit</c> and then <c>rm</c>, and neither is forced. The order is the point:
    /// <c>rm</c> first would empty the checkout that <c>deinit</c>'s refusal is about.
    /// </summary>
    [Fact]
    public async Task Remove_deinits_then_removes_and_forces_nothing()
    {
        var git = new FakeGitRunner()
            .Returns(["submodule", "deinit"])
            .Returns(["rm"]);

        SubmoduleOutcome outcome = await Create(git)
            .RemoveAsync(Repository, "libs/protocol", force: false, CancellationToken.None);

        Assert.True(outcome.Succeeded);

        Assert.Equal(["submodule", "deinit", "--", "libs/protocol"], git.Invocations[0].Args);
        Assert.Equal(["rm", "--", "libs/protocol"], git.Invocations[1].Args);

        foreach (string forbidden in new[] { "-f", "--force" })
            Assert.True(git.NeverCalledWith(forbidden), forbidden);
    }

    /// <summary>
    /// Git refused because the submodule holds uncommitted work. The service reports it and stops --
    /// forcing is the caller's second question, never this service's own decision.
    /// </summary>
    [Fact]
    public async Task Remove_reports_local_changes_rather_than_escalating()
    {
        var git = new FakeGitRunner()
            .Returns(
                ["submodule", "deinit"],
                exitCode: 1,
                stderr: "error: the following file has local modifications:\n    notes.md\n"
                        + "(use --cached to keep the file, or -f to force removal)")
            .Returns(["rm"]);

        SubmoduleOutcome outcome = await Create(git)
            .RemoveAsync(Repository, "libs/protocol", force: false, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.True(outcome.HasLocalChanges);

        //Nothing was removed, and nothing was forced on the way to saying so.
        Assert.True(git.NeverCalledWith("rm"));
        Assert.True(git.NeverCalledWith("-f"));
    }

    /// <summary>
    /// Only an answer to the second question reaches the forced spelling, and it reaches both
    /// commands -- <c>deinit -f</c> leaves a checkout <c>rm</c> would still refuse.
    /// </summary>
    [Fact]
    public async Task Remove_forces_both_commands_when_force_was_asked_for()
    {
        var git = new FakeGitRunner()
            .Returns(["submodule", "deinit"])
            .Returns(["rm"]);

        await Create(git).RemoveAsync(Repository, "libs/protocol", force: true, CancellationToken.None);

        Assert.Equal(["submodule", "deinit", "-f", "--", "libs/protocol"], git.Invocations[0].Args);
        Assert.Equal(["rm", "-f", "--", "libs/protocol"], git.Invocations[1].Args);
    }

    /// <summary>
    /// A target that is not inside the repository is refused before any process starts. Absolute and
    /// climbing-out are one test because they are one guard.
    /// </summary>
    [Theory]
    [InlineData(@"C:\somewhere\else")]
    [InlineData(@"..\outside")]
    [InlineData("../outside")]
    [InlineData("libs/../../outside")]
    public async Task Add_refuses_a_path_outside_the_repository(string path)
    {
        var git = new FakeGitRunner();

        SubmoduleOutcome outcome = await Create(git)
            .AddAsync(Repository, "https://example.com/r.git", path, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(SubmoduleRefusal.OutsideRepository, outcome.Refusal);

        //The whole point of refusing here rather than letting Git do it.
        Assert.Empty(git.Invocations);
    }

    /// <summary>An empty URL is refused the same way, and for the same reason.</summary>
    [Fact]
    public async Task Add_refuses_an_empty_url()
    {
        var git = new FakeGitRunner();

        SubmoduleOutcome outcome = await Create(git)
            .AddAsync(Repository, "   ", "libs/protocol", CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(SubmoduleRefusal.NoUrl, outcome.Refusal);
        Assert.Empty(git.Invocations);
    }

    /// <summary>
    /// The listing reads and never writes, and it asks <c>.gitmodules</c> rather than
    /// <c>git submodule status</c> -- which has no porcelain form, so parsing it would be reading
    /// output shaped for a terminal.
    /// </summary>
    [Fact]
    public async Task Listing_reads_gitmodules_and_never_asks_submodule_status()
    {
        var git = new FakeGitRunner()
            .Returns(
                ["config", "-f", ".gitmodules"],
                Stream("submodule.libs/protocol.path\nlibs/protocol",
                       "submodule.libs/protocol.url\nhttps://example.com/protocol.git"))
            .Returns(["diff", "HEAD"], "libs/protocol\0");

        IReadOnlyList<GitSubmodule> modules = await Create(git).ListAsync(Repository, CancellationToken.None);

        GitSubmodule module = Assert.Single(modules);
        Assert.Equal("libs/protocol", module.Path);
        Assert.True(module.HasChanges);

        Assert.True(git.NeverCalledWith("status"));
        Assert.All(git.Invocations, invocation => Assert.True(invocation.ReadOnly));
    }
}
