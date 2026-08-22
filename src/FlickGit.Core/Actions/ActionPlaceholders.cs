using FlickGit.Models;

namespace FlickGit.Actions;

/// <summary>
/// What a placeholder can be replaced with.
/// </summary>
/// <param name="Repository">The resolved repository. <c>{repo}</c>.</param>
/// <param name="Branch">The checked-out branch. <c>{branch}</c>.</param>
/// <param name="Upstream">Its upstream, as <c>origin/main</c>. <c>{upstream}</c>.</param>
/// <param name="Remote">The default remote. <c>{remote}</c>.</param>
/// <param name="Selection">The clicked path, when a surface has one. <c>{selection}</c>.</param>
/// <param name="Files">The ticked paths. <c>{files}</c>, which expands to one argument each.</param>
public sealed record ActionContext(
    RepositoryInfo Repository,
    string? Branch = null,
    string? Upstream = null,
    string Remote = "origin",
    string? Selection = null,
    IReadOnlyList<string>? Files = null);

/// <summary>
/// Expands <c>{repo}</c> and friends inside an argument list.
///
/// <b>Per entry, never across them.</b> CLAUDE.md: placeholders are "substituted into
/// <c>ArgumentList</c> entries — <i>never</i> into a concatenated string." That is the whole point of
/// this class existing rather than a <c>string.Format</c> at the call site: a repository path with a
/// space in it is one argument here and two arguments in anything built by concatenation, and a path
/// with a quote in it is an injected argument.
///
/// A pure function of its arguments, which is why it is static — see Hard Requirement 3's exception
/// for exactly that.
/// </summary>
public static class ActionPlaceholders
{
    /// <summary>
    /// The one placeholder that is a list rather than a value.
    ///
    /// Honoured only as a <i>whole</i> argument, deliberately. Substituting a file list into part of
    /// an argument would need a separator, and choosing a separator is making a quoting decision —
    /// which is the decision <c>ArgumentList</c> exists to take away from us.
    /// </summary>
    private const string FilesToken = "{files}";

    /// <summary>
    /// Expands every argument of <paramref name="run"/>, giving back a run that names the real
    /// command.
    ///
    /// Both the palette's footer and the confirmation dialog show this rather than the declaration.
    /// CLAUDE.md wants "the exact command about to run" visible before Enter, and
    /// <c>git fetch --prune {remote}</c> is not a command anybody can run — it is what the action file
    /// says, which is a different thing.
    /// </summary>
    public static ActionRun Expand(ActionRun run, ActionContext context) =>
        run switch
        {
            GitRun git => new GitRun(Expand(git.Args, context)),
            ProcessRun process => new ProcessRun(process.FileName, Expand(process.Args, context)),
            CompositeRun composite => new CompositeRun([.. composite.Steps.Select(step => Expand(step, context))]),

            //A window has no arguments to expand.
            _ => run,
        };

    /// <summary>
    /// Expands every entry of <paramref name="args"/>.
    ///
    /// An unknown placeholder is left exactly as written. It is a typo in the user's own file, and
    /// silently deleting it would turn <c>git push {remoat}</c> into <c>git push</c> — a different
    /// command that succeeds.
    /// </summary>
    public static IReadOnlyList<string> Expand(IReadOnlyList<string> args, ActionContext context)
    {
        var expanded = new List<string>(args.Count);

        foreach (string argument in args)
        {
            //A whole-token {files} becomes one argument per file, and none at all when there are no
            //files -- which is what makes `git add -- {files}` with nothing ticked a no-op rather
            //than a command that means "everything".
            if (argument == FilesToken)
            {
                if (context.Files is { } files)
                    expanded.AddRange(files);

                continue;
            }

            expanded.Add(Substitute(argument, context));
        }

        return expanded;
    }

    /// <summary>
    /// Expands the placeholders in a single value.
    ///
    /// Missing values become empty rather than being left as the literal placeholder: an action
    /// asking for <c>{upstream}</c> in a repository that has none should produce an argument Git can
    /// reject clearly, not the four characters that would make Git think it was a ref name.
    /// </summary>
    private static string Substitute(string argument, ActionContext context)
    {
        if (!argument.Contains('{'))
            return argument;

        return argument
            .Replace("{repo}", context.Repository.Root, StringComparison.Ordinal)
            .Replace("{branch}", context.Branch ?? string.Empty, StringComparison.Ordinal)
            .Replace("{upstream}", context.Upstream ?? string.Empty, StringComparison.Ordinal)
            .Replace("{remote}", context.Remote, StringComparison.Ordinal)
            .Replace("{selection}", context.Selection ?? context.Repository.Root, StringComparison.Ordinal);
    }
}
