using System.IO;
using FlickGit.App.Localization;
using FlickGit.Diff;
using FlickGit.Logging;
using Microsoft.VisualBasic.FileIO;

namespace FlickGit.App.Infrastructure;

/// <summary>
/// Removes one path from the working tree — the commit window's file list, and the Explorer menu's
/// Delete.
///
/// <b>It goes to the Recycle Bin, and that is the whole reason this is not one line of
/// <c>File.Delete</c>.</b> CLAUDE.md's Safety Rules forbid discarding uncommitted work, and an
/// untracked file is uncommitted work Git has never seen — <c>git restore</c> cannot bring it back,
/// because there is nothing to restore it from. A shell delete makes the one destructive thing these
/// surfaces do recoverable by a gesture the user already knows, which is what lets <c>Del</c> and the
/// menu entry run it without a question in the way.
///
/// <b>A folder is the same operation</b>, because the Explorer entry is drawn on a clicked folder as
/// well as on a file and everything under it goes at once. It is the one branch in here:
/// <c>DeleteDirectory</c> rather than <c>DeleteFile</c>, with the same bin, the same guards and the
/// same silence. The commit window never produces it — its rows are files.
///
/// <b>What Git holds is somebody else's job.</b> The commit window untracks a tracked row instead of
/// coming here; the menu's Delete comes here <i>after</i> <c>TrackingService.UntrackAsync</c> has
/// taken the path out of the index, in that order, so a path Git refuses is still on disk. Nothing in
/// this class knows or asks which case it is in.
///
/// Here rather than in <c>FlickGit.Core</c> because it reaches a Windows shell facility, and Core is
/// <c>net9.0</c> precisely so that it cannot. The guard that matters — is this path really inside the
/// repository — is Core's own, reused rather than rewritten: two answers to that question is the one
/// place they could disagree.
/// </summary>
public sealed class WorkingTreeDeleter(ILog log) : ITrash
{
    /// <param name="repositoryRoot">Absolute repository root. Nothing outside it may be deleted.</param>
    /// <param name="relativePath">Repository-relative path, forward or back slashed.</param>
    public DeleteOutcome Delete(string repositoryRoot, string relativePath)
    {
        string? absolute = WorkingTreeWriter.ResolveInsideRepository(repositoryRoot, relativePath);

        if (absolute is null)
            return DeleteOutcome.Refused(Strings.Get("delete.outside", relativePath, repositoryRoot));

        //FileSystemInfo, because a FileInfo over a directory reports Exists false and this would
        //answer "no longer on disk" about a folder that is plainly there.
        bool isFolder = Directory.Exists(absolute);
        FileSystemInfo info = isFolder ? new DirectoryInfo(absolute) : new FileInfo(absolute);

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
            if (isFolder)
            {
                //DeleteAllContents rather than ThrowIfDirectoryNonEmpty: the user pointed at the
                //folder, and everything under it goes with it -- to the bin, in one undoable item.
                FileSystem.DeleteDirectory(
                    absolute,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin,
                    UICancelOption.ThrowException);
            }
            else
            {
                FileSystem.DeleteFile(
                    absolute,
                    UIOption.OnlyErrorDialogs,
                    RecycleOption.SendToRecycleBin,
                    UICancelOption.ThrowException);
            }
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
