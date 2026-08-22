using System.Security.Cryptography;
using System.Text;

namespace FlickGit.Diff;

/// <summary>
/// Reads a file into a <see cref="FileText"/>, detecting encoding, BOM, line endings and
/// trailing newline.
///
/// Detection order matters. A BOM is decisive when present — it is a declaration, not a
/// guess. Without one, the bytes are tested for valid UTF-8 before falling back to the
/// system code page, because UTF-8 is what a modern repository contains and a
/// mis-detection re-encodes every non-ASCII character in the file on the first save.
/// </summary>
public sealed class FileTextLoader
{
    /// <summary>Bytes examined by the binary sniff. Matches Git's own heuristic window.</summary>
    private const int SniffBytes = 8 * 1024;

    public FileText FromBytes(
        byte[] bytes,
        long? sizeInBytes = null,
        DateTime? lastWriteTimeUtc = null)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        //THE ordering constraint in this method: the byte-order mark is examined *before* the
        //binary sniff, not after.
        //
        //A BOM is a declaration, not a guess, and once it says UTF-16 the NUL bytes that
        //follow are the high halves of ASCII characters rather than evidence of a binary file.
        //Sniffing first reports every UTF-16 file in the repository as binary and refuses to
        //diff it, which is exactly what the FileTextLoaderTests UTF-16 cases exist to catch.
        (Encoding encoding, bool hasBom, int preamble) = DetectBom(bytes);

        if (!hasBom)
        {
            //No declaration, so the bytes have to speak for themselves. A NUL early on means
            //binary: the same heuristic Git uses, and right about every format that matters
            //here. UTF-16 *without* a BOM is the known false positive, and it is a better
            //trade than scanning every PNG in the tree for newlines.
            int sniffLength = Math.Min(bytes.Length, SniffBytes);

            if (bytes.AsSpan(0, sniffLength).IndexOf((byte)0) >= 0)
            {
                return new FileText
                {
                    Text = string.Empty,
                    Encoding = new UTF8Encoding(false),
                    HasByteOrderMark = false,
                    LineEndings = LineEndingStyle.None,
                    EndsWithNewline = false,
                    SizeInBytes = sizeInBytes ?? bytes.Length,
                    LastWriteTimeUtc = lastWriteTimeUtc ?? default,
                    ContentHash = hash,
                    IsBinary = true,
                };
            }

            //Only now, on bytes already known not to be binary, is the UTF-8 validation worth
            //running: it walks the whole array. If they are valid UTF-8 they are UTF-8, since
            //no other encoding produces valid multi-byte UTF-8 sequences by accident often
            //enough to matter, and pure ASCII passes trivially.
            encoding = IsValidUtf8(bytes)
                ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)

