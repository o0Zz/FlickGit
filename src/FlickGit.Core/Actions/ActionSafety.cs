namespace FlickGit.Actions;

/// <summary>
/// Decides whether an action is destructive, and so must be confirmed in the moment.
///
/// CLAUDE.md, "Safety Rules", lists the operations that may never run automatically, and then says
/// this about every surface including the ones built for speed: "Actions marked
/// <c>RequiresConfirmation</c>, and every operation in the list above, require a second explicit
/// confirmation regardless of surface."
///
/// <b>So the flag in <c>actions.json</c> can only ever be turned on by this, never off.</b> An action
/// file is the one place in the product where the argument list comes from outside the code, and a
/// user action that runs <c>reset --hard</c> from the palette with no confirmation is exactly the hole
/// a "trust the file" reading would leave. The user wrote the file, so they may have the command —
/// they do not get to have it silently.
///
/// A pure function of its arguments, hence static: Hard Requirement 3's stated exception.
/// </summary>
public static class ActionSafety
{
    /// <param name="Command">The Git subcommand, matched as a whole argument.</param>
    /// <param name="Flag">
    /// The argument that makes it destructive, or null when the subcommand always is. Matched whole --
    /// <c>--grep=clean</c> is not <c>clean</c>, and <c>-m "reset --hard"</c> is not a reset -- except
    /// that a long option also matches its <c>=value</c> spelling, so <c>--force-with-lease=main</c>
    /// is the same rule as <c>--force-with-lease</c>.
    /// </param>
    /// <param name="Exemption">An argument that makes it safe again.</param>
    private sealed record Rule(string Command, string? Flag = null, string? Exemption = null);

    /// <summary>
    /// CLAUDE.md's list, as rules rather than as strings to search for.
    ///
    /// Matched by presence rather than adjacency, because <c>git -c core.pager=cat clean -f</c> is
    /// still a clean and a matcher insisting on neighbours would be defeated by a global flag. Over-
    /// matching costs one confirmation; under-matching costs the user their work, so the asymmetry
    /// decides every doubtful case.
    ///
    /// <c>git add -A</c> is deliberately absent. It is banned from <i>the product's own</i> staging
    /// path — the user's selection decides what is committed — but it discards nothing, so demanding
    /// confirmation for a user action that stages everything would spend the user's attention on the
    /// wrong thing.
    /// </summary>
    private static readonly Rule[] Destructive =
    [
        //Any clean at all. Without -f Git refuses and does nothing, so the only cleans that reach a
        //working tree are the ones that remove from it -- and a dry run is not what anybody puts in
        //a saved action.
        new("clean"),

        new("reset", "--hard"),

        //Discarding the working tree, in all the spellings that reach it.
        //
        //`restore` needs both rules and neither is redundant. A bare `git restore <path>` defaults to
        //--worktree, so restore is destructive *unless* --staged narrows it to the index -- which is
        //the first rule. And `--staged --worktree` together are back to discarding, which the
        //exemption would wave through, so the second rule catches --worktree wherever it appears. The
        //old pair matched only a literal ".", so `restore --worktree src/` was silent.
        new("restore", Exemption: "--staged"),
        new("restore", "--worktree"),

        //`checkout -- <path>` is "discard the changes in this path", whatever the path is; the `--` is
        //what separates it from `checkout <branch>`, which discards nothing and is refused by Git when
        //it would.
        new("checkout", "."),
        new("checkout", "--"),
        new("checkout", "-f"),
        new("checkout", "--force"),

        //The same two, for the verb that replaced checkout. --discard-changes is the spelling `switch`
        //uses for what `checkout -f` does, and it is the one a saved action is most likely to carry.
        new("switch", "-f"),
        new("switch", "--force"),
        new("switch", "--discard-changes"),

        new("branch", "-D"),
        new("branch", "--delete"),

        //Tag deletion, which CLAUDE.md's list does not name -- and this class is otherwise a reading
        //of that list rather than an improvement on it. The exception is argued because the asymmetry
        //this class already relies on decides it: a tag is the only ref in Git with no reflog, so
        //`tag -d` is the one delete that leaves nothing at all behind to recover from. `branch -D` is
        //on the list and it is strictly the *less* final of the two.
        new("tag", "-d"),
        new("tag", "--delete"),

        //Throwing a stash away, on the argument the line above has just made. These two used to be
        //named here as deliberately *absent*, on the grounds that CLAUDE.md's list does not mention
        //them -- which was the right call while nothing in the product had a stash surface. It has one
        //now, so a saved action running `stash drop` is a thing somebody writes. A stash has no reflog
        //of its own: the entry is the only handle on the commit, so dropping it leaves nothing this
        //product can find again, which is exactly what earned `tag -d` its place. `clear` does it to
        //every stash at once, which is why the Stashes window offers it nowhere at all.
        new("stash", "drop"),
        new("stash", "clear"),

        //Deleting any ref on a remote. Not force-push, but it removes something other people have
        //already fetched, which is the harm `push --force` is on the list for.
        new("push", "--delete"),
        new("push", "-d"),

        //--force-with-lease is safer than --force and still overwrites a branch somebody else may be
        //standing on.
        new("push", "--force"),
        new("push", "-f"),
        new("push", "--force-with-lease"),
    ];

    /// <summary>
    /// True when <paramref name="run"/> contains anything that can lose the user's work.
    ///
    /// Recurses into a <see cref="CompositeRun"/>: a sequence whose third step is <c>reset --hard</c>
    /// is a destructive action, and asking about the sequence has to be asking about all of it.
    ///
    /// A <see cref="ProcessRun"/> is always true. There is no way to know what an arbitrary
    /// executable does, and the honest answer to "unknown" on this question is to ask.
    /// </summary>
    public static bool IsDestructive(ActionRun run) =>
        run switch
        {
            GitRun git => Destructive.Any(rule => Matches(git.Args, rule)) || IsForcedRefspec(git.Args),
            ProcessRun => true,
            CompositeRun composite => composite.Steps.Any(IsDestructive),

            //A window is a surface, not an operation. Everything it can then do carries its own
            //guardrails, which is why built-ins are windows wherever there is a choice.
            _ => false,
        };

    private static bool Matches(IReadOnlyList<string> args, Rule rule) =>
        Contains(args, rule.Command)
        && (rule.Flag is null || Contains(args, rule.Flag))
        && (rule.Exemption is null || !Contains(args, rule.Exemption));

    /// <summary>
    /// A force push written as a refspec rather than as a flag: <c>git push origin +HEAD:main</c>.
    ///
    /// The leading <c>+</c> is Git's own force marker and it overwrites the remote branch exactly as
    /// <c>--force</c> does, so leaving it to the flag rules is leaving the hole they exist to close.
    /// Nothing else a push carries begins with a <c>+</c>: options begin with <c>-</c>, and remote and
    /// branch names cannot.
    /// </summary>
    private static bool IsForcedRefspec(IReadOnlyList<string> args)
    {
        if (!Contains(args, "push"))
            return false;

        foreach (string argument in args)
        {
            if (argument.StartsWith('+') && argument.Length > 1)
                return true;
        }

        return false;
    }

    private static bool Contains(IReadOnlyList<string> args, string token)
    {
        //A long option's `=value` form is the same option. Only for `--` options: `-f=x` is not a
        //spelling Git accepts, and matching a prefix on a short flag would read `-m=reset` as one.
        bool longOption = token.StartsWith("--", StringComparison.Ordinal);

        foreach (string argument in args)
        {
            if (argument.Equals(token, StringComparison.Ordinal))
                return true;

            if (longOption && argument.StartsWith(token + "=", StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
