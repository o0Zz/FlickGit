using FlickGit.Git;
using FlickGit.Models;

namespace FlickGit.Tests;

/// <summary>
/// An <see cref="IGitProcessRunner"/> that answers from a table instead of starting a
/// process.
///
/// Every test that needs a service rather than a parser goes through this, and no test starts a
/// real git.exe — CLAUDE.md, Hard Requirement 4. A fake runner makes the *arguments* assertable,
/// which is the half a temporary repository would hide. <see cref="Invocations"/> records what was
/// asked, so a test can pin that a read went out with <c>--no-optional-locks</c>, or that a switch
/// never ran at all.
/// </summary>
internal sealed class FakeGitRunner : IGitProcessRunner
{
    private readonly List<Rule> _rules = [];

    /// <summary>Every call, in order, with the flag that says whether it was a read.</summary>
    public List<Invocation> Invocations { get; } = [];

    /// <summary>
    /// Answers any call whose arguments contain <paramref name="match"/> as a contiguous
    /// subsequence. Later rules win, so a test can register a default and then override it.
    /// </summary>
    public FakeGitRunner Returns(string[] match, string stdout = "", int exitCode = 0, string stderr = "")
    {
        _rules.Insert(0, new Rule(match, _ => new GitResult(exitCode, stdout, stderr, TimeSpan.Zero)));
        return this;
    }

    /// <summary>
    /// Answers from a callback evaluated at call time, so the reply can depend on what was asked
    /// earlier.
    ///
    /// Needed for anything Git itself would answer statefully. The switch service stashes with a
    /// freshly generated message and then looks that message up in `stash list`; a canned reply
    /// cannot contain a GUID the test never saw, so the only way to exercise find-by-message
    /// rather than find-by-index is to let the fake behave like Git and echo it back.
    /// </summary>
    public FakeGitRunner ReturnsFrom(string[] match, Func<FakeGitRunner, string> stdout)
    {
        _rules.Insert(0, new Rule(match, self => new GitResult(0, stdout(self), string.Empty, TimeSpan.Zero)));
        return this;
    }

    /// <summary>
    /// The value that followed <paramref name="option"/> in the most recent matching invocation,
    /// for a fake that has to reflect an argument back.
    /// </summary>
    public string? ArgumentAfter(string option)
    {
        for (int i = Invocations.Count - 1; i >= 0; i--)
        {
            string[] args = Invocations[i].Args;
            int index = Array.IndexOf(args, option);

            if (index >= 0 && index + 1 < args.Length)
                return args[index + 1];
        }

        return null;
    }

    public Task<GitResult> RunAsync(string? repositoryPath, IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        Execute(repositoryPath, args, readOnly: false);

    public Task<GitResult> ReadAsync(string? repositoryPath, IReadOnlyList<string> args, CancellationToken cancellationToken) =>
        Execute(repositoryPath, args, readOnly: true);

    /// <summary>
    /// Records what was written to stdin as well as what was asked.
    ///
    /// The patch text is the whole point of a staging call, so a test that only saw the arguments
    /// would be checking that `apply --cached` ran and nothing about what it would have done.
    /// </summary>
    public Task<GitResult> RunWithInputAsync(
        string? repositoryPath,
        IReadOnlyList<string> args,
        string standardInput,
        CancellationToken cancellationToken)
    {
        StandardInput.Add(standardInput);
        return Execute(repositoryPath, args, readOnly: false);
    }

    /// <summary>Everything fed to stdin, in order.</summary>
    public List<string> StandardInput { get; } = [];

    /// <summary>
    /// Present because the interface has it, and nothing more.
    ///
    /// The only streaming caller is clone, which Hard Requirement 4 puts out of scope -- so there
    /// is no progress replay here to feed a parser that no test asks about.
    /// </summary>
    public Task<GitResult> RunStreamingAsync(
        string? repositoryPath,
        IReadOnlyList<string> args,
        Action<string> onStandardErrorLine,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Execute(repositoryPath, args, readOnly: false);
    }

    private Task<GitResult> Execute(string? repositoryPath, IReadOnlyList<string> args, bool readOnly)
    {
        Invocations.Add(new Invocation(repositoryPath, [.. args], readOnly));

        foreach (Rule rule in _rules)
        {
            if (Matches(args, rule.Match))
                return Task.FromResult(rule.Result(this));
        }

        //An unmatched call fails rather than returning empty success. A service that quietly
        //carried on with "" would pass a test for the wrong reason.
        return Task.FromResult(new GitResult(
            128,
            string.Empty,
            $"fake runner: no rule for `git {string.Join(' ', args)}`",
            TimeSpan.Zero));
    }

    private static bool Matches(IReadOnlyList<string> args, string[] match)
    {
        for (int start = 0; start + match.Length <= args.Count; start++)
        {
            bool all = true;

            for (int i = 0; i < match.Length; i++)
            {
                if (!string.Equals(args[start + i], match[i], StringComparison.Ordinal))
                {
                    all = false;
                    break;
                }
            }

            if (all)
                return true;
        }

        return false;
    }

    /// <summary>True when no call carried <paramref name="argument"/>. Used to assert a command never ran.</summary>
    public bool NeverCalledWith(string argument) =>
        Invocations.All(i => !i.Args.Contains(argument, StringComparer.Ordinal));

    internal sealed record Invocation(string? RepositoryPath, string[] Args, bool ReadOnly);

    private sealed record Rule(string[] Match, Func<FakeGitRunner, GitResult> Result);
}
