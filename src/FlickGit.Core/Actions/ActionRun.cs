using FlickGit.Cli;

namespace FlickGit.Actions;

/// <summary>
/// What an action actually does. Four kinds, and no fifth.
///
/// CLAUDE.md, "Action Catalog": <c>WindowRun</c> opens a FlickGit window, <c>GitRun</c> runs Git with
/// an argument list, <c>ProcessRun</c> runs an external executable, and <c>CompositeRun</c> is an
/// ordered sequence that stops at the first failure.
///
/// Every variant carries its arguments as a <i>list</i>, never as a command string. That is the whole
/// reason this type exists rather than a single "command line" field: user actions are the one place
/// in the product where the argument text comes from outside the code, and a concatenated string is
/// where a repository path containing a quote turns into an injected argument.
/// </summary>
public abstract record ActionRun
{
    /// <summary>
    /// The command this would run, spelled out for a human.
    ///
    /// Read by the palette's footer before Enter and by the confirmation dialog before anything
    /// executes. Both are places CLAUDE.md requires the user be able to see the actual command rather
    /// than a label for it, so this is one definition and not two that could drift.
    /// </summary>
    public abstract string Describe();
}

/// <summary>Opens one of FlickGit's own windows.</summary>
/// <param name="Verb">
/// Routed through the same <c>VerbRunner</c> as the command line, so a built-in action and the CLI
/// spelling of it cannot drift apart or apply different guardrails.
/// </param>
public sealed record WindowRun(VerbKind Verb) : ActionRun
{
    //The window is the command as far as the user is concerned; what happens inside it is guarded
    //separately and has its own confirmations.
    public override string Describe() => Verb.ToString();
}

/// <summary>Runs <c>git</c> in the repository, with these arguments.</summary>
/// <param name="Args">Placeholders expanded per entry — see <see cref="ActionPlaceholders"/>.</param>
public sealed record GitRun(IReadOnlyList<string> Args) : ActionRun
{
    public override string Describe() => $"git {string.Join(' ', Args)}";
}

/// <summary>
/// Runs an external executable.
///
/// The dangerous variant, and the reason <c>actions.json</c> is a trust boundary worth documenting:
/// this can start anything the user can start. It lives in the user's own
/// <c>%LOCALAPPDATA%</c> so it is inside the boundary already, but the settings UI has to say so when
/// one is created, and the file must never be importable from a URL or a repository without explicit
/// confirmation. CLAUDE.md, "Action Catalog", Security.
/// </summary>
public sealed record ProcessRun(string FileName, IReadOnlyList<string> Args) : ActionRun
{
    public override string Describe() => $"{FileName} {string.Join(' ', Args)}".TrimEnd();
}

/// <summary>An ordered sequence. Stops at the first step that fails.</summary>
public sealed record CompositeRun(IReadOnlyList<ActionRun> Steps) : ActionRun
{
    public override string Describe() => string.Join('\n', Steps.Select(step => step.Describe()));
}
