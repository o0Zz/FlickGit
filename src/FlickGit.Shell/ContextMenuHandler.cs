using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FlickGit.Shell;

/// <summary>
/// The FlickGit block in the Explorer context menu: <c>IShellExtInit</c> and <c>IContextMenu</c> on
/// one COM identity.
///
/// <b>This exists for one reason: position.</b> A static registry verb has exactly three reachable
/// placements — <c>Top</c>, the default, and <c>Bottom</c> — and Explorer draws the whole
/// static-verb block above the shell-extension block, which it draws above <c>New</c>. So the slot
/// every Git client occupies, immediately above <c>New</c>, is not addressable by a verb at any
/// setting: the default landed the entries up among <c>Open with Code</c> and <c>Git GUI Here</c>,
/// and <c>Bottom</c> pushed them past <c>New</c> down beside <c>Properties</c>. Being in that slot
/// means being a <c>ContextMenuHandler</c>, which means this interface.
///
/// It also supersedes the <c>IExplorerCommand</c> handlers that came before it, which is a
/// simplification rather than a cost: those were one CLSID per verb, each asked separately about its
/// own title and state. This is one object asked once, and it does the same two jobs directly —
/// <see cref="Compose"/> puts the branch in the Commit label, and omits every repository-requiring
/// item outside a repository.
///
/// <b>Every method here runs inside <c>explorer.exe</c></b>, so the rules from that are unchanged:
/// nothing may throw across the boundary, nothing may block, and uncertainty shows the items rather
/// than hiding them.
/// </summary>
internal static unsafe partial class ContextMenuHandler
{
    /// <summary>An interface pointer handed to Explorer: a vtable, and the way home.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Slot
    {
        public void** Vtable;
        public Instance* Owner;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Instance
    {
        /// <summary>Offset zero, so this is also the object's <c>IUnknown</c> identity.</summary>
        public Slot ContextMenu;

        public Slot ShellExtInit;

        public int RefCount;

        /// <summary>
        /// The folder the menu is for, as a <c>CoTaskMemAlloc</c>'d string, or zero.
        ///
        /// Held rather than re-resolved: <c>Initialize</c> is the only call that is given it, and
        /// <c>QueryContextMenu</c> and <c>InvokeCommand</c> both need it afterwards.
        /// </summary>
        public char* Folder;

        /// <summary>How many items were added, so <c>InvokeCommand</c> can bound-check the offset.</summary>
        public int ItemCount;

        /// <summary>
        /// Which entry each command offset maps to, as indices into <see cref="MenuItems.All"/>.
        /// Allocated by <c>QueryContextMenu</c>, freed with the object.
        /// </summary>
        public int* ItemMap;
    }

    private static void** _contextMenuVtable;
    private static void** _shellExtInitVtable;
    private static readonly object VtableGate = new();

    public static int Create(Guid iid, void** result)
    {
        if (result is null)
            return Com.E_POINTER;

        *result = null;

        EnsureVtables();

        var instance = (Instance*)NativeMemory.AllocZeroed((nuint)sizeof(Instance));

        if (instance is null)
            return Com.E_OUTOFMEMORY;

        instance->ContextMenu.Vtable = _contextMenuVtable;
        instance->ContextMenu.Owner = instance;
        instance->ShellExtInit.Vtable = _shellExtInitVtable;
        instance->ShellExtInit.Owner = instance;
        instance->RefCount = 1;

        int hr = QueryInterfaceCore(instance, iid, result);

        ReleaseCore(instance);
        return hr;
    }

    private static void EnsureVtables()
    {
        lock (VtableGate)
        {
            if (_contextMenuVtable is not null)
                return;

            void** menu = Com.AllocateVtable(6);

            menu[0] = (delegate* unmanaged<void*, Guid*, void**, int>)&QueryInterface;
            menu[1] = (delegate* unmanaged<void*, uint>)&AddRef;
            menu[2] = (delegate* unmanaged<void*, uint>)&Release;
            menu[3] = (delegate* unmanaged<void*, nint, uint, uint, uint, uint, int>)&QueryContextMenu;
            menu[4] = (delegate* unmanaged<void*, Com.InvokeCommandInfo*, int>)&InvokeCommand;
            menu[5] = (delegate* unmanaged<void*, nuint, uint, uint*, byte*, uint, int>)&GetCommandString;

            void** init = Com.AllocateVtable(4);

            init[0] = (delegate* unmanaged<void*, Guid*, void**, int>)&QueryInterface;
            init[1] = (delegate* unmanaged<void*, uint>)&AddRef;
            init[2] = (delegate* unmanaged<void*, uint>)&Release;
            init[3] = (delegate* unmanaged<void*, nint, void*, nint, int>)&Initialize;

            _shellExtInitVtable = init;
            _contextMenuVtable = menu;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Instance* Self(void* slot) => ((Slot*)slot)->Owner;

    // ---- IUnknown -------------------------------------------------------------------

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
            return QueryInterfaceCore(Self(self), *iid, result);
        }
        catch
        {
            return Com.E_FAIL;
        }
    }

    private static int QueryInterfaceCore(Instance* instance, Guid iid, void** result)
    {
        if (iid == Com.IUnknown || iid == Com.IContextMenu)
            *result = &instance->ContextMenu;
        else if (iid == Com.IShellExtInit)
            *result = &instance->ShellExtInit;
        else
            return Com.E_NOINTERFACE;

        Interlocked.Increment(ref instance->RefCount);
        return Com.S_OK;
    }

    [UnmanagedCallersOnly]
    private static uint AddRef(void* self)
    {
        try
        {
            return (uint)Interlocked.Increment(ref Self(self)->RefCount);
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
            return ReleaseCore(Self(self));
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

        if (instance->Folder is not null)
            Marshal.FreeCoTaskMem((nint)instance->Folder);

        if (instance->ItemMap is not null)
            NativeMemory.Free(instance->ItemMap);

        NativeMemory.Free(instance);
        return 0;
    }

    // ---- IShellExtInit --------------------------------------------------------------

    /// <summary>
    /// Explorer saying what was clicked, and the only chance to find out.
    ///
    /// Two inputs, and exactly one of them is populated:
    ///
    /// <list type="bullet">
    /// <item><description><paramref name="folder"/> is a PIDL, for a click on a folder's
    /// <b>background</b> — the case that needed the whole <c>IObjectWithSite</c> service-provider
    /// chain under <c>IExplorerCommand</c>, and that arrives here for free. That is the second reason
    /// this interface is the right one.</description></item>
    /// <item><description><paramref name="dataObject"/> carries <c>CF_HDROP</c>, for a click on a
    /// selected folder.</description></item>
    /// </list>
    /// </summary>
    [UnmanagedCallersOnly]
    private static int Initialize(void* self, nint folder, void* dataObject, nint progId)
    {
        _ = progId;

        try
        {
            Instance* instance = Self(self);

            //A selection wins over the containing folder: right-clicking a subdirectory means that
            //subdirectory, not the folder it happens to be sitting in.
            string? path = Selection.FromDataObject(dataObject) ?? Selection.FromPidl(folder);

            if (instance->Folder is not null)
            {
                Marshal.FreeCoTaskMem((nint)instance->Folder);
                instance->Folder = null;
            }

            if (path is { Length: > 0 })
                instance->Folder = (char*)Marshal.StringToCoTaskMemUni(path);

            //S_OK even with nothing resolved. QueryContextMenu then adds no items, which is the
            //correct outcome for This PC, a library, or a search result -- and failing here makes
            //Explorer log an error about a handler that merely had nothing to offer.
            return Com.S_OK;
        }
        catch
        {
            return Com.E_FAIL;
        }
    }

    // ---- IContextMenu ---------------------------------------------------------------

    /// <summary>
    /// Builds the block: a separator, the root items, the <c>FlickGit</c> submenu, a separator.
    ///
    /// The two separators are the whole of the "dedicated place" — TortoiseGit's
    /// <c>QueryContextMenu</c> does exactly this and nothing else about placement.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int QueryContextMenu(void* self, nint menu, uint index, uint idFirst, uint idLast, uint flags)
    {
        try
        {
            //Explorer wants the default action only, for a double-click. Adding items here would put
            //them where nobody asked.
            if ((flags & Com.CmfDefaultOnly) != 0)
                return Com.ItemsAdded(0);

            Instance* instance = Self(self);
            string? folder = instance->Folder is null ? null : Marshal.PtrToStringUni((nint)instance->Folder);

            MenuItem[] items = MenuItems.All();

            if (items.Length == 0 || folder is null)
                return Com.ItemsAdded(0);

            RepositoryAnswer answer = RepositoryLookup.For(folder);

            //Uncertainty shows the items: a folder that could not be classified keeps everything, so
            //the menu does not come and go for reasons the user cannot see.
            bool insideRepository = answer.Verdict != RepositoryVerdict.NotARepository;

            MenuItem[] shown = [.. items.Where(i => insideRepository || !i.NeedsRepository)];

            if (shown.Length == 0)
                return Com.ItemsAdded(0);

            return Compose(instance, menu, index, idFirst, idLast, shown, items, answer.Branch);
        }
        catch
        {
            //No block rather than a broken one, and above all not an exception into Explorer.
            return Com.ItemsAdded(0);
        }
    }

    /// <summary>
    /// Inserts the items and records which command id maps to which entry.
    /// </summary>
    private static int Compose(
        Instance* instance,
        nint menu,
        uint index,
        uint idFirst,
        uint idLast,
        MenuItem[] shown,
        MenuItem[] all,
        string? branch)
    {
        //One id per item, and Explorer's range is finite. Refusing to start is better than running
        //out half way through and leaving a submenu with no parent.
        if (idFirst + (uint)shown.Length + 1 > idLast)
            return Com.ItemsAdded(0);

        if (instance->ItemMap is not null)
            NativeMemory.Free(instance->ItemMap);

        instance->ItemMap = (int*)NativeMemory.AllocZeroed((nuint)shown.Length, sizeof(int));
        instance->ItemCount = 0;

        uint position = index;
        uint offset = 0;

        InsertSeparator(menu, position++);

        nint submenu = 0;
        uint submenuPosition = 0;

        foreach (MenuItem item in shown)
        {
            string label = item.ShowBranch && branch is { Length: > 0 }
                ? Decorate(item.Label, branch)
                : item.Label;

            if (item.InSubmenu)
            {
                if (submenu == 0)
                {
                    submenu = CreatePopupMenu();

                    if (submenu == 0)
                        continue;

                    //Remembered rather than inserted now: the parent has to be added after its
                    //children so the shell sees a populated popup.
                    submenuPosition = position++;
                }

                InsertItem(submenu, uint.MaxValue, idFirst + offset, label, item.Icon);
            }
            else
            {
                InsertItem(menu, position++, idFirst + offset, label, item.Icon);
            }

            instance->ItemMap[offset] = Array.IndexOf(all, item);
            offset++;
        }

        if (submenu != 0)
            InsertSubmenu(menu, submenuPosition, submenu, MenuConfig.SubmenuLabel());

        InsertSeparator(menu, position);

        instance->ItemCount = (int)offset;

        //The count of command *ids* used, which is the item count -- separators and the popup parent
        //carry no id.
        return Com.ItemsAdded((int)offset);
    }

    /// <summary>
    /// Puts the branch in the label, keeping a trailing ellipsis at the end where it belongs.
    ///
    /// <c>Commit / Push (main)…</c>, not <c>Commit / Push… (main)</c>: the ellipsis means "this opens
    /// something" and belongs to the whole label.
    /// </summary>
    internal static string Decorate(string label, string branch)
    {
        foreach (string ellipsis in new[] { "…", "..." })
        {
            if (label.EndsWith(ellipsis, StringComparison.Ordinal))
                return $"{label[..^ellipsis.Length].TrimEnd()} ({branch}){ellipsis}";
        }

        return $"{label} ({branch})";
    }

    private static void InsertSeparator(nint menu, uint position) =>
        InsertMenuW(menu, position, Com.MfByPosition | Com.MfSeparator, 0, null);

    private static void InsertItem(nint menu, uint position, uint id, string label, string? icon)
    {
        uint flags = Com.MfString | (position == uint.MaxValue ? 0 : Com.MfByPosition);
        uint at = position == uint.MaxValue ? uint.MaxValue : position;

        fixed (char* text = label)
        {
            if (!InsertMenuW(menu, at, flags, id, text))
                return;
        }

        if (MenuIcons.Bitmap(icon) is var bitmap && bitmap != 0)
        {
            var info = new Com.MenuItemInfo
            {
                Size = (uint)sizeof(Com.MenuItemInfo),
                Mask = Com.MiimBitmap,
                BitmapItem = bitmap,
            };

            //By id, because the position of an appended item is not known here -- and an icon that
            //fails to attach is a cosmetic loss, so the result is not checked.
            SetMenuItemInfoW(menu, id, false, &info);
        }
    }

    private static void InsertSubmenu(nint menu, uint position, nint submenu, string label)
    {
        fixed (char* text = label)
        {
            InsertMenuW(menu, position, Com.MfByPosition | Com.MfPopup, (uint)submenu, text);
        }
    }

    /// <summary>
    /// The click. Runs <c>flick.exe</c> with the verb the offset maps to.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int InvokeCommand(void* self, Com.InvokeCommandInfo* info)
    {
        if (info is null)
            return Com.E_INVALIDARG;

        try
        {
            Instance* instance = Self(self);

            //A high word of zero means the field is not a pointer but the zero-based offset of the
            //item that was clicked. A verb *name* is the other form, and nothing here registers one.
            if (((nuint)info->Verb >> 16) != 0)
                return Com.E_INVALIDARG;

            int offset = (int)((nuint)info->Verb & 0xFFFF);

            if (offset < 0 || offset >= instance->ItemCount || instance->ItemMap is null)
                return Com.E_INVALIDARG;

            MenuItem[] items = MenuItems.All();
            int which = instance->ItemMap[offset];

            if (which < 0 || which >= items.Length)
                return Com.E_INVALIDARG;

            string? folder = instance->Folder is null ? null : Marshal.PtrToStringUni((nint)instance->Folder);

            return Launcher.Start(MenuConfig.ExePath(), items[which].Verb, folder) ? Com.S_OK : Com.E_FAIL;
        }
        catch
        {
            return Com.E_FAIL;
        }
    }

    /// <summary>
    /// The canonical name or help text for an item. Neither is used: nothing refers to these commands
    /// by name, and the label is already the whole description.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int GetCommandString(void* self, nuint id, uint type, uint* reserved, byte* name, uint max)
    {
        _ = self;
        _ = id;
        _ = type;
        _ = reserved;
        _ = name;
        _ = max;

        return Com.E_NOTIMPL;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InsertMenuW(nint menu, uint position, uint flags, uint idNewItem, char* item);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetMenuItemInfoW(
        nint menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, Com.MenuItemInfo* info);
}
