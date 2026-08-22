using FlickGit.Models;

namespace FlickGit.Git;

/// <summary>
/// A Git command failed, carrying everything CLAUDE.md, "Error Handling" requires the
/// user to be shown: what was being attempted, which repository, Git's own words, and
/// what to do next.
///
/// "Never show generic errors such as 'Something went wrong.'" The way to guarantee that
/// is to make it impossible to construct this exception without Git's stderr, which is
/// why <see cref="GitResult"/> is a required argument rather than an optional detail.
/// </summary>
public sealed class GitOperationException(
    string operation,
    string repositoryPath,
    GitResult result,
    string? suggestion = null)
    : Exception(BuildMessage(operation, repositoryPath, result, suggestion))
{
    /// <summary>What was being attempted, in the user's words: "Commit", "Switch branch".</summary>
    public string Operation { get; } = operation;

    public string RepositoryPath { get; } = repositoryPath;

    public GitResult Result { get; } = result;

    /// <summary>The next action, when there is a specific one. Shown as its own paragraph.</summary>
    public string? Suggestion { get; } = suggestion;

    /// <summary>Git's stderr, unedited. This is what the user needs and what a paraphrase loses.</summary>
    public string GitError => Result.ErrorText;

    private static string BuildMessage(
        string operation,
        string repositoryPath,
        GitResult result,
        string? suggestion)
    {
        string message = $"{operation} failed in {repositoryPath}.\n\n{result.ErrorText}";
        return suggestion is { Length: > 0 } ? $"{message}\n\n{suggestion}" : message;
    }
}
