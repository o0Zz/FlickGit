using System.Text;
using FlickGit.Diff;
using Xunit;

namespace FlickGit.Tests;

/// <summary>
/// Saving an edited file back to the working tree.
///
/// CLAUDE.md, "Testing" asks for exactly this list: "Round-trip save preserves UTF-8 with BOM,
/// UTF-8 without BOM, UTF-16LE, CRLF, LF, mixed endings, and absence of a trailing newline" and
/// "Save is refused when the file changed on disk after load."
///
/// The round-trip assertions compare <b>bytes</b>. Comparing decoded strings would pass on a
/// save that dropped a BOM or rewrote every line ending, which is precisely the failure this
/// class exists to prevent.
/// </summary>
public class WorkingTreeWriterTests : IDisposable
{
    private static readonly FileTextLoader Loader = new();
    private static readonly WorkingTreeWriter Writer = new();

    private readonly string _root;

    public WorkingTreeWriterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"flickgit-write-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Write(string relativePath, byte[] bytes)
    {
        string full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    private async Task<(FileText Loaded, SaveOutcome Outcome, byte[] Bytes)> RoundTrip(
        string relativePath,
        byte[] original,
        Func<string, string>? edit = null)
    {
        string full = Write(relativePath, original);
        FileText loaded = await Loader.LoadAsync(full, CancellationToken.None);

        string newText = edit is null ? loaded.Text : edit(loaded.Text);

        SaveOutcome outcome = await Writer.SaveAsync(
            _root, relativePath, loaded, newText, force: false, CancellationToken.None);

        return (loaded, outcome, File.ReadAllBytes(full));
    }

    // ---- round trips --------------------------------------------------------------

    [Fact]
    public async Task Utf8WithBomRoundTripsByteIdentically()
    {
        //UTF-8 with a BOM and UTF-8 without one are different files to Git. Dropping three bytes
        //shows up as a change to line 1 of a file nobody touched.
        byte[] original = [0xEF, 0xBB, 0xBF, .. "line one\r\nline two\r\n"u8];

        (_, SaveOutcome outcome, byte[] saved) = await RoundTrip("bom.txt", original);

        Assert.True(outcome.Succeeded);
        Assert.Equal(original, saved);
    }

    [Fact]
    public async Task Utf8WithoutBomDoesNotAcquireOne()
    {
        byte[] original = "line one\nline two\n"u8.ToArray();

        (_, SaveOutcome outcome, byte[] saved) = await RoundTrip("nobom.txt", original);

        Assert.True(outcome.Succeeded);
        Assert.Equal(original, saved);
        Assert.NotEqual(0xEF, saved[0]);
    }

    [Fact]
    public async Task Utf16LittleEndianRoundTripsByteIdentically()
    {
        byte[] original = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("hello\r\nworld\r\n")];

        (_, SaveOutcome outcome, byte[] saved) = await RoundTrip("utf16.txt", original);

        Assert.True(outcome.Succeeded);
        Assert.Equal(original, saved);
    }

    [Theory]
    [InlineData("a\r\nb\r\nc\r\n")]
    [InlineData("a\nb\nc\n")]
    [InlineData("a\rb\rc\r")]
    public async Task EveryLineEndingStyleRoundTrips(string content)
    {
        byte[] original = Encoding.UTF8.GetBytes(content);

        (_, SaveOutcome outcome, byte[] saved) = await RoundTrip("endings.txt", original);

        Assert.True(outcome.Succeeded);
        Assert.Equal(original, saved);
    }

    [Fact]
    public async Task AnEditedCrLfFileKeepsCrLfOnEveryLineIncludingTheNewOne()
    {
        //THE regression this class exists for: "Silently normalising line endings on a Windows
        //tool turns a three-line change into a whole-file diff." Only the edited line may differ.
        byte[] original = Encoding.UTF8.GetBytes("one\r\ntwo\r\nthree\r\n");

        (_, SaveOutcome outcome, byte[] saved) = await RoundTrip(
            "crlf.txt", original, text => text.Replace("two", "TWO") + "four\n");

        Assert.True(outcome.Succeeded);

        string result = Encoding.UTF8.GetString(saved);

        Assert.Equal("one\r\nTWO\r\nthree\r\nfour\r\n", result);
        Assert.DoesNotContain("\n\n", result.Replace("\r\n", "\n").Replace("\n", ""));

        //Not one bare LF anywhere.
        for (int i = 0; i < saved.Length; i++)
        {
            if (saved[i] == (byte)'\n')
                Assert.Equal((byte)'\r', saved[i - 1]);
        }
    }

