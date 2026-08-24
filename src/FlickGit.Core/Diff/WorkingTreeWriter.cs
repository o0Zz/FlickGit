using System.Security.Cryptography;
using System.Text;

namespace FlickGit.Diff;

/// <summary>
/// Writes an edited file back to the working tree.
///
/// CLAUDE.md opens the section this implements with "This is the feature most likely to destroy
/// user work. These rules are not optional." Every one of them is here, and each is a refusal
/// rather than a best effort:
///
/// <list type="number">
/// <item><description><b>The file is rewritten with the same encoding, BOM state and line
/// endings it was read with.</b> Normalising line endings on a Windows tool turns a three-line
/// change into a whole-file diff. The trailing-newline state is preserved too, because adding
/// or removing one is also a diff.</description></item>
/// <item><description><b>External modification is detected before writing.</b> Size, last-write
/// time and content hash are compared against the load-time stamp. If the IDE reformatted on
/// save or a build regenerated the file, the write is refused and the caller offers reload,
/// overwrite or save-as.</description></item>
/// <item><description><b>The write is atomic.</b> A temp file in the same directory, then
/// <c>File.Replace</c>, which keeps the file's identity stable for IDE watchers and build
/// tools rather than deleting and recreating it.</description></item>
/// <item><description><b>Refused for anything outside the repository, and for a path that has
/// become a symlink or junction since load.</b> Following a reparse point would write through
/// to wherever it now points, which may be anywhere on the machine.</description></item>
/// </list>
/// </summary>
public sealed class WorkingTreeWriter
{
    /// <summary>
    /// Saves <paramref name="newText"/> over the file <paramref name="original"/> was loaded
    /// from.
    /// </summary>
    /// <param name="repositoryRoot">Absolute repository root. Nothing outside it may be written.</param>
    /// <param name="relativePath">Repository-relative path, forward or back slashed.</param>
    /// <param name="original">The <see cref="FileText"/> from load time, carrying the stamp and the format.</param>
    /// <param name="newText">The edited text, LF-normalised as the editor holds it.</param>
    /// <param name="force">
    /// Overwrite even if the file changed on disk. Only ever set from an explicit user choice in
    /// the external-modification prompt.
    /// </param>
    public async Task<SaveOutcome> SaveAsync(
        string repositoryRoot,
        string relativePath,
        FileText original,
        string newText,
        bool force,
        CancellationToken cancellationToken)
    {
        if (original.IsBinary)
            return SaveOutcome.Refused(SaveRefusal.Binary, "This is a binary file and cannot be edited here.");

        string? absolute = ResolveInsideRepository(repositoryRoot, relativePath);
        if (absolute is null)
        {
            //A path that escapes the root is either a bug or an attack, and either way this is
            //not the layer that guesses which.
            return SaveOutcome.Refused(
                SaveRefusal.OutsideRepository,
                $"{relativePath} is outside {repositoryRoot} and will not be written.");
        }

        var info = new FileInfo(absolute);

        if (!info.Exists)
        {
            return SaveOutcome.Refused(
                SaveRefusal.Missing,
                $"{relativePath} no longer exists on disk.\n\nIt may have been deleted or moved since it was opened.");
        }

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            //Checked at save time, not load time: the interesting case is the one where it
            //became a link while the file was open.
            return SaveOutcome.Refused(
                SaveRefusal.ReparsePoint,
                $"{relativePath} is now a symlink or junction. FlickGit will not write through one.");
        }

        if (!force)
        {
            SaveOutcome? changed = await DetectExternalChangeAsync(info, absolute, original, cancellationToken)
                .ConfigureAwait(false);

            if (changed is not null)
                return changed;
        }

        byte[] bytes = Encode(original, newText);

        try
        {
            await WriteAtomicallyAsync(absolute, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SaveOutcome.Refused(SaveRefusal.WriteFailed, $"{relativePath} could not be written:\n\n{ex.Message}");
        }

        //A fresh stamp for the caller to keep, so the next save compares against what is now on
        //disk rather than against what was there when the file was first opened.
        var saved = new FileInfo(absolute);

        return new SaveOutcome(true, null, null)
        {
            Saved = original with
            {
                Text = newText,
                SizeInBytes = saved.Length,
                LastWriteTimeUtc = saved.LastWriteTimeUtc,
                ContentHash = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                EndsWithNewline = newText.EndsWith('\n') || original.EndsWithNewline,
            },
        };
    }

