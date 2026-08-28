using System.Runtime.InteropServices;
using Microsoft.Win32;
using FlickGit.Shared;

namespace FlickGit.Shell;

/// <summary>
/// The badge Explorer draws on a repository folder: <c>IShellIconOverlayIdentifier</c>.
///
/// <b>It says one thing — this folder is a Git repository — and it says it about the repository
/// root only.</b> Not "clean", not "modified", not "ahead of the remote", and not about the folders
/// inside it. That is the whole feature, and every rule below follows from it: with no status to
/// compute there is nothing to run <c>git.exe</c> for, nothing to ask the resident service, nothing
/// to cache, and nothing to invalidate.
///
/// <b>Why the narrow version is affordable where TortoiseGit's is not.</b> An overlay identifier is
/// created once, when Explorer starts, and then <see cref="IsMemberOf"/> is called <i>synchronously
/// for every item Explorer draws</i>, forever, on the thread painting the view. A handler that runs
/// a status per item makes scrolling stutter; this one does a bit test on the attributes it was
/// handed, and at most one <c>GetFileAttributesW</c>.
///
/// <b>The fifteen-slot limit is real and is not this file's problem to solve.</b> Windows loads the
/// first <see cref="ShellCommandIds.OverlaySlotLimit"/> registered handlers sorted by key name.
/// <c>flick diag doctor</c> reports where FlickGit landed; nothing here can tell whether it was
/// loaded, because a handler that was not loaded is never asked.
///
/// <b>Every method runs inside <c>explorer.exe</c></b>: nothing may throw across the boundary,
/// nothing may block, and uncertainty draws nothing rather than guessing.
/// </summary>
internal static unsafe class OverlayHandler
{
    /// <summary>
    /// The object, and its own <c>IUnknown</c> identity.
    ///
    /// <b>No <c>Slot</c> indirection, unlike <see cref="ContextMenuHandler"/>.</b> That one carries
    /// two interfaces and so needs a way back from either vtable pointer to the object; this carries
    /// one, so the instance pointer <i>is</i> the interface pointer and the vtable sits at offset
    /// zero where COM expects it.
    ///
    /// There is no per-instance state beyond the reference count. <see cref="IsMemberOf"/> is handed
    /// the path it is asked about, so there is nothing to remember between calls -- which is what
    /// makes an object living for the whole Explorer session harmless.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Instance
    {
        public void** Vtable;
        public int RefCount;
    }

    private static void** _vtable;
    private static readonly object VtableGate = new();

    public static int Create(Guid iid, void** result)
    {
        if (result is null)
            return Com.E_POINTER;

        *result = null;

        EnsureVtable();

        var instance = (Instance*)NativeMemory.AllocZeroed((nuint)sizeof(Instance));

        if (instance is null)
            return Com.E_OUTOFMEMORY;

        instance->Vtable = _vtable;
        instance->RefCount = 1;

        int hr = QueryInterfaceCore(instance, iid, result);

        ReleaseCore(instance);
        return hr;
    }

    private static void EnsureVtable()
    {
        lock (VtableGate)
        {
            if (_vtable is not null)
                return;

            //Six slots: IUnknown's three, then IShellIconOverlayIdentifier's three.
            //
            //The order below is the DECLARATION order in shlobj.h, which is the only thing that
            //decides a slot number -- not the order the methods are written in this file, and not
            //the order they are called in. It is IsMemberOf, GetOverlayInfo, GetPriority:
            //
            //    STDMETHOD(IsMemberOf)(PCWSTR pwszPath, DWORD dwAttrib);              slot 3
            //    STDMETHOD(GetOverlayInfo)(PWSTR, int cchMax, int*, DWORD*);          slot 4
            //    STDMETHOD(GetPriority)(int *pIPriority);                             slot 5
            //
            //Getting this wrong does not crash and does not log: every call still lands on a real
            //function with the wrong arguments and returns a plausible HRESULT. Explorer had been
            //calling IsMemberOf and reaching GetOverlayInfo, so dwAttrib arrived as cchMax -- always
            //0x10, 0x11 or 0x20 -- and a 70-character icon path never fit, so every item on the
            //machine was refused with E_FAIL and no badge was ever drawn.
            //
            //A signature mismatch is what makes it silent, so the delegate types are spelled out per
            //slot: they are the only local record of what Explorer actually pushes.
            void** table = Com.AllocateVtable(6);

            table[0] = (delegate* unmanaged<void*, Guid*, void**, int>)&QueryInterface;
            table[1] = (delegate* unmanaged<void*, uint>)&AddRef;
            table[2] = (delegate* unmanaged<void*, uint>)&Release;
            table[3] = (delegate* unmanaged<void*, char*, uint, int>)&IsMemberOf;
            table[4] = (delegate* unmanaged<void*, char*, int, int*, uint*, int>)&GetOverlayInfo;
            table[5] = (delegate* unmanaged<void*, int*, int>)&GetPriority;

            _vtable = table;
        }
    }

