using System.Runtime.InteropServices;

namespace FlickGit.Shell;

/// <summary>
/// The folder <c>IShellExtInit::Initialize</c> was given, whichever way it was given.
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
    /// The first selected item's path, out of the data object Explorer passes for a click on a
    /// folder rather than on the background.
    ///
    /// Only the first, even on a multiple selection: a Git action applies to a repository, and two
    /// folders selected together are either in the same one — where the first is the right answer —
    /// or in two, where there is no single right answer to give.
    /// </summary>
    public static string? FromDataObject(void* dataObject)
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

            //The HGLOBAL *is* the HDROP. DragQueryFileW with index 0 asks for the first path, and
            //with a null buffer it would return the length instead -- MAX_PATH is enough, and a
            //longer path comes back truncated, which the repository probe then simply fails to find.
            const int MaxPath = 260;

            char* buffer = stackalloc char[MaxPath];

            return DragQueryFileW(medium.Data, 0, buffer, MaxPath) > 0 ? new string(buffer) : null;
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

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHGetPathFromIDListW(nint pidl, char* path);

    [LibraryImport("shell32.dll")]
    private static partial uint DragQueryFileW(nint drop, uint file, char* buffer, uint length);

    [LibraryImport("ole32.dll")]
    private static partial void ReleaseStgMedium(Com.StgMedium* medium);
}
