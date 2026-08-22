using FlickGit.Cli;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// The command-line grammar.
///
/// Phase 3 makes one part of this load-bearing that was not before: the same parser now runs in two
/// processes. The stub parses nothing, but the resident service parses a line that came from
/// <i>somewhere else</i>, so the working directory can no longer be read from the environment.
/// </summary>
public class VerbTests
{
    /// <summary>
    /// A request arriving over the pipe defaults &lt;path&gt; to the <i>stub's</i> directory.
    ///
    /// This is the whole reason the parameter exists. The resident service's own working directory
    /// is wherever the logon task started it — with <c>Environment.CurrentDirectory</c>, a bare
    /// <c>flick commit</c> typed in a repository would open the commit window on <c>C:\Windows</c>.
    /// </summary>
    [Fact]
    public void Missing_path_defaults_to_the_supplied_working_directory()
    {
        Verb verb = Verb.Parse(["commit"], @"C:\dev\FlickGit");

        Assert.Equal(VerbKind.Commit, verb.Kind);
        Assert.Equal(@"C:\dev\FlickGit", verb.Path);
    }

    /// <summary>An explicit path wins over the working directory.</summary>
    [Fact]
    public void Explicit_path_beats_the_working_directory()
    {
        Verb verb = Verb.Parse(["commit", @"C:\repos\alpha"], @"C:\dev\FlickGit");

        Assert.Equal(@"C:\repos\alpha", verb.Path);
    }

    /// <summary>
    /// The path-less verbs stay path-less even when a working directory is offered.
    ///
    /// Otherwise <c>flick settings</c> forwarded from a repository would look like it applied to
    /// that repository, and the runner would demand one before opening a window that has none.
    /// </summary>
    [Theory]
    [InlineData("settings")]
    [InlineData("palette")]
    [InlineData("install-shell")]
    [InlineData("uninstall-shell")]
    [InlineData("autostart")]
    [InlineData("version")]
    [InlineData("help")]
    public void Path_less_verbs_ignore_the_working_directory(string head)
    {
        Assert.Null(Verb.Parse([head], @"C:\dev\FlickGit").Path);
    }

    /// <summary>
    /// No arguments means "go resident", not "commit here".
    ///
    /// The logon task and a bare double-click both land here, and both must produce a tray icon
    /// rather than a window.
    /// </summary>
    [Fact]
    public void No_arguments_starts_resident()
    {
        Verb verb = Verb.Parse([], @"C:\dev\FlickGit");

        Assert.Equal(VerbKind.Tray, verb.Kind);
        Assert.Null(verb.Path);
        Assert.Null(verb.Error);
    }

    /// <summary>
    /// <c>autostart on|off</c> puts its switch where a path normally goes.
    ///
    /// Worth pinning because the positional grammar is what makes that work: the verb reads
    /// args[1] itself, and a "path" that is really a switch must not be turned into a directory.
    /// </summary>
    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    public void Autostart_carries_its_switch_as_the_path_token(string state)
    {
        Verb verb = Verb.Parse(["autostart", state], @"C:\dev\FlickGit");

        Assert.Equal(VerbKind.Autostart, verb.Kind);
        Assert.Equal(state, verb.Path);
    }

    /// <summary>
    /// <c>flick ai key set</c> puts its two sub-tokens where a path and an argument normally go.
    ///
    /// The same positional trick as <c>autostart on</c>, and worth pinning for the same reason: the
    /// verb reads them itself, so a "path" that is really a subcommand must not be turned into a
    /// directory.
    /// </summary>
    [Fact]
    public void Ai_carries_its_subcommand_and_action_as_the_two_positional_tokens()
    {
        Verb bare = Verb.Parse(["ai"], @"C:\dev");
        Assert.Equal(VerbKind.Ai, bare.Kind);
        Assert.Null(bare.Path);
        Assert.Null(bare.Argument);

        Verb key = Verb.Parse(["ai", "key"], @"C:\dev");
        Assert.Equal(VerbKind.Ai, key.Kind);
        Assert.Equal("key", key.Path);
        Assert.Null(key.Argument);

        Verb set = Verb.Parse(["ai", "key", "set"], @"C:\dev");
        Assert.Equal("key", set.Path);
        Assert.Equal("set", set.Argument);
    }

