using FlickGit.Commits;
using FlickGit.Models;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Turning a status and its ticks into the two path lists <see cref="CommitFlow"/> acts on.
///
/// In scope under Hard Requirement 4 as <b>the safety rules</b> and <b>the commit sequence</b>.
/// There are two commit surfaces now — the commit window and the quick-commit popup — and both
/// derive their request here, so this is the one place that decides what gets staged and what comes
/// back out of the index. Getting <c>PathsToUnstage</c> wrong commits a file the user deliberately
/// unticked, because `git commit` commits the index and not the selection.
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
        GitChangeType workTree = GitChangeType.Modified) =>
        new()
        {
            Path = path,
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
}