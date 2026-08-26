using System.Diagnostics;
using System.Text;
using FlickGit.Diagnostics;
using FlickGit.Git;
using FlickGit.History;
using FlickGit.Models;

namespace FlickGit.Ai;

/// <summary>What the model is shown. Already capped, filtered and redacted.</summary>
/// <param name="Heading">
/// The first line: <c>Branch: main</c> for a commit, <c>Branch: feature/x → main</c> for a pull
/// request. Empty when there is no branch to name, which is the unborn-HEAD case.
/// </param>
/// <param name="Subjects">
/// One line per commit, oldest first — the branch's own account of itself. Empty for a commit
/// message, which is being written precisely because nobody has described the change yet.
///
/// <b>Before the diff, and that is the point of the pull-request payload.</b> A commit message is
/// written from a diff because there is nothing else; a branch has already been described, one
/// commit at a time, by the person who wrote it. Those lines are the best statement of intent
/// available and the cheapest — so a model reading a truncated diff still has them.
/// </param>
/// <param name="Files">One line per included file, for the prompt's file list.</param>
/// <param name="Excluded">"package-lock.json (lock file)" — what was held back, and why.</param>
/// <param name="Diff">The payload. Empty when there was nothing sendable.</param>
/// <param name="Truncated">True when the model is seeing less than the whole change.</param>
/// <param name="TruncationVerb">
/// What the closing instruction asks for: <c>Summarise</c> for a commit message,
/// <c>Describe</c> for a pull-request description. One word, so it is a field rather than a second
/// <c>ToPromptText</c>.
/// </param>
/// <param name="Instruction">
/// One line appended last, saying something about <i>this request</i> rather than about the task --
/// today, the changelog's Brief-or-Detailed choice. Empty for the surfaces with nothing to choose.
///
/// <b>Here rather than in the system prompt</b>, because the system prompt is a file the user owns:
/// see <see cref="ChangelogPrompt"/>. It cannot widen what may leave the machine, being one sentence
/// of English appended to a payload that was already capped, filtered and redacted above it.
/// </param>
public sealed record AiContext(
    string Heading,
    IReadOnlyList<string> Subjects,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Excluded,
    string Diff,
    bool Truncated,
    string TruncationVerb,
    string Instruction)
{
    public static AiContext Empty { get; } = new(string.Empty, [], [], [], string.Empty, false, "Summarise", string.Empty);

    /// <summary>True when there is nothing worth asking about.</summary>
    public bool IsEmpty => Files.Count == 0 && Subjects.Count == 0;

    /// <summary>
    /// The user message, verbatim.
    ///
    /// Assembled here rather than in each provider, so no two can disagree about what the model was
    /// shown — which would make one provider's messages inexplicably better than another's.
    /// </summary>
    public string ToPromptText()
    {
        var text = new StringBuilder();

        if (Heading.Length > 0)
            text.Append(Heading).Append('\n');

        Section(text, "Commits, oldest first:", Subjects);
        Section(text, "Changed files:", Files);

        //Named rather than silently dropped: without this the model describes a change it was
        //only shown half of, and confidently.
        Section(text, "Not shown (excluded from the payload):", Excluded);

        if (Diff.Length > 0)
            text.Append("\nDiff:\n").Append(Diff);

        if (Truncated)
            text.Append($"\nThe diff above is truncated. {TruncationVerb} the intent, not the omissions.\n");

        //Last, where a trailing instruction carries the most weight and cannot be read as part of
        //the diff above it.
        if (Instruction.Length > 0)
            text.Append('\n').Append(Instruction).Append('\n');

        return text.ToString();
    }

    private static void Section(StringBuilder text, string title, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
            return;

        text.Append('\n').Append(title).Append('\n');

        foreach (string line in lines)
            text.Append("  ").Append(line).Append('\n');
    }
}

/// <summary>
/// Gathers the diff the model is shown, for both surfaces that ask for one.
///
/// <b>One builder, because the two questions differ only in their revisions.</b> There were two —
/// one per surface — and they agreed on the include/exclude loop, the <see cref="DiffPayload"/>
/// call, the four <c>--no-*</c> flags, the pathspec and the prompt skeleton. Everything that decides
/// <i>what may leave the machine</i> is the safety-critical half of this product, and two copies of
/// it is one copy too many: a rule tightened in one and not the other is a leak that compiles.
///
/// What genuinely differs is the revision arguments, and they are a parameter:
///
/// <list type="bullet">
/// <item><description><b>A commit is <c>diff HEAD</c>, not <c>diff --cached</c>.</b> CLAUDE.md says
/// to prefer <c>--cached</c> "because it represents the upcoming commit", and in most tools it
/// would. It does not here: <see cref="Commits.CommitFlow"/> stages as its <i>first</i> step, at
/// commit time, so when the window wants a message the index is usually empty and <c>--cached</c>
/// would return nothing at all. Staging early to make it true is worse — pressing Esc would then
/// leave the index mutated, which is exactly the silent change to the user's repository the Safety
/// Rules forbid.</description></item>
/// <item><description><b>A pull request is <c>diff &lt;merge base&gt; HEAD</c></b>, which is what a
/// forge shows. Against the target's tip it would put every commit made on the target since the
/// branch started into the payload, and the model would faithfully describe somebody else's
/// work.</description></item>
/// </list>
/// </summary>
public sealed class AiContextBuilder(IGitProcessRunner git, OperationTimings? timings = null)
{
    /// <summary>What the upcoming commit will contain: the ticked files, against HEAD.</summary>
    public Task<AiContext> ForCommitAsync(
        RepositoryInfo repository,
        RepositoryStatus status,
        int maxDiffBytes,
        CancellationToken cancellationToken) =>
        BuildAsync(
            repository,
            [.. status.Files.Where(f => f.IsSelected)],
            subjects: [],
            heading: status.Branch is { Length: > 0 } branch ? $"Branch: {branch}" : string.Empty,

            //An unborn HEAD is not a revision, so there is nothing to diff against.
            revisions: status.IsUnborn ? null : ["HEAD"],
            truncationVerb: "Summarise",
            instruction: string.Empty,
            timingKey: "ai.payload",
            maxDiffBytes,
            cancellationToken);

