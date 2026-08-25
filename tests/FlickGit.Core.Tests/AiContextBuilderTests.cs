using FlickGit.Ai;
using FlickGit.History;
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
public class AiContextBuilderTests
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

    private static Task<AiContext> Build(FakeGitRunner git, RepositoryStatus status) =>
        new AiContextBuilder(git)
            .ForCommitAsync(Repository, status, DiffPayload.VerbatimCeilingBytes, CancellationToken.None);

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

        AiContext context = await Build(git, Status(
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

        AiContext context = await Build(git, Status(File("src/A.cs", selected: false)));

        Assert.True(context.IsEmpty);
        Assert.Empty(git.Invocations);
    }

    /// <summary>
    /// A changelog is computed over the range it was handed, and its style travels in the payload.
    ///
    /// Both halves are in scope. The revisions are the safety-critical half: a changelog over history
    /// computed against HEAD would describe whatever happens to be checked out. The style is the other
    /// half of the same argument -- it rides in the payload precisely so that a prompt file the user
    /// owns stays the whole prompt.
    /// </summary>
    [Fact]
    public async Task The_changelog_diffs_the_range_and_carries_the_style_in_the_payload()
    {
        var git = new FakeGitRunner()
            .Returns(["diff", "b2", "e5"], "diff --git a/src/A.cs b/src/A.cs\n+new\n");

        AiContext context = await new AiContextBuilder(git).ForChangelogAsync(
            Repository,
            baseSpec: "b2",
            tipSpec: "e5",
            commits: [Commit("e5", "add pooling"), Commit("c3", "fix the leak")],
            changed: [File("src/A.cs"), File("package-lock.json")],
            ChangelogStyle.Detailed,
            DiffPayload.VerbatimCeilingBytes,
            CancellationToken.None);

        FakeGitRunner.Invocation diff = git.Invocations.Single(i => i.Args.Contains("diff"));

        //The range's two ends, in that order, and no HEAD anywhere near it.
        Assert.Equal(["diff", "b2", "e5"], diff.Args.Take(3));
        Assert.DoesNotContain("HEAD", diff.Args);
        Assert.True(diff.ReadOnly);

        //The same exclusion rules as every other payload: one builder, one set of rules.
        Assert.DoesNotContain("package-lock.json", diff.Args);

        string text = context.ToPromptText();

        //Oldest first, which is the order the work was done in and the order a changelog reads in.
        Assert.True(text.IndexOf("fix the leak", StringComparison.Ordinal) < text.IndexOf("add pooling", StringComparison.Ordinal));

        //Last, and the whole of how a style reaches the model.
        Assert.EndsWith(ChangelogPrompt.Instruction(ChangelogStyle.Detailed) + "\n", text, StringComparison.Ordinal);
    }

    private static LogCommit Commit(string sha, string subject) => new()
    {
        Sha = sha,
        ShortSha = sha,
        Parents = [],
        Author = "Ana",
        When = DateTimeOffset.UnixEpoch,
        Refs = string.Empty,
        Message = subject,
    };
}