    [Fact]
    public async Task MixedEndingsAreNotNormalisedIntoOneStyle()
    {
        //A file the user did not ask to have tidied up must not come back tidied. New lines get
        //LF, which is the only choice that leaves every existing line untouched.
        byte[] original = Encoding.UTF8.GetBytes("crlf\r\nlf\nmore\n");

        (FileText loaded, SaveOutcome outcome, byte[] saved) = await RoundTrip("mixed.txt", original);

        Assert.Equal(LineEndingStyle.Mixed, loaded.LineEndings);
        Assert.True(outcome.Succeeded);
        Assert.Equal(original, saved);
    }

    [Fact]
    public async Task AFileWithNoTrailingNewlineDoesNotGainOne()
    {
        //Adding a trailing newline is a diff. So is removing one.
        byte[] original = Encoding.UTF8.GetBytes("one\ntwo");

        (_, SaveOutcome outcome, byte[] saved) = await RoundTrip("notrailing.txt", original);

        Assert.True(outcome.Succeeded);
        Assert.Equal(original, saved);
        Assert.NotEqual((byte)'\n', saved[^1]);
    }

    [Fact]
    public async Task AFileWithATrailingNewlineDoesNotLoseIt()
    {
        byte[] original = Encoding.UTF8.GetBytes("one\ntwo\n");

        (_, SaveOutcome outcome, byte[] saved) = await RoundTrip(
            "trailing.txt", original, text => text.TrimEnd('\n'));

        Assert.True(outcome.Succeeded);
        Assert.Equal((byte)'\n', saved[^1]);
    }

    [Fact]
    public async Task NonAsciiContentSurvivesTheRoundTrip()
    {
        byte[] original = Encoding.UTF8.GetBytes("héllo Ω файл\nsecond\n");

        (_, SaveOutcome outcome, byte[] saved) = await RoundTrip("unicode.txt", original);

        Assert.True(outcome.Succeeded);
        Assert.Equal(original, saved);
    }

    [Fact]
    public async Task APathWithSpacesAndUnicodeWorks()
    {
        byte[] original = Encoding.UTF8.GetBytes("x\n");

        (_, SaveOutcome outcome, _) = await RoundTrip("src/Ünïcödé dir/a file.txt", original);

        Assert.True(outcome.Succeeded);
    }

    // ---- refusals -----------------------------------------------------------------

