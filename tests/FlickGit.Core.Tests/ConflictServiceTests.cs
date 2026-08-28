using FlickGit.Logging;
using FlickGit.Merges;
using FlickGit.Models;
using FlickGit.Repositories;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The way out of a conflict, and the four refusals that keep it from being a way to lose work.
///
/// In scope on two of Hard Requirement 4's bullets. <see cref="ConflictService.TakeSideAsync"/> is a
/// <b>sequence</b> — checkout then add, in that order and never the other way — which is exactly the
/// kind of bug clicking does not reveal, because both orders look identical on screen and only one of
/// them records the conflict markers as the resolution. The rest are <b>safety rules</b>: continue is
/// refused while a path is unmerged, no command carries <c>--force</c>, and nothing reaches
/// <c>--abort</c> except a caller asking for it.
/// </summary>
public class ConflictServiceTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\dev\repo", "repo", HasSubmodules: false, IsBare: false, GitDirectory: @"C:\dev\repo\.git");

    private static ConflictService Create(FakeGitRunner git) =>
        new(git, new RepositoryService(git), NullLog.Instance);

    /// <summary>`git diff --diff-filter=U` with nothing to say: no unmerged paths left.</summary>
    private static FakeGitRunner Resolved() =>
        new FakeGitRunner().Returns(["--diff-filter=U"], stdout: string.Empty);

    [Fact]
    public async Task TakingASideChecksOutBeforeItStages()
    {
        //The order is the whole test. Reversed, `git add` would record the file with its conflict
        //markers still in it as the resolution, and the checkout that followed would overwrite the
        //working tree under an index already saying "resolved" -- a commit containing markers, arrived
        //at by two commands that both exited 0.
        var git = new FakeGitRunner()
            .Returns(["checkout", "--ours"])
            .Returns(["add", "--"]);

        ConflictResult result = await Create(git)
            .TakeSideAsync(Repository, "src/a[1].cs", ConflictSide.Ours, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, git.Invocations.Count);

        Assert.Equal(
            ["checkout", "--ours", "--", ":(literal)src/a[1].cs"],
            git.Invocations[0].Args);

        //And the pathspec cannot glob, on both calls. `a[1].cs` is an ordinary Windows file name and a
        //character class to Git, so without :(literal) the resolution would land on `a1.cs`.
        Assert.Equal(
            ["add", "--", ":(literal)src/a[1].cs"],
            git.Invocations[1].Args);
    }

    [Fact]
    public async Task AFailedCheckoutStagesNothing()
    {
        //The return before the add is what leaves the path unmerged -- and therefore still resolvable
        //by every other route, including the other side.
        var git = new FakeGitRunner()
            .Returns(["checkout", "--theirs"], exitCode: 1, stderr: "error: path 'x' does not have their version");

        ConflictResult result = await Create(git)
            .TakeSideAsync(Repository, "x", ConflictSide.Theirs, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("does not have their version", result.Error);
        Assert.True(git.NeverCalledWith("add"));
    }

    [Fact]
    public async Task ContinueRunsNothingWhileAPathIsStillUnmerged()
    {
        //THE safety rule of this feature. The window disables its button on the status it happens to be
        //holding; this refuses on the state the repository is actually in a moment before the command
        //would go -- which is what covers a terminal, or an IDE, creating a conflict while the window
        //sat open. `rebase --continue` over a half-resolved tree is how markers reach history.
        var git = new FakeGitRunner()
            .Returns(["--diff-filter=U"], stdout: "src/a.cs\0src/b.cs\0");

        ConflictResult result = await Create(git)
            .ContinueAsync(Repository, MergeOperation.Rebase, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(git.NeverCalledWith("--continue"));

        //Named, not counted: the paths are what the user acts on next.
        Assert.Contains("src/a.cs", result.Error);
        Assert.Contains("src/b.cs", result.Error);

        //And flagged as the refusal that ran nothing, so the surface can say "resolve these first"
        //rather than putting a bare list of paths under "the rebase did not continue".
        Assert.True(result.Blocked);
    }

    [Fact]
    public async Task ContinueCarriesAnEditorThatCannotOpen()
    {
        //All four --continue spellings open an editor for the commit message, and this process has no
        //console for one to appear on -- so without core.editor=true the command hangs until it is
        //cancelled, which reads as the whole application freezing.
        FakeGitRunner git = Resolved().Returns(["rebase", "--continue"]);

        ConflictResult result = await Create(git)
            .ContinueAsync(Repository, MergeOperation.Rebase, CancellationToken.None);

        Assert.True(result.Succeeded);

        string[] args = git.Invocations[^1].Args;
        Assert.Equal(["-c", "core.editor=true", "rebase", "--continue"], args);
    }

    [Fact]
    public async Task NoConflictCommandCanEverCarryForce()
    {
        //There is no code path to it: not one method here takes a force parameter, so this pins the
        //absence rather than a caller's choice. It is also why a delete/modify conflict is not fully
        //served -- recording "take the deletion" would need `git rm --force` on an unmerged path.
        FakeGitRunner git = Resolved()
            .Returns(["checkout", "--ours"])
            .Returns(["checkout", "--theirs"])
            .Returns(["add", "--"])
            .Returns(["merge", "--continue"])
            .Returns(["merge", "--abort"]);

        ConflictService conflicts = Create(git);

        await conflicts.TakeSideAsync(Repository, "a", ConflictSide.Ours, CancellationToken.None);
        await conflicts.TakeSideAsync(Repository, "b", ConflictSide.Theirs, CancellationToken.None);
        await conflicts.MarkResolvedAsync(Repository, "c", CancellationToken.None);
        await conflicts.ContinueAsync(Repository, MergeOperation.Merge, CancellationToken.None);
        await conflicts.AbortAsync(Repository, MergeOperation.Merge, CancellationToken.None);

        Assert.True(git.NeverCalledWith("--force"));
        Assert.True(git.NeverCalledWith("-f"));
    }

    [Fact]
    public async Task NothingReachesAbortExceptAbort()
    {
        //CLAUDE.md, "Pull --rebase": "do not automatically abort a rebase". That is a rule about the
        //code path, not about the wording of a message -- so a continue that Git refuses must leave the
        //half-finished operation exactly where it is, for the user to decide about. Both failure shapes
        //are exercised: refused by our gate, and refused by Git.
        var blocked = new FakeGitRunner().Returns(["--diff-filter=U"], stdout: "src/a.cs\0");

        await Create(blocked).ContinueAsync(Repository, MergeOperation.Rebase, CancellationToken.None);
        Assert.True(blocked.NeverCalledWith("--abort"));

        FakeGitRunner refused = Resolved()
            .Returns(["rebase", "--continue"], exitCode: 1, stderr: "No changes - did you forget to use 'git add'?");

        ConflictResult result = await Create(refused)
            .ContinueAsync(Repository, MergeOperation.Rebase, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(refused.NeverCalledWith("--abort"));

        //Git ran and objected, which is the other failure shape: not blocked, so the surface shows Git's
        //words alone rather than prefixing them with a sentence about unresolved files.
        Assert.False(result.Blocked);

        //Git's own words, never paraphrased -- including the suggestion of `--skip`, which this product
        //does not offer because skipping drops somebody's commit.
        Assert.Contains("No changes", result.Error);
    }

    [Fact]
    public async Task TheGateReadsRatherThanWrites()
    {
        //--no-optional-locks on the one read here, per CLAUDE.md: this runs while the user's IDE is
        //very likely looking at the same conflicted tree, and a status read that takes index.lock is
        //how the two collide.
        FakeGitRunner git = Resolved().Returns(["rebase", "--continue"]);

        await Create(git).ContinueAsync(Repository, MergeOperation.Rebase, CancellationToken.None);

        FakeGitRunner.Invocation gate = git.Invocations[0];

        Assert.True(gate.ReadOnly);
        Assert.Equal(["diff", "--name-only", "--diff-filter=U", "-z"], gate.Args[..4]);
    }

    [Fact]
    public async Task AnUnreadableRepositoryStopsTheContinue()
    {
        //The direction to fail in. This gates a command that writes history, so a read that did not
        //come back must not be taken as "nothing is unmerged".
        var git = new FakeGitRunner().Returns(["--diff-filter=U"], exitCode: 128, stderr: "fatal: not a git repository");

        ConflictResult result = await Create(git)
            .ContinueAsync(Repository, MergeOperation.CherryPick, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(git.NeverCalledWith("--continue"));
    }

    [Theory]
    [InlineData(MergeOperation.Merge, "merge")]
    [InlineData(MergeOperation.Rebase, "rebase")]
    [InlineData(MergeOperation.CherryPick, "cherry-pick")]
    [InlineData(MergeOperation.Revert, "revert")]
    public void EachOperationSpellsItsOwnVerb(MergeOperation operation, string expected)
    {
        //The four share one continue and one abort, and the only thing that differs is this word. A
        //wrong one here would run `merge --abort` against a rebase, which Git refuses -- loudly, but
        //with a message about the wrong operation entirely.
        Assert.Equal(expected, MergeState.Verb(operation));
    }
}
