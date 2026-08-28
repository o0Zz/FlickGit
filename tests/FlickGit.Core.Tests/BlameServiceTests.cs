using FlickGit.Blame;
using FlickGit.Models;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The service behind the blame window.
/// </summary>
public class BlameServiceTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\dev\d360-portal", "d360-portal", HasSubmodules: false, IsBare: false, GitDirectory: @"C:\dev\d360-portal\.git");

    private static string Stream(string sha, string text) => string.Join('\n',
        $"{sha} 1 1 1",
        "author o0Zz",
        "author-time 1787430202",
        "author-tz +0200",
        "summary a commit",
        "filename src/Gateway.cs",
        "\t" + text);

    /// <summary>
    /// In scope under the safety rules, which this covers on two counts: "every read carries
    /// <c>--no-optional-locks</c>", and the blame window's own promise that it performs nothing.
    /// The mechanical form of the second is that no invocation from this surface goes through
    /// <c>RunAsync</c> — which is what catches somebody later hanging a checkout or a revert off the
    /// blame window.
    /// </summary>
    [Fact]
    public async Task BlameOnlyEverReads()
    {
        var git = new FakeGitRunner().Returns(["blame"], Stream("a91030d413df21c94931a3eebc2c748f7c4bcd2b", "x"));

        var service = new BlameService(git);

        await service.BlameAsync(Repository, "src/Gateway.cs", null, CancellationToken.None);
        await service.BlameAsync(Repository, "src/Gateway.cs", "a91030d", CancellationToken.None);

        Assert.NotEmpty(git.Invocations);
        Assert.All(git.Invocations, i => Assert.True(i.ReadOnly));
    }

    /// <summary>
    /// In scope under the safety rules: the path is passed after <c>--</c> so a file whose name
    /// looks like a revision is still read as a file, and the revision is passed as its own argument
    /// rather than spliced into the path.
    /// </summary>
    [Fact]
    public async Task TheRequestNamesTheRevisionAndSeparatesThePath()
    {
        var git = new FakeGitRunner().Returns(["blame"], string.Empty);

        await new BlameService(git).BlameAsync(Repository, "src/Gateway.cs", "HEAD~3", CancellationToken.None);

        string[] args = Assert.Single(git.Invocations).Args;

        Assert.Equal(["blame", "--porcelain", "HEAD~3", "--", "src/Gateway.cs"], args);
    }

    /// <summary>
    /// In scope under the safety rules — the guard rather than the parse. Git does not refuse to
    /// blame a binary file, it blames it into nonsense, so the refusal has to be ours.
    /// </summary>
    [Fact]
    public async Task BinaryContentIsRefusedRatherThanShown()
    {
        var git = new FakeGitRunner()
            .Returns(["blame"], Stream("a91030d413df21c94931a3eebc2c748f7c4bcd2b", "PNG\0\u0001\u0002"));

        BlameOutcome outcome = await new BlameService(git)
            .BlameAsync(Repository, "logo.png", null, CancellationToken.None);

        Assert.True(outcome.IsBinary);
        Assert.False(outcome.Succeeded);
        Assert.Empty(outcome.Lines);
    }

    /// <summary>
    /// In scope under the safety rules: a refusal reports Git's own words, per CLAUDE.md's Error
    /// Handling rule that a Git error is never swallowed or replaced with a generic message.
    /// </summary>
    [Fact]
    public async Task GitsOwnMessageSurvivesAFailure()
    {
        var git = new FakeGitRunner()
            .Returns(["blame"], string.Empty, exitCode: 128, stderr: "fatal: no such path 'x.cs' in HEAD");

        BlameOutcome outcome = await new BlameService(git)
            .BlameAsync(Repository, "x.cs", null, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Contains("no such path", outcome.Error);
    }
}
