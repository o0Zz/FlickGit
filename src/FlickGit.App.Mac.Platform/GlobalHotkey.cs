using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FlickGit.Logging;

namespace FlickGit.App.Mac;

/// <summary>
/// The global hotkeys, through Carbon's <c>RegisterEventHotKey</c>.
///
/// <b>Carbon rather than an NSEvent global monitor, and the reason is a permission.</b> A global
/// <c>NSEvent</c> monitor sees every keystroke system-wide and therefore requires Accessibility
/// access — which the user must grant in System Settings, and which is exactly the kind of prompt
/// CLAUDE.md's Windows trigger is designed to avoid ("a global low-level hook on a first run by an
/// unsigned binary is what EDR products flag"). <c>RegisterEventHotKey</c> asks the system for one
/// specific combination and needs no permission at all. It is the same trade the Windows side makes
/// by choosing <c>RegisterHotKey</c> over <c>WH_KEYBOARD_LL</c>.
///
/// Carbon is deprecated and this API is not: <c>RegisterEventHotKey</c> remains the supported way to
/// take a system-wide hotkey without Accessibility, and every launcher on the platform uses it.
///
/// <b>The handler does nothing but hand over.</b> It runs on the main run loop, so the callback
/// raises the event and returns; resolving a folder or opening a window from inside it would block
/// the loop that delivers every other key.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class GlobalHotkey(ILog log) : IDisposable
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    /// <summary>kEventClassKeyboard / kEventHotKeyPressed.</summary>
    private const uint EventClassKeyboard = 0x6B657962;

    private const uint EventHotKeyPressed = 5;

    /// <summary>Carbon's own modifier bits, which are not the Cocoa ones.</summary>
    private const uint CommandKey = 0x0100;

    private const uint OptionKey = 0x0800;

    /// <summary>Virtual key codes: ANSI_G and ANSI_R.</summary>
    private const uint KeyG = 5;

    private const uint KeyR = 15;

    private readonly List<IntPtr> _registered = [];
    private HotkeyHandler? _handler;
    private IntPtr _handlerRef;

    /// <summary>Raised on the main run loop when the commit hotkey is pressed.</summary>
    public event Action? CommitRequested;

    /// <summary>Raised on the main run loop when the palette hotkey is pressed.</summary>
    public event Action? PaletteRequested;

    /// <summary>
    /// Registers both hotkeys, and reports which of them could not be taken.
    ///
    /// A combination another application already holds fails rather than stealing it, and the
    /// failure is a sentence rather than an exception: a hotkey nobody could register is a feature
    /// the user does not have, not a reason the service should refuse to start.
    /// </summary>
    public string? Install()
    {
        //Cmd+Alt+G and Cmd+Alt+R, mirroring Ctrl+Alt+G and Ctrl+Alt+R on Windows -- Command is what
        //Ctrl means on this platform.
        var failures = new List<string>();

        _handler = OnHotkey;

        var kind = new EventTypeSpec { EventClass = EventClassKeyboard, EventKind = EventHotKeyPressed };

        if (InstallEventHandler(GetApplicationEventTarget(), _handler, 1, ref kind, IntPtr.Zero, out _handlerRef) != 0)
            return "The hotkey handler could not be installed.";

        if (!Register(KeyG, id: 1))
            failures.Add("Cmd+Alt+G");

        if (!Register(KeyR, id: 2))
            failures.Add("Cmd+Alt+R");

        return failures.Count == 0
            ? null
            : $"{string.Join(" and ", failures)} could not be registered: another application already has it.";
    }

    private bool Register(uint keyCode, uint id)
    {
        var identifier = new EventHotKeyID { Signature = 0x464C4B47 /* 'FLKG' */, Id = id };

        int status = RegisterEventHotKey(
            keyCode,
            CommandKey | OptionKey,
            identifier,
            GetApplicationEventTarget(),
            0,
            out IntPtr reference);

        if (status != 0 || reference == IntPtr.Zero)
        {
            log.Warn($"RegisterEventHotKey for key {keyCode} failed with status {status}.");

            return false;
        }

        _registered.Add(reference);

        return true;
    }

    private int OnHotkey(IntPtr callRef, IntPtr @event, IntPtr userData)
    {
        var identifier = default(EventHotKeyID);

        //kEventParamDirectObject / typeEventHotKeyID.
        if (GetEventParameter(@event, 0x2D2D2D2D, 0x686B6964, IntPtr.Zero, Marshal.SizeOf<EventHotKeyID>(),
                IntPtr.Zero, ref identifier) != 0)
        {
            return 0;
        }

        //Raised and returned from immediately. See the class remarks: this is the main run loop.
        switch (identifier.Id)
        {
            case 1:
                CommitRequested?.Invoke();
                break;

            case 2:
                PaletteRequested?.Invoke();
                break;
        }

        return 0;
    }

    public void Dispose()
    {
        foreach (IntPtr reference in _registered)
            UnregisterEventHotKey(reference);

        _registered.Clear();

        if (_handlerRef != IntPtr.Zero)
        {
            RemoveEventHandler(_handlerRef);
            _handlerRef = IntPtr.Zero;
        }

        //Held until here so the delegate is not collected while Carbon still has the pointer.
        _handler = null;
    }

    private delegate int HotkeyHandler(IntPtr callRef, IntPtr @event, IntPtr userData);

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyID
    {
        public uint Signature;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [LibraryImport(Carbon)]
    private static partial IntPtr GetApplicationEventTarget();

    [DllImport(Carbon)]
    private static extern int InstallEventHandler(
        IntPtr target,
        HotkeyHandler handler,
        uint count,
        ref EventTypeSpec kinds,
        IntPtr userData,
        out IntPtr handlerRef);

    [LibraryImport(Carbon)]
    private static partial int RemoveEventHandler(IntPtr handlerRef);

    [DllImport(Carbon)]
    private static extern int RegisterEventHotKey(
        uint keyCode,
        uint modifiers,
        EventHotKeyID identifier,
        IntPtr target,
        uint options,
        out IntPtr reference);

    [LibraryImport(Carbon)]
    private static partial int UnregisterEventHotKey(IntPtr reference);

    [DllImport(Carbon)]
    private static extern int GetEventParameter(
        IntPtr @event,
        uint name,
        uint type,
        IntPtr outActualType,
        int bufferSize,
        IntPtr outActualSize,
        ref EventHotKeyID data);
}
