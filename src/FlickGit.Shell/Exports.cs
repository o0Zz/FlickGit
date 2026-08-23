using System.Runtime.InteropServices;
using FlickGit.Shared;

namespace FlickGit.Shell;

/// <summary>
/// The two functions Windows looks for in an in-process COM server, and the class factory behind
/// them.
///
/// <b>Native AOT, so there is no CLR to host.</b> This is a real native DLL with real exports —
/// which is the whole reason the DLL is allowed to exist at all. The alternative, <c>comhost</c>,
/// loads the .NET runtime into <c>explorer.exe</c>: hundreds of milliseconds on the first
/// right-click, a runtime that can never be unloaded, and a second copy of it if any other
/// extension has already done the same. CLAUDE.md's process-split argument for <c>flick.exe</c>
/// applies here with more force, because this one is not even our process.
/// </summary>
internal static unsafe class Exports
{
    /// <summary>
    /// The class factory. One per requested CLSID, and it holds nothing but that CLSID.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Factory
    {
        public void** Vtable;
        public int RefCount;
    }

    private static void** _factoryVtable;
    private static readonly object VtableGate = new();

    /// <summary>
    /// Windows asking for one of our classes. The only entry point that matters.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    public static int DllGetClassObject(Guid* clsid, Guid* iid, void** result)
    {
        if (result is null)
            return Com.E_POINTER;

        *result = null;

        if (clsid is null || iid is null)
            return Com.E_INVALIDARG;

        try
        {
            //The one class this build serves. An unrecognised CLSID is a stale registry entry from a
            //version that registered per-verb IExplorerCommand handlers, and
            //CLASS_E_CLASSNOTAVAILABLE is exactly what it means -- Explorer then draws no entry,
            //rather than an entry that does nothing.
            if (Guid.Parse(ShellCommandIds.MenuHandlerClsid) != *clsid)
                return Com.CLASS_E_CLASSNOTAVAILABLE;

            //A class object is asked for as IClassFactory or IUnknown, and nothing else.
            if (*iid != Com.IClassFactory && *iid != Com.IUnknown)
                return Com.E_NOINTERFACE;

            EnsureFactoryVtable();

            var factory = (Factory*)NativeMemory.AllocZeroed((nuint)sizeof(Factory));

            if (factory is null)
                return Com.E_OUTOFMEMORY;

            factory->Vtable = _factoryVtable;
            factory->RefCount = 1;

            *result = factory;
            return Com.S_OK;
        }
        catch
        {
            return Com.E_FAIL;
        }
    }

    /// <summary>
    /// <b>Always <c>S_FALSE</c>: this DLL never unloads.</b>
    ///
    /// Not a shortcut. The .NET runtime — Native AOT included — does not support being unloaded and
    /// reinitialised inside a live process, so agreeing to unload is agreeing to a crash the next
    /// time Explorer builds a menu.
    ///
    /// The cost is that <c>FlickGit.Shell.dll</c> stays locked while <c>explorer.exe</c> runs, so
    /// replacing it means restarting Explorer. The uninstall path removes the registry entries
    /// without needing the file, so an uninstall still takes effect immediately; it is only
    /// overwriting the binary that has to wait.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    public static int DllCanUnloadNow() => Com.S_FALSE;

    private static void EnsureFactoryVtable()
    {
        lock (VtableGate)
        {
            if (_factoryVtable is not null)
                return;

            void** table = Com.AllocateVtable(5);

            table[0] = (delegate* unmanaged<void*, Guid*, void**, int>)&FactoryQueryInterface;
            table[1] = (delegate* unmanaged<void*, uint>)&FactoryAddRef;
            table[2] = (delegate* unmanaged<void*, uint>)&FactoryRelease;
            table[3] = (delegate* unmanaged<void*, void*, Guid*, void**, int>)&CreateInstance;
            table[4] = (delegate* unmanaged<void*, int, int>)&LockServer;

            _factoryVtable = table;
        }
    }

    [UnmanagedCallersOnly]
    private static int FactoryQueryInterface(void* self, Guid* iid, void** result)
    {
        if (result is null)
            return Com.E_POINTER;

        *result = null;

        if (iid is null)
            return Com.E_INVALIDARG;

        try
        {
            if (*iid != Com.IClassFactory && *iid != Com.IUnknown)
                return Com.E_NOINTERFACE;

            Interlocked.Increment(ref ((Factory*)self)->RefCount);
            *result = self;
            return Com.S_OK;
        }
        catch
        {
            return Com.E_FAIL;
        }
    }

    [UnmanagedCallersOnly]
    private static uint FactoryAddRef(void* self)
    {
        try
        {
            return (uint)Interlocked.Increment(ref ((Factory*)self)->RefCount);
        }
        catch
        {
            return 1;
        }
    }

    [UnmanagedCallersOnly]
    private static uint FactoryRelease(void* self)
    {
        try
        {
            var factory = (Factory*)self;
            int remaining = Interlocked.Decrement(ref factory->RefCount);

            if (remaining > 0)
                return (uint)remaining;

            NativeMemory.Free(factory);
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    [UnmanagedCallersOnly]
    private static int CreateInstance(void* self, void* outer, Guid* iid, void** result)
    {
        if (result is null)
            return Com.E_POINTER;

        *result = null;

        if (iid is null)
            return Com.E_INVALIDARG;

        //Aggregation is a COM feature nothing has asked for since 1998, and refusing it is the
        //documented answer rather than a limitation.
        if (outer is not null)
            return Com.CLASS_E_NOAGGREGATION;

        try
        {
            _ = self;
            return ContextMenuHandler.Create(*iid, result);
        }
        catch
        {
            return Com.E_FAIL;
        }
    }

    /// <summary>
    /// <c>IClassFactory::LockServer</c>. Nothing to do: the DLL already refuses to unload.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int LockServer(void* self, int lockServer)
    {
        _ = self;
        _ = lockServer;

        return Com.S_OK;
    }
}
