using System.Text;
using FlickGit.Logging;

namespace FlickGit.Ai;

/// <param name="Text">
/// The system prompt to send. Never empty -- the built-in stands in whenever there is no usable
/// file, so no caller has to check.
/// </param>
/// <param name="Source">The file it came from, or null when the built-in is in use.</param>
/// <param name="Error">
/// Why a file that exists was not used. Null when there was nothing wrong, including when there is
/// simply no file: deleting one is how a user goes back to the built-in, and that is not a fault.
/// </param>
public readonly record struct ResolvedPrompt(string Text, string? Source, string? Error);

/// <summary>
/// The three system prompts, as files the user can edit.
///
/// <b>Only the system prompt.</b> The other half of a request -- <see cref="AiContext.ToPromptText"/>,
/// fed by <see cref="DiffPayload"/> -- is the safety-critical half: it is what decides what may leave
/// the machine, and it is capped, path-filtered and redacted. Nothing here can widen it. A prompt
/// file changes the instructions, never the payload.
///
/// Shaped like <see cref="Actions.ActionCatalog"/>, and for the same reasons: the path is passed in
/// rather than derived, because <c>FlickGit.Core</c> deliberately knows nothing about where a user
/// profile is; and a file that cannot be read leaves the built-in working rather than throwing.
/// Unlike that one it needs no JSON context, because a prompt is text.
/// </summary>
public sealed class PromptStore(string directoryPath, ILog log)
{
    public const string CommitFileName = "commit-prompt.md";
    public const string PullRequestFileName = "pull-request-prompt.md";
    public const string ChangelogFileName = "changelog-prompt.md";

    public string CommitFilePath { get; } = Path.Combine(directoryPath, CommitFileName);

    public string PullRequestFilePath { get; } = Path.Combine(directoryPath, PullRequestFileName);

    public string ChangelogFilePath { get; } = Path.Combine(directoryPath, ChangelogFileName);

    /// <summary>
    /// The commit-message prompt.
    ///
    /// <paramref name="conventionalCommits"/> chooses between the two built-in variants and is
    /// <b>not</b> consulted when a file is in use: that file is the whole prompt, and appending a
    /// rule the user did not write to a prompt they thought was final is the kind of surprise this
    /// feature exists to remove. `flick ai` says so when both are set.
    /// </summary>
    public ResolvedPrompt ForCommit(bool conventionalCommits) =>
        Resolve(CommitFilePath, CommitFileName, CommitPrompt.For(conventionalCommits));

    public ResolvedPrompt ForPullRequest() =>
        Resolve(PullRequestFilePath, PullRequestFileName, PullRequestPrompt.System);

    /// <summary>
    /// The changelog prompt.
    ///
    /// It takes no argument, unlike <see cref="ForCommit"/>, and that is deliberate rather than a
    /// gap: the Brief-or-Detailed choice belongs to the payload, so this file is the whole prompt
    /// whatever the window's box says. See <see cref="ChangelogPrompt"/>.
    /// </summary>
    public ResolvedPrompt ForChangelog() =>
        Resolve(ChangelogFilePath, ChangelogFileName, ChangelogPrompt.System);

    /// <summary>
    /// Writes either file that is not there yet, seeded with the built-in prompt.
    ///
    /// Called once at startup. A user cannot edit a file they have never seen, and the seeded header
    /// is the only place that can explain the rules -- that the whole file is the prompt, that
    /// deleting it reverts, and what FlickGit appends underneath.
    ///
    /// Runs on every launch, not only the first, because "missing" is the only signal there is and
    /// a marker file would be state nobody asked for. The consequence is worth being plain about,
    /// and the header says it: deleting a file resets it to the built-in <i>wording</i> -- the file
    /// comes back holding it -- rather than unbinding the install from the file. So a later
    /// FlickGit that improves its built-in prompt does not reach an install that already has one.
    /// </summary>
    public void SeedMissingFiles(bool conventionalCommits)
    {
        Seed(CommitFilePath, CommitHeader, CommitPrompt.For(conventionalCommits));
        Seed(PullRequestFilePath, PullRequestHeader, PullRequestPrompt.System);
        Seed(ChangelogFilePath, ChangelogHeader, ChangelogPrompt.System);
    }

