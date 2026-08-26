using FlickGit.Git;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Argument construction.
///
/// CLAUDE.md, "Testing" asks for a test that asserts "no string concatenation path exists".
/// A test cannot prove a negative about the whole codebase, so it does the next best thing:
/// it pins the invariants of the one function every Git call in the product goes through,
/// including that each argument arrives as its own list element with no quoting applied.
/// A path with a space that survived quoting would be a path with a space that a shell could
/// re-split.
/// </summary>
public class GitArgumentTests
{
    private static List<string> Build(string? repository, string[] args, bool readOnly)
    {
        var target = new List<string>();
        GitProcessRunner.BuildArguments(target, repository, args, readOnly);
        return target;
    }

    [Fact]
    public void RepositoryIsPassedWithDashCBeforeAnythingElse()
    {
        List<string> args = Build(@"C:\dev\repo", ["status"], readOnly: true);

        //-C first, so Git resolves the repository before any -c option or subcommand is
        //considered. Never "cd then run": the working directory belongs to the whole
        //resident process.
        Assert.Equal("-C", args[0]);
        Assert.Equal(@"C:\dev\repo", args[1]);
    }

    [Fact]
    public void QuotePathIsAlwaysDisabled()
    {
        List<string> args = Build(@"C:\dev\repo", ["status"], readOnly: false);

        int index = args.IndexOf("-c");

        Assert.True(index >= 0);
        Assert.Equal("core.quotepath=false", args[index + 1]);
    }

    [Fact]
    public void ReadOperationsPassNoOptionalLocks()
    {
        //THE flag. Without it `git status` takes the index lock to write a refreshed stat
        //cache, and a background scan collides with the user's IDE in the same tree.
        Assert.Contains("--no-optional-locks", Build(@"C:\dev\repo", ["status"], readOnly: true));
    }

    [Fact]
    public void WriteOperationsDoNotPassNoOptionalLocks()
    {
        //A commit is supposed to take the lock. Passing the flag on a write would be
        //meaningless at best and, on a future Git, a refusal.
        Assert.DoesNotContain("--no-optional-locks", Build(@"C:\dev\repo", ["commit"], readOnly: false));
    }

    [Fact]
    public void RepositoryLessCommandsOmitDashC()
    {
        //`git --version` has no repository. Passing -C with a null path would either throw
        //or run against the process's working directory.
        List<string> args = Build(null, ["--version"], readOnly: true);

        Assert.DoesNotContain("-C", args);
        Assert.Contains("--version", args);
    }

    [Theory]
    [InlineData(@"C:\dev\my repo\src")]
    [InlineData(@"C:\dev\Ünïcödé Ω\repo")]
    [InlineData(@"C:\dev\repo with ""quote""")]
    [InlineData(@"C:\dev\repo & more")]
    public void PathsAreNeverQuotedOrEscaped(string path)
    {
        //Each argument is one list element, verbatim. ProcessStartInfo.ArgumentList does the
        //escaping when it builds the real command line, and doing it here as well would
        //double-escape -- which is how a path with a quote in it becomes a different path.
        List<string> args = Build(path, ["status"], readOnly: true);

        Assert.Equal(path, args[1]);
    }

    [Fact]
    public void ArgumentOrderIsPreserved()
    {
        List<string> args = Build(@"C:\dev\repo", ["diff", "--numstat", "-z"], readOnly: true);

        int diff = args.IndexOf("diff");

        Assert.Equal("--numstat", args[diff + 1]);
        Assert.Equal("-z", args[diff + 2]);
    }

    [Fact]
    public void NullArgumentThrowsRatherThanReachingGitAsAnEmptyString()
    {
        //An empty argument silently changes what a Git command means -- `git add -- ""` is
        //not `git add --`. Failing loudly is the only safe behaviour.
        Assert.Throws<ArgumentNullException>(() =>
            Build(@"C:\dev\repo", ["add", null!], readOnly: false));
    }

    [Fact]
    public void PathListsKeepTheirDoubleDashSeparator()
    {
        //`--` before a path list is not decoration: without it, a file named "-f" or
        //"--cached" is read as an option.
        List<string> args = Build(@"C:\dev\repo", ["add", "--", "-f", "--cached"], readOnly: false);

        int separator = args.IndexOf("--");

        Assert.True(separator > 0);
        Assert.Equal("-f", args[separator + 1]);
        Assert.Equal("--cached", args[separator + 2]);
    }
}
