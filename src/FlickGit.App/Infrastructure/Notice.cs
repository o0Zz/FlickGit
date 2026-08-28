using System.Windows;
using FlickGit.App.Localization;
using FlickGit.App.Views;

namespace FlickGit.App.Infrastructure;

/// <summary>
/// Shows a modal notice owned by the window that raised it.
///
/// Six windows had their own <c>Report(title, message)</c> doing exactly this line, and two more
/// call sites wrote it out inline. The duplication was harmless right up until one of them forgot
/// the owner: an unowned modal over a window the user is looking at can end up behind it, and a
/// modal dialog behind its own parent is a hung application as far as the user can tell.
///
/// <see cref="CommandLine.VerbOutput.Notice"/> is the other half of this and stays separate on
/// purpose: it shows a <i>non-modal</i>, unowned notice, because it answers a verb that may have no
/// window at all.
/// </summary>
internal static class Notice
{
    /// <param name="detail">Git's raw stderr, shown monospaced in its own box. Null to omit it.</param>
    public static void Show(Window owner, string title, string message, string? detail = null) =>
        new NoticeWindow(title, message, compact: false, detail) { Owner = owner }.ShowDialog();

    /// <summary>
    /// A Git command failed, said in the four parts CLAUDE.md's "Error Handling" section requires:
    /// the operation, what happened, the repository path, and Git's own words.
    ///
    /// <b>This exists because the same mistake was made five times.</b> Five call sites passed
    /// <c>outcome.GitError ?? string.Empty</c> as the <i>message</i> and nothing as the detail, which
    /// puts raw stderr where a sentence belongs, drops the repository path, and -- when Git said
    /// nothing at all -- renders a titled dialog with no body, the one thing
    /// <see cref="NoticeWindow"/>'s own contract says cannot happen. The path was reaching exactly one
    /// error in the product before this.
    /// </summary>
    /// <param name="title">The operation, in the user's words: "Drop stash", "Check out".</param>
    /// <param name="message">
    /// What happened, as a sentence. Says what did <i>not</i> change where that is the useful half --
    /// a failed drop leaves the stash in the list, and knowing that is what tells the user they can
    /// simply try again.
    /// </param>
    /// <param name="gitError">Git's stderr. Omitted from the dialog when Git said nothing.</param>
    public static void GitFailure(
        Window owner,
        string title,
        string message,
        string? gitError,
        string repositoryPath)
    {
        string body = message + "\n\n" + Strings.Get("error.repositorypath", repositoryPath);

        Show(owner, title, body, gitError is { Length: > 0 } words ? words.Trim() : null);
    }
}
