using System.Text;
using FlickGit.Diff;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Encoding, BOM and line-ending detection.
///
/// This is the least glamorous test file in the repository and the one guarding the most
/// damage. CLAUDE.md, "Live Editing the Working Tree": "Silently normalising line endings on
/// a Windows tool turns a three-line change into a whole-file diff, and it will happen on the
/// first CRLF repository otherwise." Phase 2 writes files back; these assertions are what
/// make that safe, and they are here before the writer is.
/// </summary>
public class FileTextLoaderTests
{
    private static readonly FileTextLoader Loader = new();

    [Fact]
    public void Utf8WithBomIsDetectedAndTheBomIsNotPartOfTheText()
    {
        //UTF-8 with a BOM and UTF-8 without one are different files to Git. Dropping three
        //bytes on save shows up as a change to line 1 of a file nobody touched.
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. "hello"u8];

        FileText text = Loader.FromBytes(bytes);

        Assert.True(text.HasByteOrderMark);
        Assert.Equal("hello", text.Text);
    }

    [Fact]
    public void Utf8WithoutBomIsDetected()
    {
        FileText text = Loader.FromBytes("hello"u8.ToArray());

        Assert.False(text.HasByteOrderMark);
        Assert.Equal("hello", text.Text);
    }

    [Fact]
    public void Utf16LittleEndianIsDecodedRatherThanTreatedAsBinary()
    {
        byte[] bytes = new UnicodeEncoding(bigEndian: false, byteOrderMark: true)
            .GetPreamble()
            .Concat(Encoding.Unicode.GetBytes("hello"))
            .ToArray();

        FileText text = Loader.FromBytes(bytes);

        Assert.True(text.HasByteOrderMark);
        Assert.Equal("hello", text.Text);

        //UTF-16 text contains NUL bytes for ASCII characters. The binary sniff has to run
        //*after* the BOM check, or every UTF-16 file in the repository reads as binary.
        Assert.False(text.IsBinary);
    }

    [Fact]
    public void Utf16BigEndianIsDecoded()
    {
        byte[] bytes = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes("hi")];

        FileText text = Loader.FromBytes(bytes);

        Assert.Equal("hi", text.Text);
        Assert.False(text.IsBinary);
    }

    [Fact]
    public void Utf32LittleEndianIsNotMisreadAsUtf16()
    {
        //FF FE 00 00 starts with the UTF-16LE BOM, so testing the shorter mark first would
        //mis-detect every little-endian UTF-32 file. The order of the checks is the fix.
        byte[] bytes = [0xFF, 0xFE, 0x00, 0x00, .. Encoding.UTF32.GetBytes("hi")];

        FileText text = Loader.FromBytes(bytes);

        Assert.Equal("hi", text.Text);
    }

    [Fact]
    public void NonUtf8BytesFallBackToAnEncodingThatRoundTripsThem()
    {
        //0xFF is not valid UTF-8. Latin-1 is chosen over the machine's ANSI code page
        //because it round-trips every byte 0x00-0xFF, so even a wrong guess about what the
        //bytes *mean* still writes back exactly what was read.
        byte[] bytes = [0x48, 0xFF, 0x49];

        FileText text = Loader.FromBytes(bytes);

        Assert.False(text.IsBinary);
        Assert.Equal(bytes, text.Encoding.GetBytes(text.Text));
    }

    [Fact]
    public void NulByteMakesTheFileBinaryAndNoTextIsDecoded()
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x02];

        FileText text = Loader.FromBytes(bytes);

        Assert.True(text.IsBinary);
        Assert.Equal(string.Empty, text.Text);
    }

    [Theory]
    [InlineData("a\r\nb\r\nc", LineEndingStyle.CrLf)]
    [InlineData("a\nb\nc", LineEndingStyle.Lf)]
    [InlineData("a\rb\rc", LineEndingStyle.Cr)]
    [InlineData("a\r\nb\nc", LineEndingStyle.Mixed)]
    [InlineData("single line", LineEndingStyle.None)]
    [InlineData("", LineEndingStyle.None)]
    public void LineEndingsAreDetected(string raw, LineEndingStyle expected) =>
        Assert.Equal(expected, FileTextLoader.DetectLineEndings(raw));

    [Fact]
    public void CrLfSurvivesDetectionEvenThoughTheTextIsNormalisedToLf()
    {
        //The text is normalised to LF for diffing and editing; NewLine carries the original
        //back so a save restores it. Both halves have to hold or the round trip rewrites the
        //whole file.
        FileText text = Loader.FromBytes(Encoding.UTF8.GetBytes("a\r\nb\r\n"));

        Assert.Equal(LineEndingStyle.CrLf, text.LineEndings);
        Assert.Equal("a\nb\n", text.Text);
        Assert.Equal("\r\n", text.NewLine);
    }

    [Fact]
    public void MixedEndingsCaptureEveryLinesOwnTerminator()
    {
        //A mixed file cannot be rebuilt from one style, so the loader records what followed each
        //line individually. Without this the writer has to pick a side, and picking either one
        //rewrites every line of the other kind -- a one-line edit becoming a whole-file diff,
        //which is the exact failure CLAUDE.md warns about.
        FileText text = Loader.FromBytes(Encoding.UTF8.GetBytes("a\r\nb\nc"));

        Assert.Equal(LineEndingStyle.Mixed, text.LineEndings);
        Assert.NotNull(text.PerLineEndings);
        Assert.Equal(["\r\n", "\n", ""], text.PerLineEndings);

        //NewLine is the dominant style, used only for lines the user adds. Existing lines keep
        //their own terminator from PerLineEndings.
        Assert.Equal("\r\n", text.NewLine);
    }

    [Fact]
    public void AUniformFileCarriesNoPerLineEndings()
    {
        //One list entry per line is an allocation worth paying only where it is needed. A
        //uniformly terminated file is rebuilt exactly from NewLine alone.
        Assert.Null(Loader.FromBytes(Encoding.UTF8.GetBytes("a\r\nb\r\n")).PerLineEndings);
        Assert.Null(Loader.FromBytes(Encoding.UTF8.GetBytes("a\nb\n")).PerLineEndings);
    }

    [Fact]
    public void TheDominantStyleOfAMixedFileIsTheMostFrequentOne()
    {
        FileText mostlyLf = Loader.FromBytes(Encoding.UTF8.GetBytes("a\nb\nc\nd\r\n"));

        Assert.Equal(LineEndingStyle.Mixed, mostlyLf.LineEndings);
        Assert.Equal("\n", mostlyLf.NewLine);
    }

    [Theory]
    [InlineData("a\nb\n", true)]
    [InlineData("a\nb", false)]
    [InlineData("", false)]
    public void TrailingNewlinePresenceIsRecorded(string raw, bool expected)
    {
        //Adding a trailing newline is a diff, and so is removing one. A save that
        //"tidied up" either way would show as a change on the last line.
        FileText text = Loader.FromBytes(Encoding.UTF8.GetBytes(raw));

        Assert.Equal(expected, text.EndsWithNewline);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("one", 1)]
    [InlineData("one\ntwo", 2)]
    [InlineData("one\ntwo\n", 2)]
    [InlineData("one\ntwo\nthree\n", 3)]
    public void LineCountTreatsAnUnterminatedLastLineAsALine(string raw, int expected)
    {
        //Git counts it too, and marks it "\ No newline at end of file" in a diff. Not
        //counting it would put this column one out of step with `git diff --numstat`.
        FileText text = Loader.FromBytes(Encoding.UTF8.GetBytes(raw));

        Assert.Equal(expected, text.LineCount);
    }

    [Fact]
    public void ContentHashIsRecordedForTheExternalModificationGuard()
    {
        //The authoritative half of the "did the IDE reformat this behind us?" check that
        //Phase 2's save path depends on.
        FileText first = Loader.FromBytes(Encoding.UTF8.GetBytes("hello"));
        FileText same = Loader.FromBytes(Encoding.UTF8.GetBytes("hello"));
        FileText other = Loader.FromBytes(Encoding.UTF8.GetBytes("hello!"));

        Assert.NotEmpty(first.ContentHash);
        Assert.Equal(first.ContentHash, same.ContentHash);
        Assert.NotEqual(first.ContentHash, other.ContentHash);
    }

    [Fact]
    public void EmptyIsUsableAsAnAbsentLeftHandSide()
    {
        //An untracked file has no base. That is an empty left pane, not an error.
        Assert.Equal(string.Empty, FileText.Empty.Text);
        Assert.False(FileText.Empty.IsBinary);
        Assert.Equal(0, FileText.Empty.LineCount);
    }
}
