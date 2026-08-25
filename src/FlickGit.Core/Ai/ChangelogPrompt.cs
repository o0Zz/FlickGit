namespace FlickGit.Ai;

/// <summary>
/// How much of a changelog to write.
///
/// Two registers rather than a number, because they are two documents: one is the list somebody
/// scans in the release notes of a patch build, the other is what goes on a "what's new" page. A
/// slider between them would be a question nobody can answer, and a setting for it would be one
/// nobody asked for -- so it is two words in a box, chosen per changelog and remembered nowhere.
/// </summary>
public enum ChangelogStyle
{
    /// <summary>One short line per change, and nothing else.</summary>
    Brief,

    /// <summary>Grouped, with a sentence per entry saying what it means for the user.</summary>
    Detailed,
}

/// <summary>
/// The system prompt for a changelog, and the one line that chooses its length.
///
/// <b>The style is not part of the system prompt, and that is the shape of this whole file.</b>
/// <see cref="PromptStore"/> lets the user replace the prompt with a file of their own, and while
/// such a file exists it is sent verbatim -- so a style rule living in the prompt would silently
/// stop working the moment anybody edited it, which is exactly the trap <c>aiConventionalCommits</c>
/// is documented as rather than a second instance of it. The style is a line of the <i>payload</i>
/// instead, where it reads as what it is: an instruction about this request, not a rule about
/// changelogs. A user's own prompt keeps working, and the two words in the box keep meaning
/// something.
/// </summary>
public static class ChangelogPrompt
{
    /// <summary>
    /// The built-in prompt.
    ///
    /// <b>Written for the reader of the software, not the reader of the diff.</b> That is the one
    /// thing separating it from <see cref="PullRequestPrompt"/>: a description is read by a reviewer
    /// who is about to look at the code, and a changelog by somebody who never will. So most of the
    /// rules are about what to leave out -- a refactor, a test, a dependency bump -- because a model
    /// shown a diff will otherwise report all three, accurately and uselessly.
    /// </summary>
    public const string System = """
        Given the commits and diff of a range of work, write a changelog for the people who use this
        software.

        Rules:
        - write for a user, not for a developer: say what changed for them, not how it was built
        - name something technical only where the user has to act on it: a setting, a file, a command
        - one entry per user-visible change; several commits that add one feature are one entry
        - leave out anything with no user-visible effect: refactoring, formatting, test-only changes,
          and dependency bumps that change nothing
        - group entries under Added, Changed, Fixed and Removed when there is more than one kind
        - start each entry with a verb in the present tense
        - do not invent changes, version numbers, dates, issue numbers or links
        - say nothing about a change you were not shown
        - output only the changelog, in Markdown, with no preamble and no code fences
        """;

    /// <summary>
    /// The line appended to the payload, last -- where a trailing instruction carries the most
    /// weight, and where it cannot be mistaken for part of the diff above it.
    /// </summary>
    public static string Instruction(ChangelogStyle style) => style switch
    {
        ChangelogStyle.Brief =>
            "Style: minimal. A flat bulleted list, one short line per change, with no headings and "
            + "no explanation -- fixes this, adds that.",

        _ =>
            "Style: full. Group the entries under headings, and give each one a sentence saying what "
            + "it means for somebody using the software.",
    };
}