    [Fact]
    public async Task SaveIsRefusedWhenTheFileChangedOnDiskAfterLoad()
    {
        //The IDE reformatted on save, or a build regenerated the file. Overwriting would discard
        //that silently.
        string full = Write("changed.txt", "original\n"u8.ToArray());
        FileText loaded = await Loader.LoadAsync(full, CancellationToken.None);

        await File.WriteAllTextAsync(full, "somebody else wrote this\n");

        SaveOutcome outcome = await Writer.SaveAsync(
            _root, "changed.txt", loaded, "my edit\n", force: false, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(SaveRefusal.ExternallyModified, outcome.Refusal);

        //And the other party's content is still there.
        Assert.Equal("somebody else wrote this\n", await File.ReadAllTextAsync(full));
    }

    [Fact]
    public async Task ForceOverwritesAnExternallyModifiedFile()
    {
        //The explicit "overwrite" choice from the prompt. Available, but never the default.
        string full = Write("forced.txt", "original\n"u8.ToArray());
        FileText loaded = await Loader.LoadAsync(full, CancellationToken.None);

        await File.WriteAllTextAsync(full, "somebody else\n");

        SaveOutcome outcome = await Writer.SaveAsync(
            _root, "forced.txt", loaded, "my edit\n", force: true, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal("my edit\n", await File.ReadAllTextAsync(full));
    }

    [Fact]
    public async Task AFileTouchedButNotChangedIsNotTreatedAsModified()
    {
        //A build that rewrote identical bytes must not make the user answer a dialog. Size and
        //time moved; the hash did not.
        string full = Write("touched.txt", "same\n"u8.ToArray());
        FileText loaded = await Loader.LoadAsync(full, CancellationToken.None);

        File.SetLastWriteTimeUtc(full, DateTime.UtcNow.AddMinutes(5));

        SaveOutcome outcome = await Writer.SaveAsync(
            _root, "touched.txt", loaded, "edited\n", force: false, CancellationToken.None);

        Assert.True(outcome.Succeeded);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData(@"..\outside.txt")]
    public async Task APathThatEscapesTheRepositoryIsRefused(string relativePath)
    {
        //"Never edit files outside the resolved repository root."
        FileText any = Loader.FromBytes("x\n"u8.ToArray());

        SaveOutcome outcome = await Writer.SaveAsync(
            _root, relativePath, any, "y\n", force: false, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(SaveRefusal.OutsideRepository, outcome.Refusal);
    }

    [Fact]
    public async Task AnAbsolutePathIsRefused()
    {
        FileText any = Loader.FromBytes("x\n"u8.ToArray());

        SaveOutcome outcome = await Writer.SaveAsync(
            _root, @"C:\Windows\System32\drivers\etc\hosts", any, "y\n", force: false, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(SaveRefusal.OutsideRepository, outcome.Refusal);
    }

    [Fact]
    public void ASiblingDirectoryWithASharedPrefixIsNotInsideTheRepository()
    {
        //"C:\repo2" must not pass a StartsWith check against "C:\repo".
        Assert.Null(WorkingTreeWriter.ResolveInsideRepository(@"C:\dev\repo", @"..\repo2\file.txt"));
        Assert.NotNull(WorkingTreeWriter.ResolveInsideRepository(@"C:\dev\repo", @"src\file.txt"));
    }

    [Fact]
    public async Task ABinaryFileIsRefused()
    {
        FileText binary = Loader.FromBytes([0x00, 0x01, 0x02]);

        SaveOutcome outcome = await Writer.SaveAsync(
            _root, "logo.png", binary, "text", force: false, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(SaveRefusal.Binary, outcome.Refusal);
    }

    [Fact]
    public async Task AMissingFileIsRefusedRatherThanRecreated()
    {
        //The file was deleted or moved while it was open. Recreating it would resurrect something
        //the user removed.
        FileText loaded = Loader.FromBytes("x\n"u8.ToArray());

        SaveOutcome outcome = await Writer.SaveAsync(
            _root, "never-existed.txt", loaded, "y\n", force: false, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal(SaveRefusal.Missing, outcome.Refusal);
    }

    // ---- mechanics ----------------------------------------------------------------

    [Fact]
    public async Task ASuccessfulSaveReturnsAFreshStampSoTheNextSaveComparesAgainstDisk()
    {
        //Without this, the second save of a session would compare against the load-time stamp and
        //refuse its own previous write as an external change.
        string full = Write("stamp.txt", "one\n"u8.ToArray());
        FileText loaded = await Loader.LoadAsync(full, CancellationToken.None);

        SaveOutcome first = await Writer.SaveAsync(
            _root, "stamp.txt", loaded, "two\n", force: false, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.NotNull(first.Saved);
        Assert.NotEqual(loaded.ContentHash, first.Saved!.ContentHash);

        SaveOutcome second = await Writer.SaveAsync(
            _root, "stamp.txt", first.Saved, "three\n", force: false, CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal("three\n", await File.ReadAllTextAsync(full));
    }

    [Fact]
    public async Task NoTemporaryFileIsLeftBehind()
    {
        //The atomic write puts its temp file in the target's own directory, so a leak would show
        //up beside the user's source.
        string full = Write("src/atomic.txt", "one\n"u8.ToArray());
        FileText loaded = await Loader.LoadAsync(full, CancellationToken.None);

        await Writer.SaveAsync(_root, "src/atomic.txt", loaded, "two\n", force: false, CancellationToken.None);

        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "src"), "*.tmp"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "src"), ".flickgit-*"));
    }

    [Fact]
    public void EncodingIsAssertableWithoutTheFileSystem()
    {
        //The guarantee is byte-level, so the encoder is testable directly rather than only
        //through a round trip.
        FileText crlfWithBom = Loader.FromBytes([0xEF, 0xBB, 0xBF, .. "a\r\n"u8]);

        byte[] encoded = WorkingTreeWriter.Encode(crlfWithBom, "a\nb\n");

        Assert.Equal([0xEF, 0xBB, 0xBF, .. "a\r\nb\r\n"u8], encoded);
    }
}
