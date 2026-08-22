using FlickGit.Models;
using FlickGit.Tags;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Tags — Hard Requirement 4's third bullet, the safety rules.
///
/// Every test here is one of those rules rather than coverage of the service: the order a deletion
/// happens in, the ref it names, that no argument list can force anything, that a name is refused
/// before Git is asked to do anything with it, and that the listing read carries
/// <c>--no-optional-locks</c>. Listing, creating and the remote resolution are exercised by running
/// the product.
/// </summary>
public class TagServiceTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\dev\repo", "repo", HasSubmodules: false, IsBare: false);

    /// <summary>
    /// A deletion that cannot reach the remote deletes nothing at all.
    ///
    /// This is the order-that-matters rule, and it is why the sequence lives in Core. Local first
    /// would leave the tag published and no longer visible from this machine — the user can no longer
    /// see the thing they still have to delete. CLAUDE.md: "when an operation fails midway, preserve
    /// repository state."
    /// </summary>
    [Fact]
    public async Task A_failed_remote_deletion_leaves_the_local_tag_alone()
    {
        var git = new FakeGitRunner()
            .Returns(["push"], exitCode: 1, stderr: "fatal: unable to access 'https://example.invalid/'")
            .Returns(["tag", "-d"]);

        TagOutcome outcome = await new TagService(git)
            .DeleteAsync(Repository, "v1.4.0", "origin", CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("unable to access", outcome.GitError);

        //The local delete must not have been attempted, not merely have been undone.
        Assert.True(git.NeverCalledWith("-d"));
    }

    /// <summary>
    /// A remote deletion names <c>refs/tags/&lt;name&gt;</c>, never the bare name.
    ///
    /// <c>git push origin --delete v1.0</c> is ambiguous when a <i>branch</i> called v1.0 also
    /// exists, and the fully qualified ref is what makes it impossible for the deletion to land on
    /// the wrong one.
    /// </summary>
    [Fact]
    public async Task Remote_deletion_names_the_fully_qualified_ref()
    {
        var git = new FakeGitRunner().Returns(["push"]).Returns(["tag", "-d"]);

        await new TagService(git).DeleteAsync(Repository, "v1.0", "origin", CancellationToken.None);

        string[] push = git.Invocations.First(i => i.Args.Contains("push")).Args;

        Assert.Equal(["push", "origin", "--delete", "refs/tags/v1.0"], push);
    }

    /// <summary>Publishing one tag qualifies its ref for the same reason.</summary>
    [Fact]
    public async Task Publishing_one_tag_names_the_fully_qualified_ref()
    {
        var git = new FakeGitRunner().Returns(["push"]);

        await new TagService(git).PushAsync(Repository, "v2.0", "origin", CancellationToken.None);

        Assert.Equal(["push", "origin", "refs/tags/v2.0"], git.Invocations[0].Args);
    }

    /// <summary>
    /// No argument list the service builds can force anything.
    ///
    /// CLAUDE.md's "No argument list ever contains ..." rule, for the two spellings that would let a
    /// tag be moved onto a different commit — which is how two people end up with different code
    /// under one version number.
    /// </summary>
    [Fact]
    public async Task Nothing_is_ever_forced()
    {
        var git = new FakeGitRunner()
            .Returns(["check-ref-format"])
            .Returns(["tag"])
            .Returns(["push"]);

        var tags = new TagService(git);

        await tags.CreateAsync(Repository, "v1.0", "release", null, CancellationToken.None);
        await tags.PushAsync(Repository, "v1.0", "origin", CancellationToken.None);
        await tags.DeleteAsync(Repository, "v1.0", "origin", CancellationToken.None);

        foreach (string forbidden in new[] { "--force", "-f", "--force-with-lease" })
            Assert.True(git.NeverCalledWith(forbidden), forbidden);
    }

    /// <summary>
    /// An invalid name is refused before any Git command runs.
    ///
    /// The same rule <c>CommitFlow</c> follows for a branch name: the cheap pattern answers first, so
    /// nothing is created and no process is started on a name Git was always going to reject.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("-v1.0")]
    [InlineData("v1..0")]
    [InlineData("v 1.0")]
    [InlineData("v1.0/")]
    [InlineData("v1.0.lock")]
    public async Task An_invalid_name_creates_nothing(string name)
    {
        var git = new FakeGitRunner();

        TagOutcome outcome = await new TagService(git)
            .CreateAsync(Repository, name, null, null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Empty(git.Invocations);
    }

    /// <summary>
    /// Listing is a read, so it goes through <c>ReadAsync</c> and carries
    /// <c>--no-optional-locks</c>.
    ///
    /// The palette and the tag window both scan repositories in the background while an IDE is doing
    /// the same, which is where CLAUDE.md's <c>index.lock</c> contention comes from.
    /// </summary>
    [Fact]
    public async Task Listing_is_a_read()
    {
        var git = new FakeGitRunner().Returns(["for-each-ref"], stdout: string.Empty);

        await new TagService(git).ListAsync(Repository, CancellationToken.None);

        Assert.True(git.Invocations[0].ReadOnly);

        //for-each-ref rather than `git tag --list`: CLAUDE.md forbids parsing porcelain output.
        Assert.Contains("for-each-ref", git.Invocations[0].Args);
        Assert.True(git.NeverCalledWith("--list"));
    }
}
