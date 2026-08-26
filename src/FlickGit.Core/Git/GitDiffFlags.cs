namespace FlickGit.Git;

/// <summary>
/// The three flags every diff read carries, from CLAUDE.md, "Git Command Execution":
/// <c>--no-color --no-ext-diff --no-textconv</c> "on every diff read, against the user's own
/// gitconfig".
///
/// All three are load-bearing, and each defends against a different setting the user is entitled
/// to have:
///
/// <list type="bullet">
/// <item><description><c>color.diff = always</c> fills the output with ANSI escapes, which a
/// parser reads as part of the content.</description></item>
/// <item><description><c>diff.external</c> replaces Git's output entirely with whatever the
/// driver prints -- so a numstat becomes something no parser here understands.</description></item>
/// <item><description>A <c>textconv</c> filter spawns a process per blob and makes a binary file
/// report line counts, so the file list shows a fabricated "+42 -17" where it owes the user
/// "bin".</description></item>
/// </list>
///
/// One constant rather than six hand-written triples, because the omission is invisible: a read
/// missing a flag behaves identically on the machine of anyone who has not set the corresponding
/// config, which is almost everyone, until it does not.
///
/// <b><c>-M</c> is deliberately not in here.</b> Rename detection changes <i>which paths come
/// back</i>, not how faithfully they are rendered, so it is a per-call decision and every caller
/// that wants it still asks for it. It matters most where it is absent: the numstat reads in
/// <c>StatusService</c> are merged onto porcelain v2 paths, and porcelain does its own rename
/// detection -- adding <c>-M</c> there would change the paths under the merge and silently drop
/// the counts for a renamed file.
///
/// Spread <b>after</b> the subcommand's own format arguments and <b>before</b> any revision or
/// <c>--</c>, which is what <see cref="History.HistoryService"/> already did: <c>git diff --numstat
/// -z --no-color …</c>, not <c>git diff --no-color --numstat …</c>. Git accepts either, so this is
/// for the reader -- the format is what the call is *for* and belongs next to the subcommand.
/// </summary>
internal static class GitDiffFlags
{
    public static readonly string[] ReadSafe = ["--no-color", "--no-ext-diff", "--no-textconv"];
}