    /// <summary>
    /// Encodes the edited text back into the file's original format.
    ///
    /// Internal rather than private so the round-trip tests can assert on bytes directly: the
    /// whole guarantee of this class is byte-level, and asserting it through the file system
    /// would test the file system.
    /// </summary>
    internal static byte[] Encode(FileText original, string newText)
    {
        //The editor holds LF. Everything here puts the file's own convention back.
        string body = newText.Replace("\r\n", "\n").Replace('\r', '\n');

        if (original.PerLineEndings is { Count: > 0 } perLine)
        {
            //A mixed-ending file. Each surviving line gets back the terminator it had, matched
            //by diffing the original text against the edited one -- so an edit to line 2 of a
            //file that is half CRLF and half LF changes line 2 and nothing else.
            //
            //Applying one uniform style here instead is the failure CLAUDE.md names: it would
            //rewrite every line of the other kind and turn a one-line change into a whole-file
            //diff.
            body = ReapplyMixedEndings(original, perLine, body);
        }
        else if (original.NewLine != "\n")
        {
            body = body.Replace("\n", original.NewLine);
        }

        //Trailing newline: matched to the original. A file that did not end with one must not
        //acquire one, and a file that did must not lose it -- both show up as a change to the
        //last line in every diff the user looks at afterwards.
        //
        //The terminator compared against is the file's last one, which for a mixed file is not
        //necessarily the dominant style.
        string trailing = LastTerminator(original);
        bool hasTrailing = trailing.Length > 0 && body.EndsWith(trailing, StringComparison.Ordinal);

        if (original.EndsWithNewline && !hasTrailing && body.Length > 0)
            body += trailing.Length > 0 ? trailing : original.NewLine;
        else if (!original.EndsWithNewline && hasTrailing)
            body = body[..^trailing.Length];

        byte[] content = original.Encoding.GetBytes(body);

        if (!original.HasByteOrderMark)
            return content;

        //The encoding instance carries the right preamble for its own flavour -- three bytes for
        //UTF-8, two for UTF-16, four for UTF-32 -- so the BOM is never assembled by hand.
        byte[] preamble = original.Encoding.GetPreamble();

        if (preamble.Length == 0)
            return content;

        byte[] withBom = new byte[preamble.Length + content.Length];
        preamble.CopyTo(withBom, 0);
        content.CopyTo(withBom, preamble.Length);
        return withBom;
    }

