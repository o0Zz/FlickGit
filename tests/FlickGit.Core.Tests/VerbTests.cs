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
    [InlineData("language")]
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
    /// <c>flick language fr</c> puts the code where a path normally goes.
    ///
    /// The same positional trick as <c>autostart on</c>, and pinned for the same reason: the verb
    /// reads args[1] itself, so a "path" that is really a language code must not be turned into a
    /// directory -- which would make `flick language fr` typed in a repository set the language to
    /// that repository's path.
    /// </summary>
    [Theory]
    [InlineData("fr")]
    [InlineData("auto")]
    public void Language_carries_its_code_as_the_path_token(string code)
    {
        Verb verb = Verb.Parse(["language", code], @"C:\dev\FlickGit");

        Assert.Equal(VerbKind.Language, verb.Kind);
        Assert.Equal(code, verb.Path);
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
    /// `stash` reads a message as the second positional slot, the way `tag` reads a name below.
    ///
    /// Bare it is the window; with a message it puts the working tree away. Nothing spells popping or
    /// dropping, because both name an existing stash by a reflog selector and a selector written into
    /// a script is a position that will have moved by the time it runs.
    /// </summary>
    [Fact]
    public void Stash_carries_its_message_as_the_argument()
    {
        Verb bare = Verb.Parse(["stash", @"C:\dev\repo"], @"C:\dev");

        Assert.Equal(VerbKind.Stash, bare.Kind);
        Assert.Equal(@"C:\dev\repo", bare.Path);
        Assert.Null(bare.Argument);

        Verb described = Verb.Parse(["stash", @"C:\dev\repo", "pool leak"], @"C:\dev");

        Assert.Equal("pool leak", described.Argument);
    }

    /// <summary>
    /// `tag` reads its name as the second positional slot, the way `switch` reads a branch.
    ///
    /// Bare it is the picker, so the argument stays null rather than becoming the empty string --
    /// which is the distinction the runner branches on.
    /// </summary>
    [Fact]
    public void Tag_carries_its_name_as_the_argument()
    {
        Verb bare = Verb.Parse(["tag", @"C:\dev\repo"], @"C:\dev");

        Assert.Equal(VerbKind.Tag, bare.Kind);
        Assert.Equal(@"C:\dev\repo", bare.Path);
        Assert.Null(bare.Argument);

        Verb named = Verb.Parse(["tag", @"C:\dev\repo", "v1.4.0"], @"C:\dev");

        Assert.Equal("v1.4.0", named.Argument);
    }

    /// <summary>
    /// `submodule` is the picker and nothing else: no second token, and the path still defaults.
    ///
    /// Adding takes a URL *and* a folder, so there is no one value the single positional slot could
    /// safely hold -- and a token that was silently ignored would be worse than one that is refused.
    /// </summary>
    [Fact]
    public void Submodule_is_the_picker_and_takes_no_argument()
    {
        Verb bare = Verb.Parse(["submodule"], @"C:\dev\repo");

        Assert.Equal(VerbKind.Submodule, bare.Kind);
        Assert.Equal(@"C:\dev\repo", bare.Path);
        Assert.Null(bare.Argument);

        Verb located = Verb.Parse(["submodule", @"C:\dev\other"], @"C:\dev");

        Assert.Equal(@"C:\dev\other", located.Path);
        Assert.Null(located.Argument);
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

            //`help` is the one documented verb whose answer *is* Help, so the guard below cannot
            //apply to it. Everywhere else, Help means the flat table did not recognise the head --
            //and an unrecognised head also carries an Error, which the assertion above already
            //catches. This keeps the second signal for every verb that can still give it.
            if (head != "help")
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
        Verb verb = Verb.Parse(["run", "custom.fetch-prune", @"C:\dev
epo"], @"C:\elsewhere");

        Assert.Equal(VerbKind.RunAction, verb.Kind);
        Assert.Equal("custom.fetch-prune", verb.Argument);
        Assert.Equal(@"C:\dev
epo", verb.Path);
    }

    /// <summary>The path still defaults, so `flick run x` in a repository means that repository.</summary>
    [Fact]
    public void Run_defaults_its_path_to_the_working_directory()
    {
        Verb verb = Verb.Parse(["run", "custom.fetch-prune"], @"C:\dev
epo");

        Assert.Equal(@"C:\dev
epo", verb.Path);
    }

    /// <summary>
    /// An id is not optional: `flick run` alone is help plus a reason, never a silent no-op.
    /// </summary>
    [Fact]
    public void Run_without_an_id_is_refused_with_a_reason()
    {
        Verb verb = Verb.Parse(["run"], @"C:\dev
epo");

        Assert.Equal(VerbKind.Help, verb.Kind);
        Assert.NotNull(verb.Error);
    }

    /// <summary>
    /// `add` and `rm` take every trailing token as a path, because Explorer hands them a selection.
    ///
    /// In scope under "the command-line grammar". Acting on the first token and silently dropping the
    /// rest is the bug this exists to pin: it reported success, so nothing anywhere said that six of
    /// the seven files the user selected had been left alone.
    /// </summary>
    [Theory]
    [InlineData("add")]
    [InlineData("rm")]
    public void Add_and_rm_take_every_trailing_token_as_a_path(string spelling)
    {
        Verb verb = Verb.Parse(
            [spelling, @"C:\dev\repo\a.cs", @"C:\dev\repo\notes with a space.md", @"C:\dev\repo\naïve"],
            @"C:\dev\repo");

        Assert.Null(verb.Error);
        Assert.Equal(3, verb.Paths.Count);

        Assert.Equal(
            [@"C:\dev\repo\a.cs", @"C:\dev\repo\notes with a space.md", @"C:\dev\repo\naïve"],
            verb.Paths);

        //The first entry is still what repository resolution works from, and nothing landed in the
        //slot a second token would occupy for another verb.
        Assert.Equal(@"C:\dev\repo\a.cs", verb.Path);
        Assert.Null(verb.Argument);
    }

    /// <summary>
    /// Only those two read more than one positional path.
    ///
    /// In scope under "the command-line grammar", and the assertion that pins the reason the path list
    /// is a switch on the kind rather than a rule about trailing arguments: `tag`, `stash`, `switch`
    /// and `clone` all use the second slot for something that is <i>not</i> a path, so a general rule
    /// would read `flick tag . v1.0` as two paths and create no tag at all.
    /// </summary>
    [Theory]
    [InlineData("tag", "v1.0")]
    [InlineData("stash", "wip: pooling")]
    [InlineData("switch", "feature/storage-gw")]
    [InlineData("clone", "https://example.com/x.git")]
    public void Only_add_and_rm_read_more_than_one_positional_path(string spelling, string second)
    {
        Verb verb = Verb.Parse([spelling, @"C:\dev\repo", second], @"C:\dev");

        Assert.Null(verb.Error);
        Assert.Equal(@"C:\dev\repo", verb.Path);
        Assert.Equal(second, verb.Argument);
        Assert.Equal([@"C:\dev\repo"], verb.Paths);
    }

    /// <summary>
    /// A selection too large for one command line arrives as a count and <b>no path at all</b>.
    ///
    /// In scope under "the command-line grammar". The empty list is the safety property: the shell
    /// handler sends this instead of a shortened list, because a removal carrying the first four
    /// hundred of five hundred selected files is a removal nobody asked for. `Path` staying null is
    /// the other half — defaulting it to the working directory here would turn "too many files" into
    /// an operation on a whole directory.
    /// </summary>
    [Fact]
    public void A_selection_too_large_for_one_command_line_carries_no_path_at_all()
    {
        Verb verb = Verb.Parse(["add", "--too-many", "742"], @"C:\dev\repo");

        Assert.Equal(VerbKind.Add, verb.Kind);
        Assert.Null(verb.Error);
        Assert.Empty(verb.Paths);
        Assert.Null(verb.Path);
        Assert.Equal("742", verb.Argument);
    }

    /// <summary>
    /// With no path given at all, the two selection verbs still default to the working directory.
    ///
    /// In scope under "the command-line grammar": CLAUDE.md says `&lt;path&gt;` defaults to the current
    /// directory for every verb, and the path list must not have quietly taken that away. It is also
    /// what keeps the empty list meaning only one thing — a refused selection, never an absent one.
    /// </summary>
    [Fact]
    public void A_selection_verb_with_no_path_still_defaults_to_the_working_directory()
    {
        Verb verb = Verb.Parse(["rm"], @"C:\dev\repo");

        Assert.Equal(VerbKind.Remove, verb.Kind);
        Assert.Equal(@"C:\dev\repo", verb.Path);
        Assert.Equal([@"C:\dev\repo"], verb.Paths);
    }
}
