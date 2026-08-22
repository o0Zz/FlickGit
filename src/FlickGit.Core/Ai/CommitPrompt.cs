namespace FlickGit.Ai;

/// <summary>
/// The system prompt, and the fence stripping that defends against it being ignored.
///
/// CLAUDE.md: "The real control is the system prompt: the message and nothing else — no preamble,
/// no code fences, no explanation. Strip fences defensively anyway."
/// </summary>
public static class CommitPrompt
{
    /// <summary>The prompt from CLAUDE.md, verbatim.</summary>
    private const string Base = """
        Given the following Git diff, produce a concise commit message.

        Rules:
        - summarise the intent, not every changed line
        - first line <= 72 characters when practical
        - imperative mood
        - do not invent changes
        - output only the commit message
        """;

    /// <summary>
    /// With Conventional Commits left to the model's judgement, which is CLAUDE.md's default
    /// wording: "use Conventional Commits when clearly appropriate".
    /// </summary>
    public static string Optional => Base + "\n- use Conventional Commits when clearly appropriate\n";

    /// <summary>With Conventional Commits required, for a repository whose history uses them.</summary>
    public static string Required =>
        Base + "\n- always use Conventional Commits: feat, fix, chore, refactor, docs, test, perf\n";

    public static string For(bool conventionalCommits) => conventionalCommits ? Required : Optional;

    /// <summary>
    /// Removes a Markdown code fence a model wrapped the message in.
    ///
    /// Applied to the finished text rather than to each token: a fence cannot be recognised from a
    /// fragment, and holding tokens back to look for one would defeat the streaming this feature
    /// exists to have.
    /// </summary>
    public static string Clean(string message)
    {
        string text = message.Trim();

        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        //Drop the opening fence line, including any language tag on it.
        int firstBreak = text.IndexOf('\n');

        if (firstBreak < 0)
            return string.Empty;

        text = text[(firstBreak + 1)..];

        int closing = text.LastIndexOf("```", StringComparison.Ordinal);

        return (closing >= 0 ? text[..closing] : text).Trim();
    }
}
