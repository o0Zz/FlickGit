using System.Text.RegularExpressions;

namespace FlickGit.Branches;

/// <summary>
/// The cheap half of ref-name validation: a name Git will reject, caught before any command runs.
///
/// It exists so the ComboBox in the commit window, the create row in the Branches picker and the
/// name box in the Tags window can all give live feedback per keystroke without a process start.
/// The authoritative answer is <c>git check-ref-format</c>, which
/// <see cref="BranchService.ValidateAsync"/> and <c>TagService.ValidateAsync</c> ask before anything
/// is created -- this only approximates it.
///
/// <b>One pattern, for branches and tags alike.</b> There were two, byte-identical, with a comment
/// on the tag copy claiming they had to differ because "a tag may be called <c>HEAD</c> and a branch
/// may not". Neither pattern mentioned <c>HEAD</c>, so the distinction the duplication was justified
/// by existed in neither of them. The rules below are the ones both kinds genuinely share; if a
/// difference is ever wanted, it belongs on top of this rather than as a second copy of it.
///
/// A pure function of its argument, hence static -- Hard Requirement 3's stated exception.
/// </summary>
public static partial class RefName
{
    [GeneratedRegex(
        """
        (?x)
          ^$                     # empty
        | ^[-.]                  # leading dash or dot
        | [.]$ | [/]$            # trailing dot or slash
        | \.\.                   # ".." anywhere
        | @\{                    # "@{" is reflog syntax
        | ^@$                    # "@" alone means HEAD
        | //                     # empty path component
        | [\x00-\x20~^:?*\[\\\x7f]   # control chars and the characters git forbids outright
        | \.lock(?:/|$)          # a component ending in .lock
        """,
        RegexOptions.CultureInvariant)]
    private static partial Regex ObviouslyInvalid();

    /// <summary>
    /// False when Git is certain to reject <paramref name="name"/>. True means "not obviously wrong",
    /// never "accepted" -- that answer only <c>check-ref-format</c> can give.
    /// </summary>
    public static bool LooksValid(string name) => !ObviouslyInvalid().IsMatch(name.Trim());
}
