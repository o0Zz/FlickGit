using System.Runtime.InteropServices;

namespace FlickGit.App.Trigger;

/// <summary>
/// The parts of Explorer's automation surface FlickGit asks about, and nothing else.
///
/// <b>Declared as <c>IDispatch</c>, not as vtables.</b> Every object on this path is a dual
/// interface, so the CLR dispatches by <i>name</i> through <c>IDispatch::Invoke</c> and only the
/// members actually called need declaring. The alternative — <c>ComInterfaceType.InterfaceIsIUnknown</c>
/// down <c>IServiceProvider</c> → <c>IShellBrowser</c> → <c>IShellView</c> → <c>IFolderView2</c> —
/// requires every unused vtable slot to be declared in exactly the right order (<c>IFolderView2</c>
/// has about forty), and one omitted slot silently calls the wrong function. That is a bad trade for
/// the only COM surface in the product.
///
/// The other candidate was late binding through <c>dynamic</c>, which is less code again but loads
/// <c>Microsoft.CSharp</c> and initialises the DLR at the first call site — tens of milliseconds,
/// inside an 80 ms budget.
/// </summary>
internal static class ShellWindows
{
    /// <summary><c>CLSID_ShellWindows</c>. The running Explorer windows, as a collection.</summary>
    private static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    /// <summary>
    /// Creates the shell-windows collection, or null when Explorer is not running.
    /// </summary>
    public static IShellWindows? Create()
    {
        Type? type = Type.GetTypeFromCLSID(ShellWindowsClsid);

        return type is null ? null : Activator.CreateInstance(type) as IShellWindows;
    }

    /// <summary>Releases an RCW without waiting for the finaliser thread.</summary>
    public static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
            Marshal.ReleaseComObject(comObject);
    }
}

[ComImport]
[Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IShellWindows
{
    /// <summary>
    /// One entry per Explorer <i>tab</i> on Windows 11, not one per window — which is the whole
    /// reason the resolver returns a list.
    /// </summary>
    int Count { get; }

    [return: MarshalAs(UnmanagedType.IDispatch)]
    object? Item(object index);
}

[ComImport]
[Guid("D30C1661-CDAF-11D0-8A3E-00C04FC9E26E")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IWebBrowser2
{
    /// <summary>
    /// The frame window. Shared by every tab of one Explorer window.
    ///
    /// <c>long</c>, not <c>nint</c>: this arrives through <c>IDispatch</c> as a VARIANT, and
    /// <c>nint</c> is not a VARIANT type, so the marshaller has nothing to convert it into.
    /// </summary>
    long HWND { get; }

    object? Document { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
}

/// <summary><c>IShellFolderViewDual</c> — what an Explorer tab's <c>Document</c> actually is.</summary>
[ComImport]
[Guid("E7A1AF80-4D96-11CF-960C-0080C7F4EE85")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IShellFolderViewDual
{
    object? Folder { [return: MarshalAs(UnmanagedType.IDispatch)] get; }

    [return: MarshalAs(UnmanagedType.IDispatch)]
    object? SelectedItems();
}

/// <summary><c>Folder</c> — the shell folder the tab is showing.</summary>
[ComImport]
[Guid("BBCBDE60-C3FF-11CE-8350-444553540000")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IShellFolderDispatch
{
    /// <summary>The folder as an item, which is how its path is reached.</summary>
    object? Self { [return: MarshalAs(UnmanagedType.IDispatch)] get; }
}

[ComImport]
[Guid("FAC32C80-CBE4-11CE-8350-444553540000")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IFolderItem
{
    string Name { get; }

    /// <summary>Empty for anything that is not a file-system path — a control panel, a library.</summary>
    string Path { get; }

    bool IsFolder { get; }
}

[ComImport]
[Guid("744129E0-CBE5-11CE-8350-444553540000")]
[InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
internal interface IFolderItems
{
    int Count { get; }

    [return: MarshalAs(UnmanagedType.IDispatch)]
    object? Item(object index);
}