    [UnmanagedCallersOnly]
    private static int QueryInterface(void* self, Guid* iid, void** result)
    {
        if (result is null)
            return Com.E_POINTER;

        *result = null;

        if (iid is null)
            return Com.E_INVALIDARG;

        try
        {
            return QueryInterfaceCore((Instance*)self, *iid, result);
        }
        catch
        {
            return Com.E_FAIL;
        }
    }

    private static int QueryInterfaceCore(Instance* instance, Guid iid, void** result)
    {
        if (iid != Com.IUnknown && iid != Com.IShellIconOverlayIdentifier)
            return Com.E_NOINTERFACE;

        *result = instance;

        Interlocked.Increment(ref instance->RefCount);
        return Com.S_OK;
    }

    [UnmanagedCallersOnly]
    private static uint AddRef(void* self)
    {
        try
        {
            return (uint)Interlocked.Increment(ref ((Instance*)self)->RefCount);
        }
        catch
        {
            return 1;
        }
    }

    [UnmanagedCallersOnly]
    private static uint Release(void* self)
    {
        try
        {
            return ReleaseCore((Instance*)self);
        }
        catch
        {
            return 1;
        }
    }

    private static uint ReleaseCore(Instance* instance)
    {
        int remaining = Interlocked.Decrement(ref instance->RefCount);

        if (remaining > 0)
            return (uint)remaining;

        NativeMemory.Free(instance);
        return 0;
    }

    /// <summary>
    /// Which icon to draw. Asked once, when Explorer loads the handler.
    /// </summary>
    /// <remarks>
    /// The path is written into the caller's buffer, whose size is <paramref name="cchMax"/>
    /// <i>including</i> the terminator. A path that does not fit is refused rather than truncated: a
    /// truncated path names a different file, and the honest answer to "I cannot tell you where the
    /// icon is" is for Explorer to drop the handler.
    ///
    /// <b><c>E_FAIL</c> when the value is missing</b>, for the same reason. That happens when the
    /// <c>HKLM</c> half of the registration outlived the <c>HKCU</c> half -- an uninstall that could
    /// not elevate -- and dropping the handler is exactly right: it stops occupying a slot for the
    /// rest of the session.
    /// </remarks>
    [UnmanagedCallersOnly]
    private static int GetOverlayInfo(void* self, char* iconFile, int cchMax, int* index, uint* flags)
    {
        _ = self;

        if (iconFile is null || index is null || flags is null)
            return Com.E_POINTER;

        if (cchMax <= 0)
            return Com.E_INVALIDARG;

        try
        {
            if (IconPath() is not { Length: > 0 } path || path.Length + 1 > cchMax)
                return Com.E_FAIL;

            path.AsSpan().CopyTo(new Span<char>(iconFile, cchMax));
            iconFile[path.Length] = '\0';

            //A single-icon .ico, so the first and only image in it.
            *index = 0;
            *flags = Com.IsioiIconFile | Com.IsioiIconIndex;

            return Com.S_OK;
        }
        catch
        {
            return Com.E_FAIL;
        }
    }

