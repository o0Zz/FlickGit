namespace FlickGit.Git;

/// <summary>
/// Wraps a path as a pathspec Git cannot glob.
///
/// Every path in this product comes from `git status`, `git diff` or an Explorer selection -- it is
/// a file that exists, never a pattern the user typed. Git does not know that: after <c>--</c> it
/// still reads <c>*</c>, <c>?</c>, <c>[</c> and <c>]</c> as wildcards, so a ticked
/// <c>report[final].xlsx</c> matches nothing while an unticked <c>reportf.xlsx</c> matches instead.
///
/// The magic prefix turns the argument back into the literal bytes it always was. It is not
/// cosmetic on any of the three kinds of call it guards:
///
/// <list type="bullet">
/// <item><description><c>git add</c> stages the wrong file and omits the right one, so the commit
/// is not the one the user reviewed.</description></item>
/// <item><description><c>git restore --source=HEAD --staged --worktree</c> -- the one command in
/// the product that discards uncommitted work -- discards it in a file the user never
/// selected.</description></item>
/// <item><description>The AI diff read pulls a file back in that the secret detector had just
/// excluded, so it leaves the machine after all.</description></item>
/// </list>
///
/// One function rather than a copy per service, because the omission is silent: every path without
/// a glob character behaves identically with and without it, which is almost every path, until it
/// is not.
/// </summary>
internal static class GitPathspec
{
    public static string Literal(string path) => $":(literal){path}";
}
