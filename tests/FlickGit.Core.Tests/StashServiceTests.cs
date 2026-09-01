using FlickGit.History;
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
        new(@"C:\dev\repo", "repo", HasSubmodules: false, IsBare: false, GitDirectory: @"C:\dev\repo\.git");

    /// <summary>The row every pop and drop test below claims to have clicked.</summary>
    private static readonly GitStash First =
        new("stash@{0}", "a1b2c3d4e5", "main", "pool leak on reconnect", default, ["b0b0b0b0b0", "1de41de41d"]);

    private static readonly string OneStash = Stream(Record("stash@{0}", "a1b2c3d4e5", "On main: pool leak on reconnect"));

    private static StashService Create(FakeGitRunner git) =>
        new(git, new RepositoryService(git), new HistoryService(git));

    /// <summary>
    /// One <c>--format</c> record: reference, sha, parents, date, subject. Written through a helper
    /// rather than inline because the field the tests care least about -- the parents -- is the one
    /// whose absence would silently shift every field after it.
    /// </summary>
    private static string Record(
        string reference,
        string sha,
        string subject,
        string parents = "b0b0b0b0b0 1de41de41d",
        string date = "2026-08-26T14:03:00+00:00") =>
        $"{reference}\u001f{sha}\u001f{parents}\u001f{date}\u001f{subject}";

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
            Record("stash@{0}", "a1b2c3d4e5", "On main: pool leak\ton reconnect",
                date: "2026-08-26T14:03:00+02:00"),
            Record("stash@{1}", "f6a7b8c9d0", "WIP on feature/storage-gw: 9f0e1d2 café Ünïcödé",
                date: "2026-08-24T09:40:12+02:00"),

            //Four fields where there should be five. Dropped, so a truncated read costs the last row
            //rather than the list.
            "stash@{2}\u001fdeadbeef01\u001fb0b0b0b0b0\u001f2026-08-01T00:00:00+00:00"));

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
                Record("stash@{0}", "1111111111", "On main: pushed since"),
                Record("stash@{1}", "a1b2c3d4e5", "On main: pool leak")))
            .Returns(["stash", "drop"]);

        var clicked = new GitStash("stash@{1}", "9999999999", "main", "the row that was drawn", default, []);

        StashDropOutcome outcome = await Create(git).DropAsync(Repository, [clicked], CancellationToken.None);

        Assert.False(outcome.Outcome.Succeeded);
        Assert.Equal(StashRefusal.Moved, outcome.Outcome.Refusal);
        Assert.Equal(0, outcome.Dropped);

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
        await stashes.DropAsync(Repository, [First], CancellationToken.None);

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

    /// <summary>
    /// A batch drop runs highest reflog index first, whatever order the rows arrived in.
    ///
    /// <b>THE rule of the batch, and the reason the ordering is in the service rather than the
    /// window.</b> Dropping <c>stash@{k}</c> renumbers every entry above k and leaves everything
    /// below it alone. So dropped in the order a user Ctrl+clicked them, the second command would
    /// name a position that now holds a different stash — and for a drop there is nothing in the
    /// product that finds the casualty again. Highest first is what keeps every reference still
    /// waiting its turn naming the commit it named when the user pointed at it.
    /// </summary>
    [Fact]
    public async Task A_batch_drop_takes_the_highest_reflog_index_first()
    {
        var git = new FakeGitRunner()
            .Returns(["stash", "list"], Stream(
                Record("stash@{0}", "0000000000", "On main: newest"),
                Record("stash@{1}", "1111111111", "On main: second"),
                Record("stash@{2}", "2222222222", "On main: third"),
                Record("stash@{3}", "3333333333", "On main: oldest")))
            .Returns(["stash", "drop"]);

        //Handed over newest-first and out of order, which is what a Ctrl+click selection gives.
        GitStash[] selection =
        [
            new("stash@{0}", "0000000000", "main", "newest", default, []),
            new("stash@{3}", "3333333333", "main", "oldest", default, []),
            new("stash@{1}", "1111111111", "main", "second", default, []),
        ];

        StashDropOutcome outcome = await Create(git).DropAsync(Repository, selection, CancellationToken.None);

        Assert.True(outcome.Outcome.Succeeded);
        Assert.Equal(3, outcome.Dropped);

        string[] dropped =
        [
            .. git.Invocations
                .Where(invocation => invocation.Args.Contains("drop"))
                .Select(invocation => invocation.Args[^1]),
        ];

        Assert.Equal(["stash@{3}", "stash@{1}", "stash@{0}"], dropped);
    }

    /// <summary>
    /// One stale row in a selection refuses the whole batch, and nothing is dropped.
    ///
    /// Checked for every row against one read <i>before</i> the first command, rather than as each
    /// row's turn comes: verified as it went, the batch would drop the rows above the stale one and
    /// only then stop, having destroyed stashes on the strength of a list the user was no longer
    /// looking at.
    /// </summary>
    [Fact]
    public async Task A_batch_drop_with_one_stale_row_drops_nothing()
    {
        var git = new FakeGitRunner()
            .Returns(["stash", "list"], Stream(
                Record("stash@{0}", "0000000000", "On main: newest"),
                Record("stash@{1}", "1111111111", "On main: second")))
            .Returns(["stash", "drop"]);

        GitStash[] selection =
        [
            new("stash@{0}", "0000000000", "main", "newest", default, []),

            //Same position, different commit: something pushed or popped while the window sat open.
            new("stash@{1}", "9999999999", "main", "the row that was drawn", default, []),
        ];

        StashDropOutcome outcome = await Create(git).DropAsync(Repository, selection, CancellationToken.None);

        Assert.Equal(StashRefusal.Moved, outcome.Outcome.Refusal);
        Assert.Equal(0, outcome.Dropped);
        Assert.True(git.NeverCalledWith("drop"));
    }

    /// <summary>
    /// The parents field, which is what makes a stash's contents viewable.
    ///
    /// The third parent is the one that matters: it holds the untracked files that went into the
    /// stash, and they are in no other tree — so a stash listing that could not tell it was there
    /// would show a stash of five files as three and say nothing about the other two.
    /// </summary>
    [Fact]
    public void A_stash_names_its_parents()
    {
        IReadOnlyList<GitStash> stashes = StashService.Parse(Stream(
            Record("stash@{0}", "aaaaaaaaaa", "On main: with untracked",
                parents: "b0b0b0b0b0 1de41de41d c0ffee00c0"),
            Record("stash@{1}", "bbbbbbbbbb", "On main: without",
                parents: "d0d0d0d0d0 2de42de42d")));

        Assert.Equal(["b0b0b0b0b0", "1de41de41d", "c0ffee00c0"], stashes[0].Parents);
        Assert.Equal("b0b0b0b0b0", stashes[0].BaseSha);
        Assert.Equal("c0ffee00c0", stashes[0].UntrackedSha);

        //The tracked half is the stash commit against the commit it was made on, which is exactly
        //what `git stash show` compares -- and the label names the stash rather than a second hash,
        //because "which stash is this" is the question the pane's header has to answer.
        Assert.Equal("b0b0b0b0b0", stashes[0].TrackedRange?.BaseSpec);
        Assert.Equal("aaaaaaaaaa", stashes[0].TrackedRange?.TipSpec);
        Assert.Equal("b0b0b0b ↔ stash@{0}", stashes[0].TrackedRange?.Label);

        //The untracked half is that third commit against the empty tree, so every file in it reads as
        //an addition.
        Assert.Equal(CommitRange.EmptyTree, stashes[0].UntrackedRange?.BaseSpec);
        Assert.Equal("c0ffee00c0", stashes[0].UntrackedRange?.TipSpec);

        //Two parents, so no untracked half at all -- and null rather than an empty range, because the
        //window has to be able to not ask for it.
        Assert.Null(stashes[1].UntrackedSha);
        Assert.Null(stashes[1].UntrackedRange);
    }
}
