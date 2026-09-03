using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using FlickGit.App.Localization;
using FlickGit.App.Settings;
using FlickGit.Diff;
using FlickGit.Logging;

namespace FlickGit.App.Infrastructure;

/// <summary>
/// What an Edit did, or why it did not happen. <paramref name="Message"/> is null on success.
/// </summary>
public sealed record EditOutcome(bool Succeeded, string? Message)
{
    public static EditOutcome Ok() => new(true, null);

    public static EditOutcome Refused(string message) => new(false, message);
}

/// <summary>
/// Opens one file from the commit window's file list in an external editor -- the one named by
/// <see cref="FlickSettings.ExternalEditor"/>, or Notepad when that is empty.
///
/// <b>The file is always an argument, never the program.</b> That is the whole safety argument here:
/// a commit's file list routinely holds <c>build.bat</c>, a stray <c>setup.exe</c> or a <c>.reg</c>,
/// and shell-executing one of those the way Explorer would is <i>running</i> it. Passing the path to
/// an editor cannot run anything, whichever editor it is -- which is also why the fallback is
/// Notepad rather than the file's own default program. Notepad is on every Windows machine, opens
/// anything as text, and has no verb that executes.
///
/// <b>No reparse-point refusal</b>, unlike <see cref="WorkingTreeDeleter"/> beside it. That guard is
/// there because deleting through a junction destroys a tree living somewhere else; handing a path
/// to an editor destroys nothing, and what the user's editor does with the file afterwards was never
/// ours to police. The one guard that is kept is the root check, because a path escaping the
/// repository is a bug wherever it came from.
///
/// Here rather than in <c>FlickGit.Core</c> because it reads a setting, which is App's.
/// </summary>
public sealed class EditorLauncher(FlickSettings settings, ILog log)
{
    /// <param name="repositoryRoot">Absolute repository root. Nothing outside it is opened.</param>
    /// <param name="relativePath">Repository-relative path, forward or back slashed.</param>
    public EditOutcome Edit(string repositoryRoot, string relativePath)
    {
        //Core's own guard, reused rather than rewritten -- two answers to "is this path inside the
        //repository" is the one place they could disagree.
        string? absolute = WorkingTreeWriter.ResolveInsideRepository(repositoryRoot, relativePath);

        if (absolute is null)
            return EditOutcome.Refused(Strings.Get("edit.outside", relativePath, repositoryRoot));

        if (!File.Exists(absolute))
            return EditOutcome.Refused(Strings.Get("edit.missing", relativePath));

        //The editor the user named, or the platform's own.
        //
        //The macOS default needs a leading argument as well as a name. `open -t` means "open in the
        //registered *text* editor"; bare `open` hands the file to whatever is registered for its
        //extension, which on a developer's Mac is as likely to be a full IDE taking ten seconds to
        //start as it is a text editor.
        string editor = settings.ExternalEditor;
        string[] leading = [];

        if (editor.Length == 0)
            (editor, leading) = OperatingSystem.IsWindows()
                ? ("notepad.exe", Array.Empty<string>())
                : ("/usr/bin/open", ["-t"]);

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = editor,

                //So an editor that resolves relative paths, or opens a folder view beside the file,
                //starts where the repository is rather than wherever FlickGit was launched from.
                WorkingDirectory = repositoryRoot,

                //False, and not only for the reason above: it is also what keeps the path out of any
                //shell. ArgumentList quotes a folder containing a space, or a name ending in a
                //backslash, rather than us building a command string -- which the codebase does
                //nowhere.
                UseShellExecute = false,
            };

                foreach (string argument in leading)
                start.ArgumentList.Add(argument);

            start.ArgumentList.Add(absolute);

            using Process? started = Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or IOException)
        {
            //The failure a wrong setting produces, so it names the editor it was given: "Code.exe
            //could not be found" is diagnosable and "the file could not be opened" is not.
            log.Info($"{relativePath} could not be opened with {editor}: {ex.Message}");
            return EditOutcome.Refused(Strings.Get("edit.failed", relativePath, editor, ex.Message));
        }

        log.Info($"Opened {relativePath} with {editor}.");
        return EditOutcome.Ok();
    }
}
