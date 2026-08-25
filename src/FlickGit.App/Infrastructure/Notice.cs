using System.Windows;
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
}
