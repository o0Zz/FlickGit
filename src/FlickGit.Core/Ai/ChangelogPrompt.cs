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
        - write for a user, not for a developer: say what they can now do, or what now works, not
          how it was built
        - never name a class, method, file, flag, API or library; name something technical only
          where the user has to act on it: a setting, a menu entry, a command they type
        - one entry per user-visible change; several commits that add or polish one feature are one
          entry
        - keep each entry to one short line, plain words, no marketing
        - start each entry with a verb in the past tense: Added, Improved, Fixed, Removed
        - describe a fix by what the user no longer runs into, not by what the code does now
        - treat a commit type as a hint: ci, docs, test, build, chore and style are never entries,
          and a refactor is one only when its subject names an effect the user would notice
        - leave out anything with no user-visible effect: refactoring, logging, formatting,
          test-only changes, and dependency bumps that change nothing
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
            "Style: minimal. A bulleted list of what is new or improved, then a `### Fixes` heading "
            + "over a bulleted list of the fixes. One short line per entry, no other headings and no "
            + "explanation. Leave out the Fixes heading when there are no fixes.",

        _ =>
            "Style: full. Group the entries under `### Added`, `### Improved`, `### Fixed` and "
            + "`### Removed`, leaving out a heading with nothing under it, and give each entry a "
            + "sentence saying what it means for somebody using the software.",
    };
}
