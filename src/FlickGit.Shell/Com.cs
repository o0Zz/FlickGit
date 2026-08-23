using System.Runtime.InteropServices;

namespace FlickGit.Shell;

/// <summary>
/// The COM vocabulary this DLL needs, and nothing else.
///
/// <b>Hand-rolled rather than <c>[GeneratedComInterface]</c>.</b> The source generator would be
/// less code, but it decides the reference counting, the allocator behind every out-string and the
/// lifetime of the wrapper. This object is created by <c>explorer.exe</c>, on threads we do not own,
/// and a leaked reference or a string freed with the wrong allocator is a bug in the user's desktop
/// rather than in our process. Every slot below is explicit for that reason.
///
/// The interfaces are consumed as raw vtables: read the pointer at offset 0, index the slot, call
/// through it. That is all a COM call is, and it needs no marshalling because everything crossing
/// the boundary here is a pointer, a <c>uint</c> or an <c>HRESULT</c>.
/// </summary>
internal static unsafe class Com
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;

    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_POINTER = unchecked((int)0x80004003);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);

    public const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    public const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);

    public static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IClassFactory = new("00000001-0000-0000-C000-000000000046");

    /// <summary>The interface this whole DLL exists to implement.</summary>
    public static readonly Guid IExplorerCommand = new("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9");

    /// <summary>
    /// How the background of a folder is resolved. Explorer hands the handler a site instead of an
    /// item array when the user right-clicks empty space, and the folder has to be dug out of it.
    /// </summary>
    public static readonly Guid IObjectWithSite = new("FC4801A3-2BA9-11CF-A229-00AA003D7352");

    public static readonly Guid IServiceProvider = new("6d5140c1-7436-11ce-8034-00aa006009fa");

    /// <summary>Also the service id it is asked for by. The shell uses the IID as the SID here.</summary>
    public static readonly Guid IShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    public static readonly Guid IFolderView = new("cde725b0-ccc9-4519-917e-325d72fab4ce");
    public static readonly Guid IPersistFolder2 = new("1AC3D9F0-175C-11d1-95BE-00609797EA4F");

    /// <summary>`SIGDN_FILESYSPATH`. The only display name worth having: a real path.</summary>
    public const uint SigdnFileSysPath = 0x80058000;

    // ---- EXPCMDSTATE ---------------------------------------------------------------

    public const uint EcsEnabled = 0x00;
    public const uint EcsHidden = 0x02;

    /// <summary>`ECF_DEFAULT`. No sub-commands, no separator, nothing special.</summary>
    public const uint EcfDefault = 0x00;

    /// <summary>The vtable of a COM pointer: the machine word it starts with.</summary>
    public static void** Vtable(void* instance) => *(void***)instance;

    /// <summary>
    /// <c>IUnknown::QueryInterface</c>, slot 0.
    /// </summary>
    public static int QueryInterface(void* instance, Guid iid, out void* result)
    {
        void* answer = null;

        //A local, because the interface identifier has to be addressable and an argument is not
        //guaranteed to be.
        Guid requested = iid;

        int hr = ((delegate* unmanaged<void*, Guid*, void**, int>)Vtable(instance)[0])(
            instance, &requested, &answer);

        result = answer;
        return hr;
    }

    /// <summary><c>IUnknown::Release</c>, slot 2. Null-safe, because every caller here would be.</summary>
    public static void Release(void* instance)
    {
        if (instance is not null)
            ((delegate* unmanaged<void*, uint>)Vtable(instance)[2])(instance);
    }

    /// <summary>
    /// A string across the COM boundary, in the allocator the caller will free it with.
    ///
    /// <c>IExplorerCommand</c>'s out-strings are documented as the caller's to release with
    /// <c>CoTaskMemFree</c>, so they must come from <c>CoTaskMemAlloc</c> —
    /// <see cref="Marshal.StringToCoTaskMemUni"/> and nothing else. Any other allocator is a heap
    /// corruption that surfaces minutes later somewhere in Explorer.
    /// </summary>
    public static int ReturnString(string? value, char** destination)
    {
        if (destination is null)
            return E_POINTER;

        *destination = null;

        if (value is null)
            return E_NOTIMPL;

        nint allocated = Marshal.StringToCoTaskMemUni(value);

        if (allocated == 0)
            return E_OUTOFMEMORY;

        *destination = (char*)allocated;
        return S_OK;
    }

    /// <summary>
    /// Builds a vtable in unmanaged memory.
    ///
    /// Never freed, and that is deliberate: one allocation per interface for the life of the
    /// process, referenced by every instance. <see cref="Exports.DllCanUnloadNow"/> refuses to let
    /// the DLL unload anyway, so there is no moment at which freeing it would be correct.
    /// </summary>
    public static void** AllocateVtable(int slots)
    {
        var table = (void**)NativeMemory.AllocZeroed((nuint)slots, (nuint)sizeof(void*));
        return table;
    }
}