    /// <summary>What the pull request will contain: its files and its commits, against the merge base.</summary>
    /// <param name="mergeBase">
    /// Where the branch parted from its target. Empty when the target is not known locally, which
    /// produces a description from the commit subjects alone rather than no description.
    /// </param>
    public Task<AiContext> ForPullRequestAsync(
        RepositoryInfo repository,
        string mergeBase,
        string sourceBranch,
        string targetBranch,
        IReadOnlyList<LogCommit> commits,
        IReadOnlyList<GitFileChange> changed,
        int maxDiffBytes,
        CancellationToken cancellationToken) =>
        BuildAsync(
            repository,
            changed,

            //Oldest first, which is the order the work was done in and the order a description reads
            //in. `git log` hands them over newest first.
            subjects: [.. commits.Reverse().Select(c => c.Subject).Where(s => s.Length > 0)],
            heading: $"Branch: {sourceBranch} → {targetBranch}",
            revisions: mergeBase.Length > 0 ? [mergeBase, "HEAD"] : null,
            truncationVerb: "Describe",
            instruction: string.Empty,
            timingKey: "ai.pr.payload",
            maxDiffBytes,
            cancellationToken);

    /// <summary>
    /// What a range of commits changed: the range's own commits and files, against its base.
    ///
    /// The third caller, and the one that justifies the second sentence of this class's summary --
    /// it differs from the other two in its revisions and in one line of instruction, and in nothing
    /// that decides what may leave the machine.
    /// </summary>
    /// <param name="baseSpec">
    /// The left side of the range: <see cref="History.CommitRange.BaseSpec"/>, which is a bare object
    /// id and is the empty tree when the oldest commit is the repository's first. Passed through
    /// rather than re-derived, so the changelog describes exactly the range the diff and the patch do.
    /// </param>
    /// <param name="commits">
    /// The commits the range spans, newest first -- gaps included, because they are in the diff.
    /// </param>
    public Task<AiContext> ForChangelogAsync(
        RepositoryInfo repository,
        string baseSpec,
        string tipSpec,
        IReadOnlyList<LogCommit> commits,
        IReadOnlyList<GitFileChange> changed,
        ChangelogStyle style,
        int maxDiffBytes,
        CancellationToken cancellationToken) =>
        BuildAsync(
            repository,
            changed,
            subjects: [.. commits.Reverse().Select(c => c.Subject).Where(s => s.Length > 0)],

            //No heading, where the other two name a branch. The branch name and the two hashes are
            //precisely what the prompt asks the model to keep out of a changelog, and the cheapest way
            //to keep them out of the answer is to keep them out of the question.
            heading: string.Empty,
            revisions: [baseSpec, tipSpec],
            truncationVerb: "Describe",
            instruction: ChangelogPrompt.Instruction(style),
            timingKey: "ai.changelog.payload",
            maxDiffBytes,
            cancellationToken);

    /// <param name="revisions">
    /// What <c>git diff</c> is given before the pathspec, or null when there is nothing to diff
    /// against at all — an unborn HEAD, or a target this machine does not have.
    /// </param>
    private async Task<AiContext> BuildAsync(
        RepositoryInfo repository,
        IReadOnlyList<GitFileChange> changed,
        IReadOnlyList<string> subjects,
        string heading,
        IReadOnlyList<string>? revisions,
        string truncationVerb,
        string instruction,
        string timingKey,
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

        if (files.Count == 0 && subjects.Count == 0)
            return AiContext.Empty;

        //No Git at all when nothing is sendable. The file list and the commit subjects are still
        //worth a message on their own -- "add three untracked files" is a perfectly good subject,
        //and the commits are what the author already wrote about this branch.
        string unified = included.Count > 0 && revisions is not null
            ? await ReadDiffAsync(repository, revisions, included, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        DiffPayloadResult payload = DiffPayload.Build(unified, included, maxDiffBytes);

        timings?.Record(timingKey, Stopwatch.GetElapsedTime(startedAt));

        return new AiContext(heading, subjects, files, excluded, payload.Text, payload.Truncated, truncationVerb, instruction);
    }

    private async Task<string> ReadDiffAsync(
        RepositoryInfo repository,
        IReadOnlyList<string> revisions,
        IReadOnlyList<GitFileChange> included,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "diff" };

        args.AddRange(revisions);

        //Renames as renames, so a moved file does not read as a delete plus an add.
        args.Add("-M");

        //All three are load-bearing against the user's own gitconfig -- see GitDiffFlags. On this
        //path a textconv filter would also spawn a process per blob, which the AI first-token budget
        //cannot absorb.
        args.AddRange(GitDiffFlags.ReadSafe);

        //Excluding at the pathspec rather than filtering afterwards, so excluded content never
        //enters this process in the first place.
        args.Add("--");
        args.AddRange(included.Select(f => f.Path));

        GitResult result = await git.ReadAsync(repository.Root, args, cancellationToken).ConfigureAwait(false);

        //A failed diff is not a failed commit, or a failed description. An empty payload still
        //produces a message from the file list, which is better than refusing to write one.
        return result.Succeeded ? result.StdOut : string.Empty;
    }
}
