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

    // ---- IContextMenu, which is how the block reaches TortoiseGit's position ---------
    //
    // A static verb can only be Top, default, or Bottom, and Explorer draws the whole static-verb
    // block above the shell-extension block. So no Position value reaches the slot between them --
    // the one immediately above `New`, where every other Git client sits. Being there means being a
    // ContextMenuHandler, which means implementing these two interfaces.

    public static readonly Guid IContextMenu = new("000214e4-0000-0000-c000-000000000046");

    /// <summary>How Explorer tells the handler which folder, or which selection, was clicked.</summary>
    public static readonly Guid IShellExtInit = new("000214e8-0000-0000-c000-000000000046");

    /// <summary>
    /// `CMF_DEFAULTONLY`. Explorer is asking only for the default action — a double-click, not a
    /// right-click — and a handler that adds items here puts them somewhere nobody asked for.
    /// </summary>
    public const uint CmfDefaultOnly = 0x00000001;

    public const uint MfByPosition = 0x00000400;
    public const uint MfSeparator = 0x00000800;
    public const uint MfString = 0x00000000;
    public const uint MfPopup = 0x00000010;

    /// <summary>`MIIM_BITMAP`, for the one field of MENUITEMINFO this needs.</summary>
    public const uint MiimBitmap = 0x00000080;

    public const int CfHdrop = 15;
    public const uint TymedHGlobal = 1;
    public const uint DvAspectContent = 1;

    /// <summary>
    /// `FORMATETC`. The padding is real: `cfFormat` is a <c>WORD</c> followed by a pointer, so the
    /// pointer is 8-aligned and the struct is 32 bytes. Sequential layout reproduces that.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FormatEtc
    {
        public ushort Format;
        public nint TargetDevice;
        public uint Aspect;
        public int Index;
        public uint Tymed;
    }

    /// <summary>`STGMEDIUM`. Released with <c>ReleaseStgMedium</c>, never by hand.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct StgMedium
    {
        public uint Tymed;
        public nint Data;
        public nint UnkForRelease;
    }

    /// <summary>
    /// `CMINVOKECOMMANDINFO`. Only <c>Verb</c> is read: when its high word is zero it is not a
    /// pointer at all but the zero-based index of the item that was clicked, which is the whole of
    /// how <c>InvokeCommand</c> identifies what to run.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct InvokeCommandInfo
    {
        public uint Size;
        public uint Mask;
        public nint Window;
        public nint Verb;
        public nint Parameters;
        public nint Directory;
        public int Show;
        public uint HotKey;
        public nint Icon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MenuItemInfo
    {
        public uint Size;
        public uint Mask;
        public uint Type;
        public uint State;
        public uint Id;
        public nint SubMenu;
        public nint Checked;
        public nint Unchecked;
        public nint ItemData;
        public nint TypeData;
        public uint Cch;
        public nint BitmapItem;
    }

    /// <summary>
    /// The HRESULT `QueryContextMenu` returns: a success code whose low word is the number of
    /// command ids used. `MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_NULL, count)` is just the count,
    /// which is worth naming rather than leaving as a bare integer that looks like S_OK plus n.
    /// </summary>
    public static int ItemsAdded(int count) => count;

    /// <summary>The vtable of a COM pointer: the machine word it starts with.</summary>
    public static void** Vtable(void* instance) => *(void***)instance;

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
