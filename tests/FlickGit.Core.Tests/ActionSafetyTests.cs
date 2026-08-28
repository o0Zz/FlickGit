using FlickGit.Actions;
using FlickGit.Models;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The safety rules — Hard Requirement 4's third bullet.
///
/// <c>actions.json</c> is the one place in the product where an argument list comes from outside the
/// code. CLAUDE.md's "Safety Rules" say every operation on its destructive list "require[s] a second
/// explicit confirmation regardless of surface", so a user action that runs one from the palette must
/// not be able to opt out of that. These pin the two halves of that: what counts as destructive, and
/// that a placeholder never becomes a concatenated command string.
/// </summary>
public class ActionSafetyTests
{
    [Theory]
    //Every entry on CLAUDE.md's list, in the spelling a user would actually write.
    [InlineData("reset", "--hard")]
    [InlineData("clean", "-fd")]
    [InlineData("clean", "-fdx")]
    [InlineData("branch", "-D")]
    [InlineData("push", "--force")]
    [InlineData("push", "--force-with-lease")]
    //Not on CLAUDE.md's list, and argued in ActionSafety itself: a tag is the only ref with no
    //reflog, so `tag -d` is strictly more final than the `branch -D` that is on the list.
    [InlineData("tag", "-d")]
    [InlineData("tag", "--delete")]
    //The same argument again, and ActionSafety makes it there: a stash has no reflog of its own, so the
    //list entry is the only handle on the commit. `clear` does it to every stash at once.
    [InlineData("stash", "drop")]
    [InlineData("stash", "clear")]
    //Removing a ref other people have already fetched, which is the harm --force is listed for.
    [InlineData("push", "--delete")]
    [InlineData("push", "-d")]
    public void Destructive_git_arguments_are_recognised(string command, string flag)
    {
        Assert.True(ActionSafety.IsDestructive(new GitRun([command, flag])));
    }

    /// <summary>
    /// A global flag between the subcommand and the dangerous one must not hide it.
    ///
    /// <c>git -c core.pager=cat clean -f</c> deletes exactly as much as <c>git clean -f</c> does, so a
    /// matcher that insisted the two tokens be adjacent would be defeated by anybody's gitconfig
    /// habits.
    /// </summary>
    [Fact]
    public void A_flag_between_the_tokens_does_not_hide_a_destructive_command()
    {
        Assert.True(ActionSafety.IsDestructive(new GitRun(["-c", "core.pager=cat", "clean", "-f"])));
    }

    /// <summary>
    /// The dangerous words appearing as *values* are not commands.
    ///
    /// A commit message mentioning a reset, or a grep for the word clean, is an ordinary thing to
    /// write. Confirming those would train the user to dismiss the dialog, which is worse than not
    /// having one.
    /// </summary>
    [Fact]
    public void Ordinary_commands_are_not_destructive()
    {
        string[][] ordinary =
        [
            ["commit", "-m", "reset --hard the docs"],
            ["log", "--grep=clean"],
            ["switch", "main"],
            ["fetch", "--prune"],
        ];

        foreach (string[] args in ordinary)
            Assert.False(ActionSafety.IsDestructive(new GitRun(args)), string.Join(' ', args));
    }

    /// <summary>
    /// <c>restore --staged .</c> only unstages, which loses no work; <c>restore .</c> discards it.
    /// </summary>
    [Fact]
    public void Unstaging_is_not_discarding()
    {
        Assert.True(ActionSafety.IsDestructive(new GitRun(["restore", "."])));
        Assert.False(ActionSafety.IsDestructive(new GitRun(["restore", "--staged", "."])));
    }

    /// <summary>
    /// An external program is always confirmed, because there is no way to know what it does.
    /// </summary>
    [Fact]
    public void An_external_process_always_confirms()
    {
        Assert.True(ActionSafety.IsDestructive(new ProcessRun("cmd.exe", ["/c", "echo hello"])));
    }

