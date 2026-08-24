namespace FlickGit.Ai;

/// <summary>
/// The system prompt for a pull-request description, and the split that turns one answer into a
/// title and a body.
///
/// Separate from <see cref="CommitPrompt"/> because it is a different task with a different shape of
/// answer, not a longer version of the same one: a commit message summarises what changed, and a
/// description tells a reviewer what to look at and why. Sharing a prompt would mean one of the two
/// getting the wrong instructions.
/// </summary>
public static class PullRequestPrompt
{
    /// <summary>
    /// <b>One answer, two fields.</b> The alternative was two requests, which doubles the latency and
    /// the cost to have a model read the same diff twice — and risks a title that describes something
    /// the body does not.
    ///
    /// The format is the one a commit message already uses, so the parsing rule is the one Git itself
    /// uses: first line, blank line, rest. No JSON, no <c>Title:</c> label — both are things a model
    /// wraps in a code fence, and both would show up in the box verbatim when it did.
    /// </summary>
    public const string System = """
        Given the commits and diff of a branch, write a pull request title and description for a
        reviewer.

        Format:
        - the first line is the title, and nothing else is on it
        - then one blank line
        - then the description, in Markdown

        Rules:
        - the title is a summary of the whole branch, <= 72 characters when practical, imperative mood
        - the description says what changed and why, not how every line changed
        - use short paragraphs, or a bulleted list when there are separable changes
        - mention anything a reviewer should look at first, or test by hand
        - do not invent changes, issue numbers, links or test results
        - do not repeat the branch name or the commit list back
        - output only the title and description, with no preamble and no code fences
        """;

    /// <summary>
    /// Splits the answer into a title and a body.
    ///
    /// Fences are stripped first, by <see cref="CommitPrompt.Clean"/> — a model that wrapped the
    /// whole answer in one would otherwise put <c>```markdown</c> in the title box.
    ///
    /// Tolerant about the blank line, on purpose: a model that omits it has still put the title on
    /// its own first line, and refusing that would throw away a good answer over whitespace. Only
    /// the leading <c>#</c> of a Markdown heading is removed, because a model asked for a title
    /// sometimes writes one.
    /// </summary>
    public static (string Title, string Body) Split(string answer)
    {
        string text = CommitPrompt.Clean(answer);

        if (text.Length == 0)
            return (string.Empty, string.Empty);

        int firstBreak = text.IndexOf('\n');

        if (firstBreak < 0)
            return (Heading(text), string.Empty);

        string title = Heading(text[..firstBreak].TrimEnd('\r'));
        string body = text[(firstBreak + 1)..].TrimStart('\n', '\r');

        return (title, body.Trim());
    }

    /// <summary>The line without a Markdown heading marker, and without the emphasis around it.</summary>
    private static string Heading(string line)
    {
        string text = line.Trim();

        while (text.StartsWith('#'))
            text = text[1..].TrimStart();

        //A model told to write a title occasionally bolds it. Stripped symmetrically, so a title
        //that genuinely contains ** in the middle is left alone.
        if (text.Length > 4 && text.StartsWith("**", StringComparison.Ordinal) && text.EndsWith("**", StringComparison.Ordinal))
            text = text[2..^2].Trim();

        return text;
    }
}
