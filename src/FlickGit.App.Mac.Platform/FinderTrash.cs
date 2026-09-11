using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FlickGit.App.Infrastructure;
using FlickGit.Diff;
using FlickGit.Logging;

namespace FlickGit.App.Mac;

/// <summary>
/// Puts a file or a folder in the Trash, through <c>NSFileManager</c>.
///
/// <b>Not a move to <c>~/.Trash</c>.</b> That is what makes the difference between a file the user
/// can put back and a file that is merely somewhere else: "Put Back" is driven by metadata Finder
/// records when <c>trashItemAtURL:</c> does the move, and a plain rename does not write it. This is
/// the only route by which FlickGit removes a file Git has never seen, so the undo has to be the one
/// the user already knows.
///
/// <c>trashItemAtURL:</c> takes a directory as readily as a file, so the folder half of the menu's
/// Delete needs no branch here — unlike Windows, where the bin is reached through two different
/// entry points.
///
/// The two refusals are the Windows implementation's, unchanged and for the same reasons: nothing
/// outside the resolved repository root, and never through a symlink — following one would delete
/// whatever it points at, somewhere the user never named.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class FinderTrash(ILog log) : ITrash
{
    public DeleteOutcome Delete(string repositoryRoot, string relativePath)
    {
        string? absolute = WorkingTreeWriter.ResolveInsideRepository(repositoryRoot, relativePath);

        if (absolute is null)
            return DeleteOutcome.Refused($"{relativePath} is outside {repositoryRoot} and will not be deleted.");

        //FileSystemInfo, because a FileInfo over a directory reports Exists false -- which here
        //would answer "already gone" about a folder that is plainly there, and succeed having done
        //nothing.
        FileSystemInfo info = Directory.Exists(absolute)
            ? new DirectoryInfo(absolute)
            : new FileInfo(absolute);

        if (!info.Exists)
            //Already gone. Nothing to report: the state the caller wanted is the state on disk.
            return DeleteOutcome.Ok();

        if (info.LinkTarget is not null || WorkingTreeWriter.CrossesReparsePoint(repositoryRoot, absolute))
            return DeleteOutcome.Refused($"{relativePath} is a symlink, or is reached through one. FlickGit will not delete through one.");

        try
        {
            return Trash(absolute)
                ? DeleteOutcome.Ok()

                //Null message: Finder has already said why in its own words, and a second sentence
                //paraphrasing it would be worse than none. The Windows implementation makes the same
                //choice for the same reason.
                : DeleteOutcome.Refused(null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            log.Error($"Could not reach NSFileManager: {ex.Message}");

            return DeleteOutcome.Refused($"{relativePath} could not be moved to the Trash.");
        }
    }

    /// <summary>
    /// <c>[[NSFileManager defaultManager] trashItemAtURL:url resultingItemURL:nil error:&amp;error]</c>,
    /// through the Objective-C runtime.
    ///
    /// <b>Every <c>objc_msgSend</c> needs its own declaration.</b> It is not a normal variadic
    /// function — the calling convention depends on the argument types, so one declaration reused
    /// with different signatures passes arguments in the wrong registers and returns nonsense rather
    /// than failing. Hence one entry point per shape below, all aliased onto the same export.
    ///
    /// <b>Unverified.</b> Written without a Mac to run it on. The shapes follow from the selectors,
    /// but this is the one file in the port whose correctness has not been observed, so it is worth
    /// exercising deliberately -- delete an untracked file, then check Finder offers Put Back, and
    /// again on a folder.
    /// </summary>
    private static bool Trash(string absolute)
    {
        //The NSString is owned by us -- it came from alloc/initWithUTF8String:, which returns a
        //+1 reference, unlike the NSURL, which is autoreleased. Released once the URL exists,
        //because fileURLWithPath: has copied what it needs by then. Without this the resident
        //process leaked one string per delete.
        IntPtr text = SendString(
            SendGet(GetClass("NSString"), Selector("alloc")),
            Selector("initWithUTF8String:"),
            absolute);

        if (text == IntPtr.Zero)
            return false;

        try
        {
            IntPtr url = SendPointer(GetClass("NSURL"), Selector("fileURLWithPath:"), text);

            if (url == IntPtr.Zero)
                return false;

            IntPtr manager = SendGet(GetClass("NSFileManager"), Selector("defaultManager"));

            return SendTrash(
                manager,
                Selector("trashItemAtURL:resultingItemURL:error:"),
                url,
                IntPtr.Zero,
                IntPtr.Zero);
        }
        finally
        {
            SendGet(text, Selector("release"));
        }
    }

    private const string Objc = "/usr/lib/libobjc.A.dylib";

    [LibraryImport(Objc, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GetClass(string name);

    [LibraryImport(Objc, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr Selector(string name);

    [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendGet(IntPtr receiver, IntPtr selector);

    [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
    private static partial IntPtr SendPointer(IntPtr receiver, IntPtr selector, IntPtr argument);

    [LibraryImport(Objc, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr SendString(IntPtr receiver, IntPtr selector, string argument);

    /// <summary>
    /// <c>I1</c>, not <c>Bool</c>. Objective-C's <c>BOOL</c> is a signed char returned in the low
    /// byte of <c>x0</c> with the upper bits undefined, while <c>UnmanagedType.Bool</c> reads a
    /// four-byte Win32 <c>BOOL</c> — so a failed trash could be read as success, leaving the file
    /// where it was while the caller reported it gone.
    /// </summary>
    [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool SendTrash(
        IntPtr receiver,
        IntPtr selector,
        IntPtr url,
        IntPtr resultingUrl,
        IntPtr error);
}