    /// <summary>
    /// The file, or the built-in. Never throws, and never returns empty text.
    ///
    /// Read on every generation rather than cached at startup, which is a deliberate difference from
    /// <see cref="Actions.ActionCatalog"/>: iterating on wording is the whole point of the feature,
    /// and a resident service that had to be restarted between attempts would make it unusable. A
    /// kilobyte read costs microseconds on a path that already costs hundreds of milliseconds.
    /// </summary>
    private ResolvedPrompt Resolve(string path, string fileName, string builtIn)
    {
        try
        {
            if (!File.Exists(path))
                return new ResolvedPrompt(builtIn, null, null);

            string text = StripComments(File.ReadAllText(path)).Trim();

            //Blank, or nothing but comments. An empty system prompt does not fail, it produces
            //confident nonsense -- so it is a fallback that says why, not silence.
            return text.Length == 0
                ? Fallback(builtIn, $"{fileName} has no prompt in it, so the built-in prompt is in use")
                : new ResolvedPrompt(text, path, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fallback(builtIn, $"{fileName} could not be read, so the built-in prompt is in use: {ex.Message}");
        }
    }

    private ResolvedPrompt Fallback(string builtIn, string error)
    {
        log.Warn(error);

        return new ResolvedPrompt(builtIn, null, error);
    }

    private void Seed(string path, string header, string builtIn)
    {
        try
        {
            if (File.Exists(path))
                return;

            Directory.CreateDirectory(directoryPath);
            File.WriteAllText(path, $"{header}\n\n{builtIn.Trim()}\n");

            log.Info($"Wrote {Path.GetFileName(path)} with the built-in prompt.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            //Never fatal. A file that could not be written costs the user something to edit, not the
            //feature: Resolve falls back to the same built-in text this would have contained.
            log.Warn($"{Path.GetFileName(path)} could not be written: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes every HTML comment, so a prompt file can carry notes to its reader without sending
    /// them to the model.
    ///
    /// The one piece of syntax in a file that is otherwise sent verbatim, and it is here because the
    /// seeded header needs somewhere to live. Markdown-native, and invisible when the file is
    /// rendered. An unterminated <c>&lt;!--</c> takes the rest of the file: someone commenting out
    /// the tail of a prompt and forgetting to close it meant to remove it, not to send the marker.
    /// </summary>
    internal static string StripComments(string markdown)
    {
        const string open = "<!--";
        const string close = "-->";

        int start = markdown.IndexOf(open, StringComparison.Ordinal);

        if (start < 0)
            return markdown;

        var text = new StringBuilder(markdown.Length);
        int cursor = 0;

        while (start >= 0)
        {
            text.Append(markdown, cursor, start - cursor);

            int end = markdown.IndexOf(close, start + open.Length, StringComparison.Ordinal);

            if (end < 0)
                return text.ToString();

            cursor = end + close.Length;
            start = markdown.IndexOf(open, cursor, StringComparison.Ordinal);
        }

        text.Append(markdown, cursor, markdown.Length - cursor);

        return text.ToString();
    }

    /// <summary>
    /// What a seeded file says above the prompt. Four rules and one paragraph of context -- the last
    /// one is not decoration: without knowing what FlickGit appends underneath, a user cannot write
    /// a prompt that makes sense.
    /// </summary>
    private const string CommitHeader = """
        <!--
        This is the prompt FlickGit sends when it writes a commit message for you. The text below
        is FlickGit's built-in prompt, copied here so you can change it. Save the file and the next
        message uses it -- there is nothing to restart.

          - The whole file is the prompt, sent verbatim. HTML comments are removed first, so
            anything written inside one is for you rather than for the model.
          - Delete this file to start over: FlickGit writes it again, with the built-in prompt,
            the next time it runs. It always prefers this file to its own built-in wording, so
            a later version that improves that wording will not change what you see here.
          - While this file exists, the `aiConventionalCommits` setting in settings.json is not
            consulted. Say what you want here instead.
          - An empty file is refused, and the built-in prompt is used instead.

        FlickGit appends this underneath, and it cannot be changed here: the branch name, the list
        of files you are committing, the list of files held back and why, and the diff of those
        files -- capped in size, with lock files and generated code excluded and anything matching
        a secret pattern redacted.
        -->
        """;

    private const string PullRequestHeader = """
        <!--
        This is the prompt FlickGit sends when it writes a pull request title and description. The
        text below is FlickGit's built-in prompt, copied here so you can change it. Save the file
        and the next description uses it -- there is nothing to restart.

          - The whole file is the prompt, sent verbatim. HTML comments are removed first, so
            anything written inside one is for you rather than for the model.
          - Delete this file to start over: FlickGit writes it again, with the built-in prompt,
            the next time it runs. It always prefers this file to its own built-in wording, so
            a later version that improves that wording will not change what you see here.
          - An empty file is refused, and the built-in prompt is used instead.
          - Keep the first-line-is-the-title rule. FlickGit splits the answer on it to fill the two
            boxes, so a prompt that asks for JSON or a `Title:` label puts that text in the title.

        FlickGit appends this underneath, and it cannot be changed here: the source and target
        branches, the commit subjects oldest first, the changed files, the files held back and why,
        and the diff against the merge base -- capped in size and redacted.
        -->
        """;

    private const string ChangelogHeader = """
        <!--
        This is the prompt FlickGit sends when it writes a changelog for the commits you selected in
        the log window. The text below is FlickGit's built-in prompt, copied here so you can change
        it. Save the file and the next changelog uses it -- there is nothing to restart.

          - The whole file is the prompt, sent verbatim. HTML comments are removed first, so
            anything written inside one is for you rather than for the model.
          - Delete this file to start over: FlickGit writes it again, with the built-in prompt,
            the next time it runs. It always prefers this file to its own built-in wording, so
            a later version that improves that wording will not change what you see here.
          - An empty file is refused, and the built-in prompt is used instead.
          - Do not put the length in here. Brief and Detailed are chosen in the window, and they
            reach the model as the last line of what is appended below -- so a rule about length
            written here fights the box rather than replacing it.

        FlickGit appends this underneath, and it cannot be changed here: the subjects of the commits
        you selected, oldest first, the files they changed, the files held back and why, the diff
        over that range -- capped in size and redacted -- and the Brief or Detailed line.
        -->
        """;
}