    /// <summary>
    /// How badly we want the corner, 0 being the highest priority and 100 the lowest.
    /// </summary>
    /// <remarks>
    /// <b>50, deliberately, and not 0.</b> An item gets one overlay, so this number is what decides
    /// a repository inside OneDrive or any other sync root. Winning there would replace "this file
    /// is not uploaded yet" with "this is a repo" -- trading information the user may act on for a
    /// reminder of something they already know. The badge is a convenience and it yields.
    /// </remarks>
    [UnmanagedCallersOnly]
    private static int GetPriority(void* self, int* priority)
    {
        _ = self;

        if (priority is null)
            return Com.E_POINTER;

        *priority = 50;
        return Com.S_OK;
    }

    /// <summary>
    /// Does this item get the badge? <c>S_OK</c> for yes, <c>S_FALSE</c> for no.
    ///
    /// <b>The hottest callback in the product.</b> Explorer calls this once per drawn item, on the
    /// thread painting the view, for every view for the whole session. The tests below are in
    /// cost order and every one of them is an early exit.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int IsMemberOf(void* self, char* path, uint attributes)
    {
        _ = self;

        if (path is null)
            return Com.S_FALSE;

        try
        {
            //1. Not a directory. One bit test, and it is what every *file* in every folder on the
            //   machine costs -- which is the entire reason this handler is affordable.
            //
            //   Guarded on the attributes being reported at all: a provider that hands over zero has
            //   told us nothing, and the probe below is correct for any input, so falling through is
            //   better than silently skipping.
            if (attributes != 0 && (attributes & Com.FileAttributeDirectory) == 0)
                return Com.S_FALSE;

            //2. A cloud placeholder. Probing one can hydrate it, which is a network round trip on
            //   Explorer's drawing thread. A badge is not worth downloading somebody's archive.
            const uint recall = Com.FileAttributeOffline
                                | Com.FileAttributeRecallOnOpen
                                | Com.FileAttributeRecallOnDataAccess;

            if ((attributes & recall) != 0)
                return Com.S_FALSE;

            //3. A UNC path. Same rule and the same reason as RepositoryLookup: the redirector's
            //   timeout is orders of magnitude past any budget here, and this one would stall the
            //   whole view rather than one menu entry.
            if (path[0] == '\\' && path[1] == '\\')
                return Com.S_FALSE;

            //4. The only question left, and one syscall to answer it.
            int length = Length(path);

            return length > 0 && GitHead.HasGitEntry(new ReadOnlySpan<char>(path, length))
                ? Com.S_OK
                : Com.S_FALSE;
        }
        catch
        {
            return Com.S_FALSE;
        }
    }

    /// <summary>
    /// The length of a NUL-terminated string, bounded.
    ///
    /// Bounded because this is a pointer from another process's idea of a path: an unterminated
    /// buffer would otherwise be walked until it faulted, inside <c>explorer.exe</c>. 32,767 is the
    /// longest path Windows has, so a longer run of characters is not a path at all.
    /// </summary>
    private static int Length(char* path)
    {
        const int max = 32767;

        for (int i = 0; i < max; i++)
        {
            if (path[i] == '\0')
                return i;
        }

        return 0;
    }

    private static readonly object IconGate = new();
    private static bool _iconLoaded;
    private static string? _iconPath;

    /// <summary>
    /// The <c>.ico</c> the App registered, from the overlay's own CLSID key.
    ///
    /// The DLL resolves no paths of its own -- the same rule that puts every menu label and icon in
    /// the registry rather than in this assembly. Read once and kept: Explorer asks for it once, and
    /// a value that changed underneath would need an Explorer restart to matter anyway, because that
    /// is when overlay handlers are enumerated.
    /// </summary>
    private static string? IconPath()
    {
        lock (IconGate)
        {
            if (_iconLoaded)
                return _iconPath;

            _iconLoaded = true;

            try
            {
                using RegistryKey? clsid = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Classes\CLSID\{ShellCommandIds.OverlayHandlerClsid}", writable: false);

                _iconPath = clsid?.GetValue(ShellCommandIds.ValueOverlayIcon) as string;
            }
            catch
            {
                //A hive that cannot be read. No icon, and GetOverlayInfo answers E_FAIL.
                _iconPath = null;
            }

            return _iconPath;
        }
    }
}
