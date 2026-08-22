using FlickGit.Ai;
using FlickGit.Models;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// What Git is asked for, on the way to the AI provider.
///
/// In scope under Hard Requirement 4 as <b>the safety rules</b>. The fake runner makes the
/// <i>arguments</i> assertable, which is the half that matters here: an excluded path appearing in
/// that argument list is a leak, and no assertion about the resulting text would catch it as
/// directly.
/// </summary>
public class CommitContextBuilderTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\repos\alpha", "alpha", HasSubmodules: false, IsBare: false);

    private static GitFileChange File(string path, bool selected = true, bool untracked = false) =>
        new()
        {
            Path = path,
            WorkTreeStatus = untracked ? GitChangeType.Untracked : GitChangeType.Modified,
            AddedLines = 2,
            RemovedLines = 1,
            IsUntracked = untracked,
            IsSelected = selected,
        };

    private static RepositoryStatus Status(params GitFileChange[] files) =>
        new() { Repository = Repository, Branch = "main", Files = files };

    private static Task<CommitContext> Build(FakeGitRunner git, RepositoryStatus status) =>
        new CommitContextBuilder(git).BuildAsync(Repository, status, DiffPayload.VerbatimCeilingBytes, CancellationToken.None);

    /// <summary>
    /// The diff names only the paths that may be sent, and it is a read.
    ///
    /// Excluding at the pathspec rather than filtering the output afterwards means the excluded
    /// content never enters the process at all — which is a stronger guarantee than removing it once
    /// it has.
    /// </summary>
    [Fact]
    public async Task The_diff_request_names_only_the_included_paths_and_is_a_read()
    {
        var git = new FakeGitRunner()
            .Returns(["diff", "HEAD"], "diff --git a/src/A.cs b/src/A.cs\n+new\n");

        await Build(git, Status(
            File("src/A.cs"),
            File("package-lock.json"),
            File(".env"),
            File("src/Skipped.cs", selected: false)));

        FakeGitRunner.Invocation diff = git.Invocations.Single(i => i.Args.Contains("diff"));

        Assert.Contains("src/A.cs", diff.Args);

        //The two excluded files, and the one the user unticked.
        Assert.DoesNotContain("package-lock.json", diff.Args);
        Assert.DoesNotContain(".env", diff.Args);
        Assert.DoesNotContain("src/Skipped.cs", diff.Args);

        //A read, so `--no-optional-locks` is added and the index is never refreshed underneath an
        //IDE doing the same thing.
        Assert.True(diff.ReadOnly);

        //HEAD, not --cached. CommitFlow stages at commit time, so at this moment the index is empty
        //and `--cached` would send nothing at all.
        Assert.Contains("HEAD", diff.Args);
        Assert.DoesNotContain("--cached", diff.Args);
    }

    /// <summary>
    /// Nothing sendable means no Git process at all — and still a usable context, because the file
    /// list alone is worth a commit message.
    /// </summary>
    [Fact]
    public async Task Nothing_is_asked_of_git_when_every_file_is_excluded()
    {
        var git = new FakeGitRunner();

        CommitContext context = await Build(git, Status(
            File("package-lock.json"),
            File("scratch/dump.json", untracked: true)));

        Assert.Empty(git.Invocations);
        Assert.Empty(context.Diff);

        //The names still go over, with the reason each was held back, so the model does not describe
        //a change it was shown none of.
        Assert.Equal(2, context.Files.Count);
        Assert.Contains(context.Excluded, e => e.Contains("lock file", StringComparison.Ordinal));
        Assert.Contains(context.Excluded, e => e.Contains("untracked", StringComparison.Ordinal));
        Assert.False(context.IsEmpty);
    }

    /// <summary>An empty selection is not worth a request, or a message.</summary>
    [Fact]
    public async Task An_empty_selection_produces_an_empty_context()
    {
        var git = new FakeGitRunner();

        CommitContext context = await Build(git, Status(File("src/A.cs", selected: false)));

        Assert.True(context.IsEmpty);
        Assert.Empty(git.Invocations);
    }
}
