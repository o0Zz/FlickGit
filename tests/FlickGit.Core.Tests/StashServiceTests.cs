using FlickGit.Models;
using FlickGit.Repositories;
using FlickGit.Stashes;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The stash: its listing format, and the refusals.
///
/// In scope on two of Hard Requirement 4's bullets. The parser and the subject split belong with the
/// other machine formats -- a reflog subject is the only place a branch name and free user text
/// arrive in one field, and cutting it at the wrong separator renames the branch. The rest belong
/// with the safety rules, and one of them is the reason the feature has a service at all: a stash is
/// named by a <i>position</i>, so acting on a row without first checking the position still holds is
/// how the wrong stash gets dropped.
/// </summary>
public class StashServiceTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\dev\repo", "repo", HasSubmodules: false, IsBare: false);

    /// <summary>The row every pop and drop test below claims to have clicked.</summary>
    private static readonly GitStash First =
        new("stash@{0}", "a1b2c3d4e5", "main", "pool leak on reconnect", default);

    private static readonly string OneStash = Stream(
        "stash@{0}\u001fa1b2c3d4e5\u001f2026-08-26T14:03:00+00:00\u001fOn main: pool leak on reconnect");

    private static StashService Create(FakeGitRunner git) => new(git, new RepositoryService(git));

    /// <summary>
    /// As Git emits it: every record NUL-terminated, and a newline after that because a
    /// <c>--format</c> holding placeholders behaves as <c>tformat:</c>. So every record but the first
    /// arrives with a newline in front of it, which is the trap the parser trims.
    /// </summary>
    private static string Stream(params string[] records) =>
        string.Concat(records.Select(record => record + "\0\n"));

    /// <summary>
    /// The listing format, including the two things a stash message can contain that a lazier
    /// separator would have split on.
    /// </summary>
    [Fact]
    public void The_listing_yields_one_stash_per_record()
    {
        IReadOnlyList<GitStash> stashes = StashService.Parse(Stream(
            "stash@{0}\u001fa1b2c3d4e5\u001f2026-08-26T14:03:00+02:00\u001fOn main: pool leak\ton reconnect",
            "stash@{1}\u001ff6a7b8c9d0\u001f2026-08-24T09:40:12+02:00\u001fWIP on feature/storage-gw: 9f0e1d2 café Ünïcödé",

            //Three fields where there should be four. Dropped, so a truncated read costs the last row
            //rather than the list.
            "stash@{2}\u001fdeadbeef01\u001f2026-08-01T00:00:00+00:00"));

        Assert.Equal(2, stashes.Count);

        Assert.Equal("stash@{0}", stashes[0].Reference);
        Assert.Equal("a1b2c3d4e5", stashes[0].Sha);
        Assert.Equal("main", stashes[0].Branch);

        //A tab is ordinary text in a stash message, which is why the field separator is not one --
        //and the existing `stash list --format=%gd%x09%gs` call in SwitchService gets away with a tab
        //only because it never looks at the message as a field.
        Assert.Equal("pool leak\ton reconnect", stashes[0].Message);

        Assert.Equal(new DateTimeOffset(2026, 8, 26, 14, 3, 0, TimeSpan.FromHours(2)), stashes[0].Created);

        //The second record is the one that arrives behind tformat's newline.
        Assert.Equal("stash@{1}", stashes[1].Reference);
        Assert.Equal("feature/storage-gw", stashes[1].Branch);
        Assert.Equal("9f0e1d2 café Ünïcödé", stashes[1].Message);
    }

    /// <summary>
    /// The subject split. The first <c>": "</c> ends the branch and every later one is the user's,
    /// which is exact rather than a guess: <c>check-ref-format</c> refuses a colon in a ref name.
    /// </summary>
    [Theory]
    [InlineData("On main: pool leak", "main", "pool leak")]
    [InlineData("WIP on main: 9f0e1d2 fix the pool", "main", "9f0e1d2 fix the pool")]
    [InlineData("On feature/x: fix: the thing", "feature/x", "fix: the thing")]
    [InlineData("On (no branch): work made detached", "(no branch)", "work made detached")]
    [InlineData("a subject in neither shape", "", "a subject in neither shape")]
    public void The_reflog_subject_splits_at_the_first_colon(string subject, string branch, string message)
    {
        (string actualBranch, string actualMessage) = StashService.ParseSubject(subject);

        Assert.Equal(branch, actualBranch);
        Assert.Equal(message, actualMessage);
    }

    /// <summary>
    /// A stash whose reference no longer names it is not dropped, and nothing is asked of Git.
    ///
    /// <b>THE rule.</b> A reflog selector is a position, so pushing one stash renumbers every row
    /// below it -- and a terminal, an IDE or FlickGit's own stash-switch-restore can do that while
    /// this window sits open. Dropping the stale reference would throw away work the user never
    /// pointed at, with nothing in the product able to find it again.
    /// </summary>
    [Fact]
    public async Task A_stash_that_moved_is_not_dropped()
    {
        //Something newer went on top, so what the user clicked as stash@{1} is now stash@{2}.
        var git = new FakeGitRunner()
            .Returns(["stash", "list"], Stream(
                "stash@{0}\u001f1111111111\u001f2026-08-26T15:00:00+00:00\u001fOn main: pushed since",
                "stash@{1}\u001fa1b2c3d4e5\u001f2026-08-26T14:03:00+00:00\u001fOn main: pool leak"))
            .Returns(["stash", "drop"]);

        var clicked = new GitStash("stash@{1}", "9999999999", "main", "the row that was drawn", default);

        StashOutcome outcome = await Create(git).DropAsync(Repository, clicked, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(StashRefusal.Moved, outcome.Refusal);

        //Not attempted, rather than attempted and failed. The verification read is the only call.
        Assert.Single(git.Invocations);
        Assert.True(git.NeverCalledWith("drop"));
    }

    /// <summary>
    /// A pop is held to the same check, because popping the wrong stash is a merge nobody asked for.
    /// </summary>
    [Fact]
    public async Task A_stash_that_moved_is_not_popped()
    {
        var git = new FakeGitRunner()
            .Returns(["stash", "list"], stdout: string.Empty)
            .Returns(["stash", "pop"]);

        StashOutcome outcome = await Create(git).PopAsync(Repository, First, CancellationToken.None);

        Assert.Equal(StashRefusal.Moved, outcome.Refusal);
        Assert.True(git.NeverCalledWith("pop"));
    }

    /// <summary>
    /// A push that stashed nothing reports that, rather than reporting success.
    ///
    /// <c>git stash push</c> on a clean working tree exits 0 having done nothing, and the only thing
    /// it offers on the way out is the sentence "No local changes to save" -- which CLAUDE.md forbids
    /// matching. So the list either side of the push is what answers.
    /// </summary>
    [Fact]
    public async Task A_push_that_stashed_nothing_says_so()
    {
        var git = new FakeGitRunner()
            .Returns(["stash", "list"], OneStash)
            .Returns(["stash", "push"]);

        StashOutcome outcome = await Create(git)
            .PushAsync(Repository, "nothing to put away", includeUntracked: true, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(StashRefusal.NothingToStash, outcome.Refusal);
    }

    /// <summary>
    /// None of the four operations can reach a spelling that throws work away wholesale or forces
    /// anything. <c>clear</c> drops every stash at once, <c>apply</c> is a second spelling of pop, and
    /// <c>--all</c> would sweep ignored files into the stash: the window offers none of them, so no
    /// argument list may carry them.
    /// </summary>
    [Fact]
    public async Task Nothing_clears_applies_or_forces()
    {
        var git = new FakeGitRunner()
            .Returns(["stash", "list"], OneStash)
            .Returns(["stash", "push"])
            .Returns(["stash", "pop"])
            .Returns(["stash", "drop"]);

        StashService stashes = Create(git);

        await stashes.PushAsync(Repository, "wip", includeUntracked: true, CancellationToken.None);
        await stashes.PopAsync(Repository, First, CancellationToken.None);
        await stashes.DropAsync(Repository, First, CancellationToken.None);

        foreach (string forbidden in new[] { "clear", "apply", "--all", "--force", "-f", "--keep-index", "--staged" })
            Assert.True(git.NeverCalledWith(forbidden), forbidden);
    }

    /// <summary>
    /// Listing is a read, so it goes through <c>ReadAsync</c> and carries
    /// <c>--no-optional-locks</c> -- and it asks for a format rather than parsing the output
    /// <c>git stash list</c> shapes for a terminal.
    /// </summary>
    [Fact]
    public async Task Listing_is_a_read()
    {
        var git = new FakeGitRunner().Returns(["stash", "list"], stdout: string.Empty);

        await Create(git).ListAsync(Repository, CancellationToken.None);

        Assert.True(git.Invocations[0].ReadOnly);
        Assert.Contains(git.Invocations[0].Args, arg => arg.StartsWith("--format=", StringComparison.Ordinal));
    }

    /// <summary>
    /// A blank message is left out of the command rather than passed as an empty one.
    ///
    /// <c>-m ""</c> produces a reflog subject that reads "On main: " and stops. Leaving <c>-m</c> out
    /// is what lets Git write its own <c>WIP on &lt;branch&gt;: &lt;sha&gt; &lt;subject&gt;</c>, which is
    /// a description of the stash rather than a stub.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_message_is_left_out(string? message)
    {
        var git = new FakeGitRunner()
            .Returns(["stash", "list"], stdout: string.Empty)
            .Returns(["stash", "push"]);

        await Create(git).PushAsync(Repository, message, includeUntracked: false, CancellationToken.None);

        string[] push = Assert.Single(git.Invocations, i => i.Args.Contains("push")).Args;

        Assert.Equal(["stash", "push"], push);
    }

    /// <summary>
    /// What a message and a ticked untracked box actually send, in order.
    ///
    /// The message is user text and goes after <c>-m</c>, which consumes whatever follows it -- so a
    /// message beginning with a dash cannot turn into an option and needs no <c>--</c> in front of it.
    /// </summary>
    [Fact]
    public async Task A_message_and_the_untracked_choice_reach_the_command()
    {
        var git = new FakeGitRunner()
            .Returns(["stash", "list"], stdout: string.Empty)
            .Returns(["stash", "push"]);

        await Create(git)
            .PushAsync(Repository, "  --force looking message  ", includeUntracked: true, CancellationToken.None);

        string[] push = Assert.Single(git.Invocations, i => i.Args.Contains("push")).Args;

        Assert.Equal(["stash", "push", "--include-untracked", "-m", "--force looking message"], push);
    }
}
