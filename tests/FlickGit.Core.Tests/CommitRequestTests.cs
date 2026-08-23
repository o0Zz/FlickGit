using FlickGit.Commits;
using FlickGit.Models;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Turning a status and its ticks into the two path lists <see cref="CommitFlow"/> acts on.
///
/// In scope under Hard Requirement 4 as <b>the safety rules</b> and <b>the commit sequence</b>.
/// The commit window and the command line both derive their request here, so this is the one place
/// that decides what gets staged and what comes back out of the index. Getting
/// <c>PathsToUnstage</c> wrong commits a file the user deliberately unticked, because `git commit`
/// commits the index and not the selection.
/// </summary>
public class CommitRequestTests
{
    private static readonly RepositoryInfo Repository =
        new(@"C:\repos\alpha", "alpha", HasSubmodules: false, IsBare: false);

    private static GitFileChange File(
        string path,
        bool selected,
        bool staged = false,
        bool untracked = false,
        bool chosenHunks = false,
        GitChangeType workTree = GitChangeType.Modified,
        GitChangeType index = GitChangeType.None) =>
        new()
        {
            Path = path,
            IndexStatus = index,
            WorkTreeStatus = workTree,
            IsUntracked = untracked,
            IsStaged = staged,
            IsSelected = selected,
            HasChosenHunks = chosenHunks,
        };

    private static RepositoryStatus Status(params GitFileChange[] files) =>
        new() { Repository = Repository, Branch = "main", Files = files };

    private static CommitRequest Build(RepositoryStatus status, string? target = null, bool create = false) =>
        CommitRequest.From(Repository, status, "a message", target, create, push: false, confirm: null);

    [Fact]
    public void Only_ticked_paths_are_staged()
    {
        CommitRequest request = Build(Status(
            File("src/Kept.cs", selected: true),
            File("src/Skipped.cs", selected: false)));

        Assert.Equal(["src/Kept.cs"], request.SelectedPaths);
    }

    /// <summary>
    /// The rule the whole type exists for. A file the user staged elsewhere and then unticked here
    /// has to leave the index, or `git commit` commits it anyway and the untick did nothing.
    /// </summary>
    [Fact]
    public void An_unticked_but_staged_file_is_unstaged()
    {
        CommitRequest request = Build(Status(
            File("src/Kept.cs", selected: true, staged: true),
            File("src/Unticked.cs", selected: false, staged: true),
            File("src/NeverStaged.cs", selected: false)));

        Assert.Equal(["src/Unticked.cs"], request.PathsToUnstage);
        Assert.Equal(["src/Kept.cs"], request.SelectedPaths);
    }

    /// <summary>
    /// Untracked files arrive from <c>StatusService</c> with <c>IsSelected = false</c>, and nothing
    /// here may put them back. CLAUDE.md calls this "the single most valuable safety default in the
    /// product" — and the popup, which has no tick boxes at all, depends on it entirely.
    /// </summary>
    [Fact]
    public void Untracked_files_left_unticked_are_not_staged()
    {
        CommitRequest request = Build(Status(
            File("src/Tracked.cs", selected: true),
            File(".env", selected: false, untracked: true, workTree: GitChangeType.Untracked),
            File("scratch/dump.json", selected: false, untracked: true, workTree: GitChangeType.Untracked)));

        Assert.Equal(["src/Tracked.cs"], request.SelectedPaths);
        Assert.Empty(request.PathsToUnstage);
    }

    /// <summary>
    /// Null <c>TargetBranch</c> is what makes the ordinary commit cost no Git call for the branch.
    /// </summary>
    [Fact]
    public void No_target_branch_means_no_switch_is_requested()
    {
        CommitRequest request = Build(Status(File("src/Kept.cs", selected: true)));

        Assert.Null(request.TargetBranch);
        Assert.False(request.CreateBranch);
    }

    /// <summary>
    /// The commit sequence. A file staged hunk by hunk is in neither path list.
    ///
    /// This is the third staging state, and both halves of it matter. Staging the file would run
    /// <c>git add</c> over it and swallow the hunks the user deliberately left out; unstaging it would
    /// run <c>git restore --staged</c> and discard the ones they deliberately kept. Either way the
    /// feature would appear to work and then commit the wrong thing, which is the failure this test
    /// exists to prevent.
    /// </summary>
    [Fact]
    public void A_file_staged_by_hunk_is_neither_staged_nor_unstaged()
    {
        CommitRequest request = Build(Status(
            File("src/Whole.cs", selected: true),
            File("src/Partial.cs", selected: true, staged: true, chosenHunks: true),
            File("src/Excluded.cs", selected: false, staged: true)));

        //The ticked ordinary file, and only it.
        Assert.Equal(["src/Whole.cs"], request.SelectedPaths);

        //The unticked one comes out of the index; the hunk-staged one is left exactly as it is.
        Assert.Equal(["src/Excluded.cs"], request.PathsToUnstage);
    }

    /// <summary>
    /// Unticking a hunk-staged file still does not unstage it.
    ///
    /// The tick is about "should this be in the commit"; the index already answers that. Letting an
    /// untick reach `restore --staged` would throw the chosen hunks away, which is not what unticking
    /// a row looks like it does.
    /// </summary>
    [Fact]
    public void Unticking_a_hunk_staged_file_does_not_discard_the_hunks()
    {
        CommitRequest request = Build(Status(
            File("src/Partial.cs", selected: false, staged: true, chosenHunks: true)));

        Assert.Empty(request.SelectedPaths);
        Assert.Empty(request.PathsToUnstage);
    }

    /// <summary>
    /// The commit sequence. A file whose deletion is already staged is never handed to `git add`.
    ///
    /// <b>This one fails loudly rather than quietly, which is why it is worth a test of its own.</b>
    /// Pathspec matching looks at the working tree and the index; a file deleted with <c>git rm</c> is
    /// in neither, so <c>git add -- &lt;path&gt;</c> does not silently do nothing — it aborts the
    /// whole command with <c>fatal: pathspec '...' did not match any files</c>, and the commit never
    /// happens.
    ///
    /// The two deletion states are indistinguishable on the row — both show a <c>D</c> — and behave
    /// oppositely:
    ///
    /// <list type="bullet">
    /// <item><description><c>1 .D</c>, gone from the working tree only: <c>git add</c> matches the
    /// surviving index entry and stages the deletion. Must still be staged.</description></item>
    /// <item><description><c>1 D.</c>, deleted with <c>git rm</c>: nothing to match. Must be left
    /// alone, because the index already holds exactly what the user is committing.</description></item>
    /// </list>
    /// </summary>
    [Fact]
    public void A_file_whose_deletion_is_already_staged_is_not_added_again()
    {
        CommitRequest request = Build(Status(
            //Deleted from the working tree only. git add stages the deletion.
            File("src/GoneFromDisk.cs", selected: true, workTree: GitChangeType.Deleted),

            //Already `git rm`-ed. Nothing on disk, nothing in the index.
            File(
                "src/AlreadyRemoved.cs",
                selected: true,
                staged: true,
                workTree: GitChangeType.None,
                index: GitChangeType.Deleted)));

        //Only the first. Including the second is the pathspec failure.
        Assert.Equal(["src/GoneFromDisk.cs"], request.SelectedPaths);

        //And it is not unstaged either: it is ticked, so the user wants the deletion committed, and
        //`restore --staged` would put the file back in the index and undo it.
        Assert.Empty(request.PathsToUnstage);
    }
}