    /// <summary>
    /// Explorer's <c>%V</c> for a drive root arrives quoted, because of the trailing backslash.
    ///
    /// <c>"C:\"</c> reaches a process as <c>C:"</c>. Trimmed in the parser rather than at every
    /// call site, so there is one place that knows about the quirk.
    /// </summary>
    [Fact]
    public void Quoted_path_from_explorer_is_unquoted()
    {
        Assert.Equal(@"C:\repos\alpha", Verb.Parse(["commit", "\"C:\\repos\\alpha\""], @"C:\dev").Path);
    }

    /// <summary>An unknown verb is help plus a reason, never a silent no-op.</summary>
    [Fact]
    public void Unknown_verb_becomes_help_with_an_error()
    {
        Verb verb = Verb.Parse(["frobnicate"], @"C:\dev");

        Assert.Equal(VerbKind.Help, verb.Kind);
        Assert.Contains("frobnicate", verb.Error);
    }

    [Fact]
    public void Unknown_diag_subcommand_becomes_help_with_an_error()
    {
        Verb verb = Verb.Parse(["diag", "everything"], @"C:\dev");

        Assert.Equal(VerbKind.Help, verb.Kind);
        Assert.Contains("everything", verb.Error);
    }

    /// <summary>
    /// Every verb the help text advertises parses to something other than an error.
    ///
    /// Cheap, and it catches the failure mode where a verb is documented, wired into the registry
    /// and then never added to the table — which shows up as "the menu entry does nothing".
    /// </summary>
    [Fact]
    public void Every_documented_verb_parses()
    {
        IEnumerable<string> documented = Verb.HelpText
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("flick ", StringComparison.Ordinal))
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1])
            //Skips the title line's em dash.
            .Where(head => head.All(c => char.IsAsciiLetterLower(c) || c == '-'));

        //The two verbs whose first token is not optional, and what to supply for it. `diag` and
        //`run` are refusals rather than errors without one, which is the behaviour their own tests
        //pin -- so here they are given the argument the help text says they take.
        Dictionary<string, string> required = new()
        {
            ["diag"] = "doctor",
            ["run"] = "custom.something",
        };

        foreach (string head in documented)
        {
            string[] args = required.TryGetValue(head, out string? extra) ? [head, extra] : [head];

            Verb verb = Verb.Parse(args, @"C:\dev");

            Assert.Null(verb.Error);
            Assert.NotEqual(VerbKind.Help, verb.Kind);
        }
    }

    /// <summary>
    /// The command-line grammar. <c>run</c> takes the action id first, the path second.
    ///
    /// The opposite way round from every other verb, and the reason it is parsed before the flat
    /// table: an id is not a path, so the positional grammar would have put it in the wrong slot.
    /// </summary>
    [Fact]
    public void Run_takes_an_action_id_then_a_path()
    {
        Verb verb = Verb.Parse(["run", "custom.fetch-prune", @"C:\devepo"], @"C:\elsewhere");

        Assert.Equal(VerbKind.RunAction, verb.Kind);
        Assert.Equal("custom.fetch-prune", verb.Argument);
        Assert.Equal(@"C:\devepo", verb.Path);
    }

    /// <summary>The path still defaults, so `flick run x` in a repository means that repository.</summary>
    [Fact]
    public void Run_defaults_its_path_to_the_working_directory()
    {
        Verb verb = Verb.Parse(["run", "custom.fetch-prune"], @"C:\devepo");

        Assert.Equal(@"C:\devepo", verb.Path);
    }

    /// <summary>
    /// An id is not optional: `flick run` alone is help plus a reason, never a silent no-op.
    /// </summary>
    [Fact]
    public void Run_without_an_id_is_refused_with_a_reason()
    {
        Verb verb = Verb.Parse(["run"], @"C:\devepo");

        Assert.Equal(VerbKind.Help, verb.Kind);
        Assert.NotNull(verb.Error);
    }
}
