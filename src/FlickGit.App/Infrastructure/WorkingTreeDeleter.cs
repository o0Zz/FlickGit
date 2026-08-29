using System.IO;
using FlickGit.App.Localization;
using FlickGit.Diff;
using FlickGit.Logging;
using Microsoft.VisualBasic.FileIO;

namespace FlickGit.App.Infrastructure;

/// <summary>
/// What a delete did, or why it did not happen.
///
/// <paramref name="Message"/> is null on success — and also on the one failure that has already
/// reported itself: the shell puts up its own error when it cannot remove a file, in Windows' words
/// about a Windows operation, and a second dialog paraphrasing it would be worse than none.
/// </summary>
public sealed record DeleteOutcome(bool Succeeded, string? Message)
{
    public static DeleteOutcome Ok() => new(true, null);

    public static DeleteOutcome Refused(string? message) => new(false, message);
}

/// <summary>
/// Removes one file from the working tree, for the commit window's file list.
///
/// <b>It goes to the Recycle Bin, and that is the whole reason this is not one line of
/// <c>File.Delete</c>.</b> CLAUDE.md's Safety Rules forbid discarding uncommitted work, and an
/// untracked file is uncommitted work Git has never seen — <c>git restore</c> cannot bring it back,
/// because there is nothing to restore it from. A shell delete makes the one destructive thing this
/// window does recoverable by a gesture the user already knows, which is what lets <c>Del</c> run it
/// without a question in the way.
///
/// <b>Nothing tracked comes here.</b> A row Git has something for is untracked from the index instead
/// — <c>TrackingService.UntrackAsync</c>, which leaves the file alone — so the bin is reached only
/// where Git holds no copy of what is about to go.
///
/// Here rather than in <c>FlickGit.Core</c> because it reaches a Windows shell facility, and Core is
/// <c>net9.0</c> precisely so that it cannot. The guard that matters — is this path really inside the
/// repository — is Core's own, reused rather than rewritten: two answers to that question is the one
/// place they could disagree.
/// </summary>
public sealed class WorkingTreeDeleter(ILog log)
{
    /// <param name="repositoryRoot">Absolute repository root. Nothing outside it may be deleted.</param>
    /// <param name="relativePath">Repository-relative path, forward or back slashed.</param>
    public DeleteOutcome Delete(string repositoryRoot, string relativePath)
    {
        string? absolute = WorkingTreeWriter.ResolveInsideRepository(repositoryRoot, relativePath);

        if (absolute is null)
            return DeleteOutcome.Refused(Strings.Get("delete.outside", relativePath, repositoryRoot));

        var info = new FileInfo(absolute);

        if (!info.Exists)
            return DeleteOutcome.Refused(Strings.Get("delete.missing", relativePath));

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || WorkingTreeWriter.CrossesReparsePoint(repositoryRoot, absolute))
        {
            //The same refusal WorkingTreeWriter makes, for a sharper reason: deleting through a
            //junction is how one click removes a file that lives somewhere else entirely.
            //
            //The whole chain, not just the leaf: an intermediate junction is the version of this that
            //ResolveInsideRepository's string comparison cannot see.
            return DeleteOutcome.Refused(Strings.Get("delete.reparsepoint", relativePath));
        }

        try
        {
            //OnlyErrorDialogs is the quietest this API goes: no progress window and no "are you
            //sure", which this operation does not have. What it leaves is the shell's own error on a
            //locked or protected file, which is the accurate report.
            //
            //The cost of suppressing that confirmation, recorded rather than discovered later: where
            //the Recycle Bin cannot take the file -- a network share, or a file past the bin's quota
            //-- the shell deletes it outright instead of asking.
            FileSystem.DeleteFile(
                absolute,
                UIOption.OnlyErrorDialogs,
                RecycleOption.SendToRecycleBin,
                UICancelOption.ThrowException);
        }
        catch (OperationCanceledException)
        {
            //Thrown for a shell failure as well as a cancellation, after the shell has said why.
            log.Info($"Delete of {relativePath} did not complete.");
            return DeleteOutcome.Refused(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DeleteOutcome.Refused(Strings.Get("delete.failed", relativePath, ex.Message));
        }

        log.Info($"Deleted {relativePath} to the Recycle Bin.");
        return DeleteOutcome.Ok();
    }
}
