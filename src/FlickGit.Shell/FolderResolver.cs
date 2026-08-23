using System.Runtime.InteropServices;

namespace FlickGit.Shell;

/// <summary>
/// Which folder the menu is being built for.
///
/// Two completely different answers depending on where the user clicked, and both are needed —
/// CLAUDE.md, "Repository Detection": right-clicking the repository root, a subdirectory inside it,
/// and "the Explorer background while browsing inside a repository" all have to work.
///
/// <list type="number">
/// <item><description><b>A folder was clicked.</b> Explorer passes an <c>IShellItemArray</c>, and
/// the path is two calls away.</description></item>
/// <item><description><b>The background was clicked.</b> The array is <c>null</c>, and the folder
/// has to be dug out of the site Explorer set on the handler: service provider, shell browser,
/// active view, folder view, persist folder, PIDL, path. Six hops, and every one of them can
/// legitimately fail.</description></item>
/// </list>
///
/// Failure is always null, never an exception. A handler that throws into
/// <c>explorer.exe</c> takes the desktop with it.
/// </summary>
internal static unsafe partial class FolderResolver
{
    /// <summary>
    /// The path of the first item in <paramref name="items"/>, or of the folder the view is showing
    /// when <paramref name="items"/> is null.
    /// </summary>
    public static string? Resolve(void* items, void* site)
    {
        try
        {
            return items is not null ? FromItems(items) : FromSite(site);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// <c>IShellItemArray::GetItemAt(0)</c> then <c>IShellItem::GetDisplayName(SIGDN_FILESYSPATH)</c>.
    ///
    /// Only the first item, even on a multiple selection: a Git action applies to a repository, and
    /// two folders selected together are either in the same one — where the first is the right answer
    /// — or in two, where there is no single right answer to give.
    /// </summary>
    private static string? FromItems(void* items)
    {
        //IShellItemArray slot 8: GetItemAt(DWORD, IShellItem**).
        //
        //Counted, not guessed. The interface has seven methods after IUnknown's three:
        //BindToHandler(3), GetPropertyStore(4), GetPropertyDescriptionList(5), GetAttributes(6),
        //GetCount(7), GetItemAt(8), EnumItems(9). Slot 9 is EnumItems, whose one argument is a
        //pointer -- calling it with a DWORD in that register and reading the result back as an
        //IShellItem is an access violation inside explorer.exe.
        void* item = null;

        int hr = ((delegate* unmanaged<void*, uint, void**, int>)Com.Vtable(items)[8])(items, 0, &item);

        if (hr < 0 || item is null)
            return null;

        try
        {
            return DisplayName(item);
        }
        finally
        {
            Com.Release(item);
        }
    }

    /// <summary>
    /// <c>IShellItem::GetDisplayName</c>, slot 5.
    ///
    /// A file-system path, so anything virtual — This PC, a library, a search result, an MTP device —
    /// fails here rather than returning something that is not a path. That is the correct answer for
    /// all of them.
    /// </summary>
    private static string? DisplayName(void* item)
    {
        char* buffer = null;

        int hr = ((delegate* unmanaged<void*, uint, char**, int>)Com.Vtable(item)[5])(
            item, Com.SigdnFileSysPath, &buffer);

        if (hr < 0 || buffer is null)
            return null;

        try
        {
            return Marshal.PtrToStringUni((nint)buffer);
        }
        finally
        {
            //Ours to free, per IShellItem::GetDisplayName.
            Marshal.FreeCoTaskMem((nint)buffer);
        }
    }

    /// <summary>
    /// The folder the active view is showing, for a click on the background.
    ///
    /// The chain is fixed by the shell and each step is one <c>QueryInterface</c> or one vtable slot:
    ///
    /// <code>
    /// site → IServiceProvider
    ///      → QueryService(SID_SShellBrowser) → IShellBrowser
    ///      → QueryActiveShellView            → IShellView
    ///      → QueryInterface(IFolderView)     → IFolderView
    ///      → GetFolder(IPersistFolder2)      → IPersistFolder2
    ///      → GetCurFolder                    → PIDL
    ///      → SHGetPathFromIDListW            → path
    /// </code>
    ///
    /// Written out flat rather than factored: every hop releases a different pointer on a different
    /// failure, and a helper that took a slot index would hide exactly the part worth reading.
    /// </summary>
    private static string? FromSite(void* site)
    {
        if (site is null)
            return null;

        if (Com.QueryInterface(site, Com.IServiceProvider, out void* provider) < 0 || provider is null)
            return null;

        void* browser = null;
        void* view = null;
        void* folderView = null;
        void* persist = null;
        nint pidl = 0;

        try
        {
            //IServiceProvider slot 3: QueryService(REFGUID, REFIID, void**). The shell browser is
            //asked for by its own IID as the service id.
            Guid service = Com.IShellBrowser;
            Guid iid = Com.IShellBrowser;

            int hr = ((delegate* unmanaged<void*, Guid*, Guid*, void**, int>)Com.Vtable(provider)[3])(
                provider, &service, &iid, &browser);

            if (hr < 0 || browser is null)
                return null;

            //IShellBrowser slot 15: QueryActiveShellView(IShellView**). Slots 0-2 are IUnknown, 3-4
            //are IOleWindow, and IShellBrowser's own eleven methods start at 5.
            hr = ((delegate* unmanaged<void*, void**, int>)Com.Vtable(browser)[15])(browser, &view);

            if (hr < 0 || view is null)
                return null;

            if (Com.QueryInterface(view, Com.IFolderView, out folderView) < 0 || folderView is null)
                return null;

            //IFolderView slot 5: GetFolder(REFIID, void**).
            Guid persistIid = Com.IPersistFolder2;

            hr = ((delegate* unmanaged<void*, Guid*, void**, int>)Com.Vtable(folderView)[5])(
                folderView, &persistIid, &persist);

            if (hr < 0 || persist is null)
                return null;

            //IPersistFolder2 slot 5: GetCurFolder(PIDLIST_ABSOLUTE*). Slot 3 is IPersist::GetClassID,
            //slot 4 IPersistFolder::Initialize.
            hr = ((delegate* unmanaged<void*, nint*, int>)Com.Vtable(persist)[5])(persist, &pidl);

            if (hr < 0 || pidl == 0)
                return null;

            return PathFromPidl(pidl);
        }
        finally
        {
            if (pidl != 0)
                Marshal.FreeCoTaskMem(pidl);

            Com.Release(persist);
            Com.Release(folderView);
            Com.Release(view);
            Com.Release(browser);
            Com.Release(provider);
        }
    }

    private static string? PathFromPidl(nint pidl)
    {
        //MAX_PATH. SHGetPathFromIDListW has no long-path form, so a folder deeper than this resolves
        //to nothing and the entry falls back to its plain label -- which is the honest outcome, and
        //the click still works because Invoke gets its path from Explorer the same way.
        const int MaxPath = 260;

        char* buffer = stackalloc char[MaxPath];

        return SHGetPathFromIDListW(pidl, buffer) ? new string(buffer) : null;
    }

    [LibraryImport("shell32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHGetPathFromIDListW(nint pidl, char* path);
}
