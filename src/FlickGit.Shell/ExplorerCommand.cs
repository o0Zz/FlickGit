using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace FlickGit.Shell;

/// <summary>
/// One context-menu entry, as Explorer sees it: <c>IExplorerCommand</c> and
/// <c>IObjectWithSite</c> on one COM identity.
///
/// <b>Every method here runs inside <c>explorer.exe</c>.</b> That single fact decides the whole
/// shape of this file:
///
/// <list type="bullet">
/// <item><description><b>Nothing may throw.</b> An exception crossing an
/// <c>[UnmanagedCallersOnly]</c> boundary is not an exception any more, it is a dead desktop. Every
/// entry point is a try/catch returning an HRESULT.</description></item>
/// <item><description><b>Nothing may block.</b> CLAUDE.md budgets <c>GetState</c> at 20 ms with a
/// 50 ms hard limit, called synchronously while the menu is built. No process, no pipe, no
/// network — see <see cref="GitHead"/> for what is left and why it is
/// enough.</description></item>
/// <item><description><b>Uncertainty shows the entry.</b> Hiding on a folder we merely failed to
/// classify would make the menu flicker in and out for reasons the user cannot
/// see.</description></item>
/// </list>
///
/// The instance lives in unmanaged memory with two interface slots, each carrying a vtable pointer
/// and a pointer back to the object. That is one shape rather than the usual base-plus-offset
/// arithmetic, so a method never has to know which slot it was reached through.
/// </summary>
internal static unsafe class ExplorerCommand
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
        public Slot Command;

        public Slot Site;

        public int RefCount;

        /// <summary>Which entry this is. The configuration is read from the matching registry key.</summary>
        public Guid Clsid;

        /// <summary>Explorer's site, held only for the folder-background case. Released on teardown.</summary>
        public void* SitePointer;
    }

    private static void** _commandVtable;
    private static void** _siteVtable;
    private static readonly object VtableGate = new();

    /// <summary>
    /// Creates one instance and returns the requested interface, with a reference already taken.
    /// </summary>
    public static int Create(Guid clsid, Guid iid, void** result)
    {
        if (result is null)
            return Com.E_POINTER;

        *result = null;

        EnsureVtables();

        var instance = (Instance*)NativeMemory.AllocZeroed((nuint)sizeof(Instance));

        if (instance is null)
            return Com.E_OUTOFMEMORY;

        instance->Command.Vtable = _commandVtable;
        instance->Command.Owner = instance;
        instance->Site.Vtable = _siteVtable;
        instance->Site.Owner = instance;
        instance->RefCount = 1;
        instance->Clsid = clsid;
        instance->SitePointer = null;

        int hr = QueryInterfaceCore(instance, iid, result);

        //The creation reference. QueryInterface took its own if it succeeded, so this one is always
        //given back -- and on failure it is what frees the object.
        ReleaseCore(instance);

        return hr;
    }

    private static void EnsureVtables()
    {
        lock (VtableGate)
        {
            if (_commandVtable is not null)
                return;

            void** command = Com.AllocateVtable(11);

            command[0] = (delegate* unmanaged<void*, Guid*, void**, int>)&QueryInterface;
            command[1] = (delegate* unmanaged<void*, uint>)&AddRef;
            command[2] = (delegate* unmanaged<void*, uint>)&Release;
            command[3] = (delegate* unmanaged<void*, void*, char**, int>)&GetTitle;
            command[4] = (delegate* unmanaged<void*, void*, char**, int>)&GetIcon;
            command[5] = (delegate* unmanaged<void*, void*, char**, int>)&GetToolTip;
            command[6] = (delegate* unmanaged<void*, Guid*, int>)&GetCanonicalName;
            command[7] = (delegate* unmanaged<void*, void*, int, uint*, int>)&GetState;
            command[8] = (delegate* unmanaged<void*, void*, void*, int>)&Invoke;
            command[9] = (delegate* unmanaged<void*, uint*, int>)&GetFlags;
            command[10] = (delegate* unmanaged<void*, void**, int>)&EnumSubCommands;

            void** site = Com.AllocateVtable(5);

            site[0] = (delegate* unmanaged<void*, Guid*, void**, int>)&QueryInterface;
            site[1] = (delegate* unmanaged<void*, uint>)&AddRef;
            site[2] = (delegate* unmanaged<void*, uint>)&Release;
            site[3] = (delegate* unmanaged<void*, void*, int>)&SetSite;
            site[4] = (delegate* unmanaged<void*, Guid*, void**, int>)&GetSite;

            //Assigned last, and this is the publication that makes the pair visible. Both tables are
            //fully built before either pointer is observable.
            _siteVtable = site;
            _commandVtable = command;
        }
    }

    /// <summary>The object behind whichever interface slot was called.</summary>
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
        //IUnknown resolves to the command slot, which is at offset zero -- so this object's identity
        //is stable whichever interface it is asked for, as COM requires.
        if (iid == Com.IUnknown || iid == Com.IExplorerCommand)
            *result = &instance->Command;
        else if (iid == Com.IObjectWithSite)
            *result = &instance->Site;
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

        //Explorer's site goes back before the memory holding the pointer to it does.
        Com.Release(instance->SitePointer);
        NativeMemory.Free(instance);

        return 0;
    }

    // ---- IExplorerCommand -----------------------------------------------------------

    /// <summary>
    /// The menu text — <c>Commit / Push…</c>, or <c>Commit / Push (feature/storage-gw)…</c>.
    ///
    /// <b>The branch goes before the ellipsis, not after it.</b> The ellipsis means "this opens
    /// something", so it belongs at the end of the whole label; <c>Commit / Push… (main)</c> reads as
    /// two separate things.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int GetTitle(void* self, void* items, char** name)
    {
        try
        {
            Instance* instance = Self(self);
            CommandConfig? config = CommandConfig.For(instance->Clsid);

            if (config is null)
                return Com.E_FAIL;

            if (!config.ShowBranch)
                return Com.ReturnString(config.Label, name);

            string? folder = FolderResolver.Resolve(items, instance->SitePointer);
            RepositoryAnswer answer = RepositoryLookup.For(folder);

            //No branch to show, or none that could be read. The plain label, which is exactly what
            //the static registry verb showed before this DLL existed.
            if (answer.Branch is not { Length: > 0 } branch)
                return Com.ReturnString(config.Label, name);

            return Com.ReturnString(Decorate(config.Label, branch), name);
        }
        catch
        {
            return Com.E_FAIL;
        }
    }

    /// <summary>
    /// Puts the branch in the label, keeping a trailing ellipsis at the end where it belongs.
    /// </summary>
    internal static string Decorate(string label, string branch)
    {
        //The ellipsis as a single character and as three dots: the .lang files use the character, but
        //a hand-edited actions.json label may not, and this must not leave "Commit / Push... (main)".
        foreach (string ellipsis in new[] { "…", "..." })
        {
            if (label.EndsWith(ellipsis, StringComparison.Ordinal))
                return $"{label[..^ellipsis.Length].TrimEnd()} ({branch}){ellipsis}";
        }

        return $"{label} ({branch})";
    }

    [UnmanagedCallersOnly]
    private static int GetIcon(void* self, void* items, char** icon)
    {
        _ = items;

        try
        {
            //E_NOTIMPL when there is no icon, which is how the shell is told to draw none. Returning
            //an empty string instead gets it looking for a resource called "".
            return Com.ReturnString(CommandConfig.For(Self(self)->Clsid)?.Icon, icon);
        }
        catch
        {
            return Com.E_FAIL;
        }
    }

    /// <summary>
    /// No tooltip. The shell shows the verb's own text, which is the whole label already.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int GetToolTip(void* self, void* items, char** tip)
    {
        _ = self;
        _ = items;

        if (tip is not null)
            *tip = null;

        return Com.E_NOTIMPL;
    }

    /// <summary>
    /// No canonical name. It exists so a command can be referred to from a ribbon or a policy, and
    /// nothing refers to these.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int GetCanonicalName(void* self, Guid* guid)
    {
        _ = self;

        if (guid is not null)
            *guid = Guid.Empty;

        return Com.E_NOTIMPL;
    }

    /// <summary>
    /// Whether to draw this entry at all. <b>The repository-aware visibility CLAUDE.md wanted from
    /// Phase 6</b>: an entry that needs a repository is hidden on a folder that is not one.
    ///
    /// Uncertainty shows the entry. A folder that could not be classified — a UNC path, a virtual
    /// item, a permission failure — keeps every entry visible, because a menu whose contents come
    /// and go for invisible reasons is worse than one entry too many.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int GetState(void* self, void* items, int okToBeSlow, uint* state)
    {
        if (state is null)
            return Com.E_POINTER;

        //Whatever happens below, the answer already in place is "show it".
        *state = Com.EcsEnabled;

        //Nothing here is slow enough to care, and saying so is better than being asked again with the
        //flag set. The one genuinely slow case -- a network path -- is refused inside RepositoryLookup
        //rather than deferred, because there is no second call to defer it to.
        _ = okToBeSlow;

        try
        {
            Instance* instance = Self(self);
            CommandConfig? config = CommandConfig.For(instance->Clsid);

            if (config is null)
                return Com.E_FAIL;

            if (!config.NeedsRepository)
                return Com.S_OK;

            string? folder = FolderResolver.Resolve(items, instance->SitePointer);

            if (RepositoryLookup.For(folder).Verdict == RepositoryVerdict.NotARepository)
                *state = Com.EcsHidden;

            return Com.S_OK;
        }
        catch
        {
            //Visible, per the rule above. The click still works: Invoke launches the CLI, which
            //refuses with a reason of its own if the folder is not a repository.
            return Com.S_OK;
        }
    }

    /// <summary>
    /// The click. Starts <c>flick.exe</c> and returns — the same command line the static registry
    /// verb held, so both routes reach the identical code path.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int Invoke(void* self, void* items, void* bindContext)
    {
        _ = bindContext;

        try
        {
            Instance* instance = Self(self);
            CommandConfig? config = CommandConfig.For(instance->Clsid);

            if (config is null)
                return Com.E_FAIL;

            string? folder = FolderResolver.Resolve(items, instance->SitePointer);

            return Launcher.Start(config.Exe, config.Verb, folder) ? Com.S_OK : Com.E_FAIL;
        }
        catch
        {
            return Com.E_FAIL;
        }
    }

    [UnmanagedCallersOnly]
    private static int GetFlags(void* self, uint* flags)
    {
        _ = self;

        if (flags is null)
            return Com.E_POINTER;

        *flags = Com.EcfDefault;
        return Com.S_OK;
    }

    /// <summary>
    /// No sub-commands. The <c>FlickGit</c> submenu is still an <c>ExtendedSubCommandsKey</c> of
    /// static verbs — see <c>ShellCommandIds.Handlers</c> for why only the two root entries are here.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int EnumSubCommands(void* self, void** enumerator)
    {
        _ = self;

        if (enumerator is not null)
            *enumerator = null;

        return Com.E_NOTIMPL;
    }

    // ---- IObjectWithSite ------------------------------------------------------------

    /// <summary>
    /// Explorer handing over the context it was invoked from, which for a click on a folder's
    /// background is the only route to the folder at all.
    /// </summary>
    [UnmanagedCallersOnly]
    private static int SetSite(void* self, void* site)
    {
        try
        {
            Instance* instance = Self(self);

            //The new one referenced before the old one is let go, so setting the same site twice
            //cannot free it in between.
            if (site is not null)
                ((delegate* unmanaged<void*, uint>)Com.Vtable(site)[1])(site);

            void* previous = instance->SitePointer;
            instance->SitePointer = site;

            Com.Release(previous);

            return Com.S_OK;
        }
        catch
        {
            return Com.E_FAIL;
        }
    }

    [UnmanagedCallersOnly]
    private static int GetSite(void* self, Guid* iid, void** result)
    {
        if (result is null)
            return Com.E_POINTER;

        *result = null;

        if (iid is null)
            return Com.E_INVALIDARG;

        try
        {
            void* site = Self(self)->SitePointer;

            return site is null ? Com.E_FAIL : Com.QueryInterface(site, *iid, out *result);
        }
        catch
        {
            return Com.E_FAIL;
        }
    }
}
