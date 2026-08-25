namespace FlickGit.Status;

/// <summary>
/// Counts the lines of an untracked file from disk.
///
/// Necessary because of CLAUDE.md, "Parsing traps": "Untracked files appear in `status`
/// but not in `numstat`." Git has nothing to diff a file it does not track against, so
/// a brand-new 156-line file would show no counts at all — the one row where the count
/// is arguably most useful, since the whole file is the change.
///
/// Both guards are required, and both are about not stalling the commit window on a
/// stray artefact the user is not going to commit anyway: a size ceiling, and a binary
/// sniff so a 900 KB PNG is never scanned for newlines.
/// </summary>
public sealed class UntrackedFileMeasurer
{
    /// <summary>Above this, report the byte size instead of a line count. CLAUDE.md: 1 MB.</summary>
    public const long MaxCountedBytes = 1024 * 1024;

    /// <summary>How much of the file the binary sniff looks at. CLAUDE.md: 8 KB.</summary>
    private const int SniffBytes = 8 * 1024;

    /// <param name="absolutePath">Full path to the untracked file.</param>
    public Measurement Measure(string absolutePath)
    {
        try
        {
            var file = new FileInfo(absolutePath);

            //Deleted, or renamed, between `git status` answering and this running. The row still
            //has to appear -- it is what the status said -- with no counts on it.
            if (!file.Exists)
                return new Measurement(null, null, false, null);

            if (file.Length > MaxCountedBytes)
                return new Measurement(null, null, false, file.Length);

            using FileStream stream = File.Open(
                absolutePath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,

                    //ReadWrite share: the file is very likely open in the user's editor,
                    //and refusing to measure it because of that would be absurd.
                    Share = FileShare.ReadWrite | FileShare.Delete,
                });

            byte[] buffer = new byte[SniffBytes];
            int read = stream.Read(buffer, 0, buffer.Length);

            //A NUL byte inside the first 8 KB. The same heuristic Git itself uses, and it
            //is right about every format that matters here: images, archives, PDFs and
            //compiled output all carry one early; UTF-8 and UTF-16-with-BOM text do not
            //(UTF-16 without a BOM does, and would be misreported -- an acceptable trade
            //against scanning every binary in bin/).
            if (buffer.AsSpan(0, read).IndexOf((byte)0) >= 0)
                return new Measurement(null, null, true, file.Length);

            int lines = CountLines(buffer, read, stream);

            //Added = every line, removed = nothing. This is a file that did not exist,
            //which is exactly what "+156 -0" says.
            return new Measurement(lines, 0, false, file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            //Locked, gone, or unreadable. The row still has to appear -- an untracked file
            //the tool cannot read is precisely the kind of thing the user wants to see
            //listed and left unticked.
            return new Measurement(null, null, false, null);
        }
    }

    private static int CountLines(byte[] firstChunk, int firstChunkLength, FileStream stream)
    {
        int lines = 0;
        bool sawAnyByte = false;
        byte last = 0;

        void CountChunk(ReadOnlySpan<byte> chunk)
        {
            foreach (byte b in chunk)
            {
                if (b == (byte)'\n')
                    lines++;

                last = b;
                sawAnyByte = true;
            }
        }

        CountChunk(firstChunk.AsSpan(0, firstChunkLength));

        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            CountChunk(buffer.AsSpan(0, read));

        //A file whose last line has no trailing newline still contains that line. Git
        //counts it too (and marks it "\ No newline at end of file" in a diff), so not
        //counting it here would put this column one out of step with `git diff --numstat`
        //on the same content once the file is added.
        if (sawAnyByte && last != (byte)'\n')
            lines++;

        return lines;
    }

    /// <param name="AddedLines">Line count, or null when binary, oversized or unreadable.</param>
    /// <param name="RemovedLines">Always 0 for a file that did not exist before, else null.</param>
    /// <param name="IsBinary">A NUL byte was found in the first 8 KB.</param>
    /// <param name="SizeInBytes">File size, or null when it could not be read.</param>
    public readonly record struct Measurement(
        int? AddedLines,
        int? RemovedLines,
        bool IsBinary,
        long? SizeInBytes);
}