    /// <summary>
    /// A sequence is as dangerous as its worst step, wherever that step sits.
    ///
    /// Checking only the first would let a composite hide a <c>reset --hard</c> behind a harmless
    /// <c>fetch</c>, which is exactly how a confirmation gets bypassed.
    /// </summary>
    [Fact]
    public void A_sequence_is_destructive_if_any_step_is()
    {
        var composite = new CompositeRun(
        [
            new GitRun(["fetch", "--prune"]),
            new GitRun(["reset", "--hard", "origin/main"]),
        ]);

        Assert.True(ActionSafety.IsDestructive(composite));
    }

    /// <summary>
    /// A window is a surface, not an operation: everything reachable inside it carries its own
    /// guardrails, which is why every built-in is one.
    /// </summary>
    [Fact]
    public void Opening_a_window_is_not_destructive()
    {
        Assert.False(ActionSafety.IsDestructive(new WindowRun(Cli.VerbKind.Commit)));
    }

    /// <summary>
    /// <c>{files}</c> becomes one argument per file, never one argument containing all of them.
    ///
    /// This is the placeholder half of the same rule. CLAUDE.md: placeholders are "substituted into
    /// <c>ArgumentList</c> entries — never into a concatenated string." Joining a file list would need
    /// a separator, and a file named <c>my report.txt</c> would then be two arguments — or, with
    /// quoting, one argument containing a quote.
    /// </summary>
    [Fact]
    public void A_file_list_expands_to_one_argument_each()
    {
        var context = new ActionContext(
            new(@"C:\dev\my repo", "my repo", HasSubmodules: false, IsBare: false, GitDirectory: @"C:\dev\my repo\.git"),
            Files: ["a b.txt", "c.txt"]);

        IReadOnlyList<string> expanded = ActionPlaceholders.Expand(["add", "--", "{files}"], context);

        Assert.Equal(["add", "--", "a b.txt", "c.txt"], expanded);
    }

    /// <summary>
    /// A path with a space in it stays one argument, which is the whole point of the list.
    /// </summary>
    [Fact]
    public void A_path_with_a_space_remains_one_argument()
    {
        var context = new ActionContext(
            new(@"C:\dev\my repo", "my repo", HasSubmodules: false, IsBare: false, GitDirectory: @"C:\dev\my repo\.git"),
            Branch: "feature/a b");

        IReadOnlyList<string> expanded = ActionPlaceholders.Expand(["log", "{repo}", "{branch}"], context);

        Assert.Equal(["log", @"C:\dev\my repo", "feature/a b"], expanded);
    }

    /// <summary>
    /// Nothing ticked means no paths, not "everything".
    ///
    /// <c>git add -- </c> with no paths stages nothing, which is the safe reading. Dropping the
    /// placeholder and leaving <c>git add --</c> is therefore correct; expanding it to <c>.</c> would
    /// be the single worst thing this class could do.
    /// </summary>
    [Fact]
    public void No_files_expands_to_no_arguments()
    {
        var context = new ActionContext(new RepositoryInfo(@"C:\dev\r", "r", false, false, @"C:\dev\r\.git"));

        Assert.Equal(["add", "--"], ActionPlaceholders.Expand(["add", "--", "{files}"], context));
    }

    /// <summary>
    /// An unknown placeholder is left alone rather than deleted.
    ///
    /// Deleting it would turn <c>git push {remoat}</c> into <c>git push</c> — a different command
    /// that succeeds, which is the worst way to report a typo.
    /// </summary>
    [Fact]
    public void An_unknown_placeholder_is_left_as_written()
    {
        var context = new ActionContext(new RepositoryInfo(@"C:\dev\r", "r", false, false, @"C:\dev\r\.git"));

        Assert.Equal(["push", "{remoat}"], ActionPlaceholders.Expand(["push", "{remoat}"], context));
    }
}
