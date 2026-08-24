using System.Diagnostics;
using System.Text;
using FlickGit.Diagnostics;
using FlickGit.Git;
using FlickGit.History;
using FlickGit.Models;

namespace FlickGit.Ai;

/// <summary>What the model is shown for a pull request. Already capped, filtered and redacted.</summary>
/// <param name="Diff">The payload. Empty when there was nothing sendable.</param>
/// <param name="Subjects">One line per commit, oldest first — the branch's own account of itself.</param>
/// <param name="Files">One line per included file.</param>
/// <param name="Excluded">"package-lock.json (lock file)" — what was held back, and why.</param>
/// <param name="SourceBranch">The branch being proposed.</param>
/// <param name="TargetBranch">Where it is going. Both are context a reviewer would have.</param>
/// <param name="Truncated">True when the model is seeing less than the whole change.</param>
public sealed record PullRequestContext(
    string Diff,
    IReadOnlyList<string> Subjects,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Excluded,
    string SourceBranch,
    string TargetBranch,
    bool Truncated)
{
    public static PullRequestContext Empty { get; } = new(string.Empty, [], [], [], string.Empty, string.Empty, false);

    /// <summary>True when there is nothing worth asking about.</summary>
    public bool IsEmpty => Subjects.Count == 0 && Files.Count == 0;

    /// <summary>
    /// The user message, verbatim.
    ///
    /// <b>The commit subjects come before the diff, and that is the point of this being its own
    /// builder.</b> A commit message is written from a diff because there is nothing else; a branch
    /// has already been described, one commit at a time, by the person who wrote it. Those lines are
    /// the best statement of intent in the payload and the cheapest — so they go first, where a model
    /// reading a truncated diff still has them.
    /// </summary>
    public string ToPromptText()
    {
        var text = new StringBuilder();

        text.Append("Branch: ").Append(SourceBranch).Append(" → ").Append(TargetBranch).Append('\n');

        if (Subjects.Count > 0)
        {
            text.Append("\nCommits, oldest first:\n");

            foreach (string subject in Subjects)
                text.Append("  ").Append(subject).Append('\n');
        }

        if (Files.Count > 0)
        {
            text.Append("\nChanged files:\n");

            foreach (string file in Files)
                text.Append("  ").Append(file).Append('\n');
        }

        if (Excluded.Count > 0)
        {
            //Named rather than silently dropped: without this the model describes a change it was
            //only shown half of, and confidently.
            text.Append("\nNot shown (excluded from the payload):\n");

            foreach (string file in Excluded)
                text.Append("  ").Append(file).Append('\n');
        }

        if (Diff.Length > 0)
            text.Append("\nDiff:\n").Append(Diff);

        if (Truncated)
            text.Append("\nThe diff above is truncated. Describe the intent, not the omissions.\n");

        return text.ToString();
    }
}

/// <summary>
/// Gathers what a pull request would contain, for the model to describe.
///
/// <b>The same exclusion and capping rules as the commit path, by construction.</b> Everything that
/// decides what may leave the machine is <see cref="DiffPayload"/>, which this calls and does not
/// reimplement — so a lock file, a minified bundle or a file matching a secret pattern is held back
/// here for the same reason and by the same code as in a commit message. That is the whole argument
/// for this class being thin: the safety-critical half already exists and is tested, and a second
/// filter beside it would be a second thing to keep right.
///
/// The diff is read against the <b>merge base</b>, which is what a forge shows and what the summary
/// in the window counts. Reading it against the target's tip instead would put every commit made on
/// the target since the branch started into the payload, and the model would faithfully describe
/// somebody else's work as this request's.
/// </summary>
public sealed class PullRequestContextBuilder(IGitProcessRunner git, OperationTimings? timings = null)
{
    public async Task<PullRequestContext> BuildAsync(
        RepositoryInfo repository,
        string mergeBase,
        string sourceBranch,
        string targetBranch,
        IReadOnlyList<LogCommit> commits,
        IReadOnlyList<GitFileChange> changed,
        int maxDiffBytes,
        CancellationToken cancellationToken)
    {
        long startedAt = Stopwatch.GetTimestamp();

        var included = new List<GitFileChange>();
        var excluded = new List<string>();
        var files = new List<string>();

        foreach (GitFileChange file in changed)
        {
            files.Add($"{file.DisplayStatus.ToShortCode()} {file.Path}");

            if (DiffPayload.ExclusionReason(file) is { } reason)
                excluded.Add($"{file.Path} ({reason})");
            else
                included.Add(file);
        }

        //Oldest first, which is the order the work was done in and the order a description reads in.
        //`git log` hands them over newest first.
        var subjects = commits.Reverse().Select(c => c.Subject).Where(s => s.Length > 0).ToList();

        if (files.Count == 0 && subjects.Count == 0)
            return PullRequestContext.Empty;

        //No Git at all when nothing is sendable. The commit subjects alone are still worth a
        //description -- they are what the author already wrote about this branch.
        string unified = included.Count > 0 && mergeBase.Length > 0
            ? await ReadDiffAsync(repository, mergeBase, included, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        DiffPayloadResult payload = DiffPayload.Build(unified, included, maxDiffBytes);

        timings?.Record("ai.pr.payload", Stopwatch.GetElapsedTime(startedAt));

        return new PullRequestContext(
            payload.Text,
            subjects,
            files,
            excluded,
            sourceBranch,
            targetBranch,
            payload.Truncated);
    }

    private async Task<string> ReadDiffAsync(
        RepositoryInfo repository,
        string mergeBase,
        IReadOnlyList<GitFileChange> included,
        CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "diff",
            mergeBase,
            "HEAD",

            //Renames as renames, so a moved file does not read as a delete plus an add.
            "-M",

            //All three are load-bearing against the user's own gitconfig. `color.diff = always`
            //would fill the payload with ANSI escapes; `diff.external` would replace it entirely;
            //a textconv filter would spawn a process per blob.
            "--no-color",
            "--no-ext-diff",
            "--no-textconv",
        };

        //Excluding at the pathspec rather than filtering afterwards, so excluded content never
        //enters this process in the first place.
        args.Add("--");
        args.AddRange(included.Select(f => f.Path));

        GitResult result = await git.ReadAsync(repository.Root, args, cancellationToken).ConfigureAwait(false);

        //A failed diff is not a failed description. The commits and the file list still produce one,
        //which is better than refusing to write anything.
        return result.Succeeded ? result.StdOut : string.Empty;
    }
}
