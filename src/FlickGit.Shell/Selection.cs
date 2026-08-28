using System.Runtime.InteropServices;

namespace FlickGit.Shell;

/// <param name="Paths">Every path that was read, in Explorer's own order. Never empty.</param>
/// <param name="Selected">
/// How many items Explorer actually had. Greater than <paramref name="Paths"/>'s length only when the
/// walk stopped at the command-line budget, and that difference is the one case in which nothing may
/// act on what was read — see <see cref="Launcher"/>, which reports the count instead of running a
/// shorter command.
/// </param>
internal readonly record struct SelectedItems(string[] Paths, int Selected);

/// <summary>
/// What <c>IShellExtInit::Initialize</c> was given, whichever way it was given.
///
/// <b>This replaced a six-hop COM chain.</b> Under <c>IExplorerCommand</c>, a click on a folder's
/// background arrived with no item at all, and the folder had to be dug out of the site Explorer set
/// on the handler: service provider, shell browser, active view, folder view, persist folder, PIDL,
/// path — every hop able to fail for its own reason. <c>IShellExtInit</c> is simply handed the PIDL.
/// That is the second reason the interface swap was worth making, after the placement it was made
/// for.
///
/// Failure is always null. A handler that throws into <c>explorer.exe</c> takes the desktop with it.
/// </summary>
internal static unsafe partial class Selection
{
    /// <summary>
    /// The clicked folder's own path, for a click on the background of a view.
    /// </summary>
    public static string? FromPidl(nint pidl)
    {
        if (pidl == 0)
            return null;

        try
        {
            //MAX_PATH. SHGetPathFromIDListW has no long-path form, so a folder deeper than this
            //resolves to nothing and no menu is drawn -- which is the honest outcome rather than a
            //truncated path that would act on the wrong directory.
            //
            //Asymmetric with FromDataObject below, which no longer has this cap: SHGetPathFromIDListEx
            //would close the gap and is not worth a second API for a background click, where the path
            //is the folder Explorer is showing rather than something the user picked out.
            const int MaxPath = 260;

            char* buffer = stackalloc char[MaxPath];

            return SHGetPathFromIDListW(pidl, buffer) ? new string(buffer) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// <b>Every</b> selected item's path, out of the data object Explorer passes for a click on items
    /// rather than on the background.
    ///
    /// This used to read index 0 and stop, on the reasoning that a Git action applies to a repository
    /// and the first item answers for it. That is true of the entries that act on a repository, and it
    /// is what still happens to them — <c>InvokeCommand</c> hands them the first path. It was never
    /// true of Add and Remove, which act on the items themselves: selecting seven files and pressing
    /// Add staged one of them and reported success.
    ///
    /// <b>The walk is bounded by the command line it has to fit into.</b> A Ctrl+A over a large folder
    /// is a real click, and reading two hundred thousand paths on the thread painting Explorer's menu
    /// is not something the 20 ms budget survives. <paramref name="budget"/> stops the reading;
    /// <see cref="SelectedItems.Selected"/> still carries the true count, so what follows can refuse
    /// honestly rather than act on the prefix that happened to fit.
    /// </summary>
    public static SelectedItems? FromDataObject(void* dataObject, int budget)
    {
        if (dataObject is null)
            return null;

        var format = new Com.FormatEtc
        {
            Format = Com.CfHdrop,
            TargetDevice = 0,
            Aspect = Com.DvAspectContent,
            Index = -1,
            Tymed = Com.TymedHGlobal,
        };

        var medium = default(Com.StgMedium);

        try
        {
            //IDataObject slot 3: GetData(FORMATETC*, STGMEDIUM*). Slots 0-2 are IUnknown.
            int hr = ((delegate* unmanaged<void*, Com.FormatEtc*, Com.StgMedium*, int>)Com.Vtable(dataObject)[3])(
                dataObject, &format, &medium);

            if (hr < 0 || medium.Data == 0)
                return null;

            //The HGLOBAL *is* the HDROP. Index 0xFFFFFFFF asks how many items it holds, which is the
            //question this file never used to ask.
            uint count = DragQueryFileW(medium.Data, 0xFFFFFFFF, null, 0);

            if (count == 0)
                return null;

            var paths = new List<string>((int)Math.Min(count, 64));
            int length = 0;

            for (uint i = 0; i < count; i++)
            {
                //A null buffer asks for the length instead of the path, in characters and without the
                //terminator. MAX_PATH is gone from here on purpose: a 300-character path used to come
                //back truncated, and a truncated path is a path that names something else.
                uint needed = DragQueryFileW(medium.Data, i, null, 0);

                if (needed == 0)
                    continue;

                //Quotes and a separator, which is what the path will cost on the command line.
                length += (int)needed + 3;

                //Stop rather than trim: the first item is always read, so QueryContextMenu still has a
                //path to look a repository up from, and everything past the budget is left unread with
                //the true count carried out instead.
                if (length > budget && paths.Count > 0)
                    break;

                var buffer = new char[needed + 1];

                fixed (char* target = buffer)
                {
                    if (DragQueryFileW(medium.Data, i, target, (uint)buffer.Length) == 0)
                        continue;

                    paths.Add(new string(target));
                }
            }

            return paths.Count == 0 ? null : new SelectedItems([.. paths], (int)count);
        }
        catch
        {
            return null;
        }
        finally
        {
            //ReleaseStgMedium, never GlobalFree: the medium may carry a pUnkForRelease, and only
            //this knows which.
            if (medium.Data != 0 || medium.UnkForRelease != 0)
                ReleaseStgMedium(&medium);
        }
    }

    /// <summary>
    /// Whether a path is a directory, in one syscall.
    ///
    /// <c>GetFileAttributesW</c> rather than <c>Directory.Exists</c>, which is the same question asked
    /// through managed path normalisation — this runs once per selected item inside
    /// <c>explorer.exe</c>, and the overlay handler is already the precedent for going straight at the
    /// attribute.
    ///
    /// A path that cannot be classified counts as a file, which is today's rule: a deleted item or one
    /// on a disconnected share offers the smaller menu rather than the wrong one.
    /// </summary>
    public static bool IsDirectory(string path)
    {
        const uint InvalidFileAttributes = 0xFFFFFFFF;
        const uint FileAttributeDirectory = 0x00000010;

        try
        {
            uint attributes = GetFileAttributesW(path);

            return attributes != InvalidFileAttributes && (attributes & FileAttributeDirectory) != 0;
        }
        catch
        {
            return false;
        }
    }

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHGetPathFromIDListW(nint pidl, char* path);

    [LibraryImport("shell32.dll")]
    private static partial uint DragQueryFileW(nint drop, uint file, char* buffer, uint length);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFileAttributesW(string path);

    [LibraryImport("ole32.dll")]
    private static partial void ReleaseStgMedium(Com.StgMedium* medium);
}
