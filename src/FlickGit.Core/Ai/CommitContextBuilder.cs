using System.Diagnostics;
using FlickGit.Diagnostics;
using FlickGit.Git;
using FlickGit.Models;

namespace FlickGit.Ai;

/// <summary>What the model is shown. Already capped, filtered and redacted.</summary>
/// <param name="Diff">The payload. Empty when there was nothing sendable.</param>
/// <param name="Files">One line per included file, for the prompt's file list.</param>
/// <param name="Excluded">"package-lock.json (lock file)" — what was held back, and why.</param>
/// <param name="Branch">The branch, when there is one. Useful context, never required.</param>
/// <param name="Truncated">True when the model is seeing less than the whole change.</param>
public sealed record CommitContext(
    string Diff,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Excluded,
    string? Branch,
    bool Truncated)
{
    public static CommitContext Empty { get; } = new(string.Empty, [], [], null, false);

    /// <summary>True when there is nothing worth asking about.</summary>
    public bool IsEmpty => Files.Count == 0;

    /// <summary>
    /// The user message, verbatim.
    ///
    /// Assembled here rather than in each provider, so the two cannot disagree about what the model
    /// was shown — which would make one provider's messages inexplicably better than the other's.
    /// </summary>
    public string ToPromptText()
    {
        var text = new System.Text.StringBuilder();

        if (Branch is { Length: > 0 })
            text.Append("Branch: ").Append(Branch).Append('\n');

        text.Append("Changed files:\n");

        foreach (string file in Files)
            text.Append("  ").Append(file).Append('\n');

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
            text.Append("\nThe diff above is truncated. Summarise the intent, not the omissions.\n");

        return text.ToString();
    }
}

/// <summary>
/// Gathers the diff the upcoming commit will contain.
///
/// <b>`git diff HEAD`, not `git diff --cached`.</b> CLAUDE.md says to prefer <c>--cached</c>
/// "because it represents the upcoming commit", and in most tools it would. It does not here:
/// <see cref="Commits.CommitFlow"/> stages as its <i>first</i> step, at commit time, so when the
/// popup wants a message the index is usually empty and <c>--cached</c> would return nothing at all.
/// <c>diff HEAD</c> over the ticked paths is what the commit will actually contain.
///
/// Staging early to make <c>--cached</c> true was the other option and is worse: pressing Esc would
/// then leave the index mutated, which is exactly the silent change to the user's repository the
/// Safety Rules forbid.
/// </summary>
public sealed class CommitContextBuilder(IGitProcessRunner git, OperationTimings? timings = null)
{
    public async Task<CommitContext> BuildAsync(
        RepositoryInfo repository,
        RepositoryStatus status,
        int maxDiffBytes,
        CancellationToken cancellationToken)
    {
        long startedAt = Stopwatch.GetTimestamp();

        var included = new List<GitFileChange>();
        var excluded = new List<string>();
        var files = new List<string>();

        foreach (GitFileChange file in status.Files.Where(f => f.IsSelected))
        {
            files.Add($"{file.DisplayStatus.ToShortCode()} {file.Path}");

            if (DiffPayload.ExclusionReason(file) is { } reason)
                excluded.Add($"{file.Path} ({reason})");
            else
                included.Add(file);
        }

        if (files.Count == 0)
            return CommitContext.Empty;

        //No Git at all when nothing is sendable. The file list alone is still worth a message --
        //"add three untracked files" is a perfectly good commit subject.
        string unified = included.Count > 0 && !status.IsUnborn
            ? await ReadDiffAsync(repository, included, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        DiffPayloadResult payload = DiffPayload.Build(unified, included, maxDiffBytes);

        timings?.Record("ai.payload", Stopwatch.GetElapsedTime(startedAt));

        return new CommitContext(payload.Text, files, excluded, status.Branch, payload.Truncated);
    }

    private async Task<string> ReadDiffAsync(
        RepositoryInfo repository,
        IReadOnlyList<GitFileChange> included,
        CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "diff",
            "HEAD",

            //Renames as renames, so a moved file does not read as a delete plus an add.
            "-M",

            //All three are load-bearing against the user's own gitconfig. `color.diff = always`
            //would fill the payload with ANSI escapes; `diff.external` would replace it entirely;
            //a textconv filter would spawn a process per blob on a latency-critical path.
            "--no-color",
            "--no-ext-diff",
            "--no-textconv",
        };

        //Excluding at the pathspec rather than filtering afterwards, so excluded content never
        //enters this process in the first place.
        args.Add("--");
        args.AddRange(included.Select(f => f.Path));

        GitResult result = await git.ReadAsync(repository.Root, args, cancellationToken).ConfigureAwait(false);

        //A failed diff is not a failed commit. An empty payload still produces a message from the
        //file list, which is better than refusing to write one.
        return result.Succeeded ? result.StdOut : string.Empty;
    }
}