                //Otherwise a legacy single-byte file. Latin-1 rather than the machine's ANSI
                //code page, because Latin-1 round-trips every byte 0x00-0xFF unchanged, so
                //even if the guess is wrong about what the bytes *mean*, saving writes back
                //exactly what was read.
                : Encoding.Latin1;
        }

        string raw = encoding.GetString(bytes, preamble, bytes.Length - preamble);

        LineEndingStyle endings = DetectLineEndings(raw);
        bool endsWithNewline = raw.Length > 0 && (raw[^1] == '\n' || raw[^1] == '\r');

        //Only for a mixed file. A uniformly-terminated one is rebuilt exactly from
        //FileText.NewLine alone, so paying a list allocation per line for it would buy
        //nothing.
        (IReadOnlyList<string>? perLine, string? dominant) = endings == LineEndingStyle.Mixed
            ? CaptureLineEndings(raw)
            : (null, null);

        //Normalised to LF in memory. Every consumer -- DiffPlex, the editor control, the
        //hunk generator -- wants one line-ending convention, and FileText.NewLine carries
        //the original so a save can put it back.
        string normalised = raw.Replace("\r\n", "\n").Replace('\r', '\n');

        return new FileText
        {
            Text = normalised,
            Encoding = encoding,
            HasByteOrderMark = hasBom,
            LineEndings = endings,
            EndsWithNewline = endsWithNewline,
            SizeInBytes = sizeInBytes ?? bytes.Length,
            LastWriteTimeUtc = lastWriteTimeUtc ?? default,
            ContentHash = hash,
            IsBinary = false,
            PerLineEndings = perLine,
            DominantNewLine = dominant,
        };
    }

    /// <summary>
    /// Records the terminator that followed each line, and which one was most common.
    ///
    /// The last entry is the empty string when the file does not end with a newline, so the
    /// list always has one entry per line and a caller can index it without a special case.
    /// </summary>
    internal static (IReadOnlyList<string> PerLine, string Dominant) CaptureLineEndings(string raw)
    {
        var endings = new List<string>();
        int crlf = 0, lf = 0, cr = 0;

        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];

            if (c == '\r')
            {
                if (i + 1 < raw.Length && raw[i + 1] == '\n')
                {
                    endings.Add("\r\n");
                    crlf++;
                    i++;
                }
                else
                {
                    endings.Add("\r");
                    cr++;
                }
            }
            else if (c == '\n')
            {
                endings.Add("\n");
                lf++;
            }
        }

        //The final line, when it is not terminated. It still exists and still needs a slot.
        if (raw.Length > 0 && raw[^1] is not ('\r' or '\n'))
            endings.Add(string.Empty);

        string dominant = crlf >= lf && crlf >= cr ? "\r\n"
            : lf >= cr ? "\n"
            : "\r";

        return (endings, dominant);
    }

    public async Task<FileText> LoadAsync(string absolutePath, CancellationToken cancellationToken)
    {
        var info = new FileInfo(absolutePath);
        byte[] bytes = await File.ReadAllBytesAsync(absolutePath, cancellationToken).ConfigureAwait(false);

        //Size and timestamp read from the same FileInfo snapshot the bytes came from, so
        //the external-change stamp cannot straddle a write that happened mid-read.
        return FromBytes(bytes, info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>
    /// Examines the byte-order mark alone.
    ///
    /// Split out from the no-BOM guesswork so the caller can run the binary sniff between the
    /// two, per the ordering note in <see cref="FromBytes"/>. When no BOM is found the
    /// returned encoding is a placeholder the caller replaces.
    /// </summary>
    /// <returns>The encoding, whether a BOM was present, and how many bytes to skip.</returns>
    private static (Encoding Encoding, bool HasBom, int PreambleLength) DetectBom(byte[] bytes)
    {
        //UTF-8 BOM.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), true, 3);

        //UTF-32 before UTF-16: FF FE 00 00 starts with the UTF-16LE BOM, so testing the
        //shorter mark first would mis-detect every little-endian UTF-32 file.
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true), true, 4);

        if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), true, 4);

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), true, 2);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), true, 2);

        //No BOM. The caller chooses between UTF-8 and Latin-1, after the binary sniff.
        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), false, 0);
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// The dominant style, or <see cref="LineEndingStyle.Mixed"/> when more than one is
    /// present. Counted over the whole file rather than sampled: a file whose first
    /// hundred lines are LF and whose remainder is CRLF is exactly the case that needs
    /// to report Mixed.
    /// </summary>
    internal static LineEndingStyle DetectLineEndings(string raw)
    {
        int crlf = 0, lf = 0, cr = 0;

        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];

            if (c == '\r')
            {
                if (i + 1 < raw.Length && raw[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (c == '\n')
            {
                lf++;
            }
        }

        int styles = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);

        if (styles == 0)
            return LineEndingStyle.None;

        if (styles > 1)
            return LineEndingStyle.Mixed;

        return crlf > 0 ? LineEndingStyle.CrLf : lf > 0 ? LineEndingStyle.Lf : LineEndingStyle.Cr;
    }
}