    /// <summary>
    /// Rebuilds a mixed-ending file, giving every line that survived the edit the terminator it
    /// originally had.
    ///
    /// The mapping comes from a line diff of the original text against the edited one, which is
    /// the only way to know that "line 7 now" is "line 5 before" after the user inserted two
    /// lines above it. Matching by index instead would shift every terminator below an insertion
    /// and flip half the file.
    ///
    /// Lines the user added get the dominant style, since they never had one.
    /// </summary>
    private static string ReapplyMixedEndings(FileText original, IReadOnlyList<string> perLine, string editedBody)
    {
        IReadOnlyList<DiffRow> rows = DiffService.Rediff(original.Text, editedBody, wordLevel: false);

        //Right-hand line number to left-hand line number, for the lines present on both sides.
        var originalLineOf = new Dictionary<int, int>();

        foreach (DiffRow row in rows)
        {
            if (row.Right.LineNumber is { } right && row.Left.LineNumber is { } left)
                originalLineOf[right] = left;
        }

        string[] lines = editedBody.Split('\n');

        //Split leaves a trailing empty element when the text ends with a newline. That element is
        //not a line, and appending a terminator to it would add a blank line to the file.
        int count = lines.Length > 0 && lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;

        var builder = new StringBuilder(editedBody.Length + 16);

        for (int i = 0; i < count; i++)
        {
            builder.Append(lines[i]);

            bool isLast = i == count - 1;
            bool needsTerminator = !isLast || editedBody.EndsWith('\n');

            if (!needsTerminator)
                continue;

            //A surviving line keeps its own terminator; a new one gets the dominant style.
            builder.Append(
                originalLineOf.TryGetValue(i + 1, out int originalLine)
                && originalLine - 1 < perLine.Count
                && perLine[originalLine - 1].Length > 0
                    ? perLine[originalLine - 1]
                    : original.NewLine);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The terminator the file actually ended with, which for a mixed file may not be the
    /// dominant one.
    /// </summary>
    private static string LastTerminator(FileText original)
    {
        if (original.PerLineEndings is { Count: > 0 } perLine)
        {
            string last = perLine[^1];
            if (last.Length > 0)
                return last;
        }

        return original.EndsWithNewline ? original.NewLine : string.Empty;
    }

    /// <summary>
    /// Compares the file on disk against the load-time stamp.
    ///
    /// All three signals are used, cheapest first. Size and timestamp catch almost everything at
    /// no cost; the hash is what catches a same-size rewrite, which is exactly what a formatter
    /// re-indenting a line produces.
    /// </summary>
    private static async Task<SaveOutcome?> DetectExternalChangeAsync(
        FileInfo info,
        string absolute,
        FileText original,
        CancellationToken cancellationToken)
    {
        bool sizeChanged = original.SizeInBytes != 0 && info.Length != original.SizeInBytes;
        bool timeChanged = original.LastWriteTimeUtc != default && info.LastWriteTimeUtc != original.LastWriteTimeUtc;

        if (!sizeChanged && !timeChanged)
            return null;

        //Size or time moved, so the hash decides. A build that touched the file without changing
        //a byte is not a reason to make the user choose anything.
        if (original.ContentHash.Length > 0)
        {
            byte[] current = await File.ReadAllBytesAsync(absolute, cancellationToken).ConfigureAwait(false);

            if (Convert.ToHexStringLower(SHA256.HashData(current)) == original.ContentHash)
                return null;
        }

        return SaveOutcome.Refused(
            SaveRefusal.ExternallyModified,
            "This file changed on disk after it was opened here.\n\n" +
            "Saving now would discard that change. Reload it, overwrite it, or save a copy.");
    }

    /// <summary>
    /// Temp file beside the target, then <c>File.Replace</c>.
    ///
    /// Same directory so the replace is a rename on one volume rather than a copy, and so the
    /// temp file inherits the target's ACLs. <c>File.Replace</c> rather than delete-then-move
    /// because it preserves the destination's identity and attributes, which is what keeps IDE
    /// file watchers and incremental builds from seeing the file as new.
    /// </summary>
    private static async Task WriteAtomicallyAsync(string absolute, byte[] bytes, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(absolute)!;
        string temporary = Path.Combine(directory, $".flickgit-{Guid.NewGuid():N}.tmp");

        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);

            //ignoreMetadataErrors so a failure to copy an audit ACL does not fail the save
            //itself. No backup file: the content the user is replacing is in Git.
            File.Replace(temporary, absolute, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>
    /// The absolute path, or null when it does not resolve to somewhere inside the repository.
    ///
    /// Public because deleting a file from the working tree needs exactly this guard, and a second
    /// implementation of "is this path really inside the repository" is the one place the two could
    /// disagree.
    /// </summary>
    public static string? ResolveInsideRepository(string repositoryRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || string.IsNullOrWhiteSpace(relativePath))
            return null;

        if (Path.IsPathRooted(relativePath))
            return null;

        try
        {
            string root = Path.GetFullPath(repositoryRoot);
            string combined = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

            //Compared after full-path resolution, so "src/../../elsewhere" is caught. The
            //separator is appended to the root before comparing, or "C:\repo2" would pass a
            //StartsWith check against "C:\repo".
            string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;

            return combined.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
                ? combined
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
            //A temp file left behind is untidy. Throwing over it while already unwinding from a
            //failed save would replace a clear error with a confusing one.
        }
    }
}

/// <summary>Why a save was refused. Each one has a different remedy in the UI.</summary>
public enum SaveRefusal
{
    None,
    Binary,
    OutsideRepository,
    ReparsePoint,
    Missing,
    ExternallyModified,
    WriteFailed,
}

/// <param name="Succeeded">The file was written.</param>
/// <param name="Refusal">Why not, when it was not.</param>
/// <param name="Message">Shown verbatim to the user.</param>
public sealed record SaveOutcome(bool Succeeded, SaveRefusal? Refusal, string? Message)
{
    /// <summary>The new stamp, to keep for the next save. Set only on success.</summary>
    public FileText? Saved { get; init; }

    public static SaveOutcome Refused(SaveRefusal refusal, string message) => new(false, refusal, message);
}
