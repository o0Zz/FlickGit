using System.Runtime.InteropServices;
using System.Windows.Interop;
using FlickGit.Logging;

namespace FlickGit.App.Trigger;

/// <summary>
/// A window with no pixels, whose only job is to receive <c>WM_HOTKEY</c>.
///
/// The resident service has no main window — that is the point of it — but <c>RegisterHotKey</c>
/// needs a window handle and a message pump to deliver to. This is that handle, for however many
/// hotkeys the product claims: one for the quick-commit trigger, one for the palette.
///
/// <b>A message-only window</b> (<c>HWND_MESSAGE</c> as the parent), so it never appears in Alt+Tab,
/// never shows on the taskbar, and cannot be activated. Created on the WPF UI thread, so both
/// messages arrive on the dispatcher and the handler needs no marshalling before it opens a window.
///
/// Not the pre-warmed popup's handle, deliberately: <c>Warm()</c> is allowed to fail and set its
/// window to null, and a trigger that quietly stops existing because a pre-warm failed is worse
/// than a trigger that never existed.
/// </summary>
internal sealed partial class TriggerWindow : IDisposable
{
    private const int WmHotkey = 0x0312;

    /// <summary>Arbitrary, and only has to be unique within this window.</summary>
    private const int FirstHotkeyId = 0xF11C;

    /// <summary><c>HWND_MESSAGE</c>. A parent that makes the window invisible and unactivatable.</summary>
    private static readonly nint HwndMessage = -3;

    private readonly HwndSource _source;
    private readonly ILog _log;

    /// <summary>Registered hotkey id to what it does. Also the list to unregister on the way out.</summary>
    private readonly Dictionary<int, Action<nint>> _handlers = [];

    private int _nextHotkeyId = FirstHotkeyId;

    public TriggerWindow(ILog log)
    {
        _log = log;

        _source = new HwndSource(new HwndSourceParameters("FlickGit.Trigger")
        {
            ParentWindow = HwndMessage,

            //Load-bearing. Left unset, HwndSourceParameters supplies WS_CHILD | WS_VISIBLE, and a
            //child window of HWND_MESSAGE is not a valid message-only window.
            WindowStyle = 0,
        });

        _source.AddHook(OnMessage);
    }

    /// <summary>
    /// Claims <paramref name="gesture"/> globally, and routes it to <paramref name="onPressed"/>.
    /// </summary>
    /// <returns>Null on success, or why it failed.</returns>
    public string? RegisterHotkey(HotkeyGesture gesture, Action<nint> onPressed)
    {
        int id = _nextHotkeyId++;

        if (RegisterHotKey(_source.Handle, id, gesture.Modifiers, gesture.VirtualKey))
        {
            _handlers[id] = onPressed;
            return null;
        }

        int error = Marshal.GetLastWin32Error();

        //1409 is ERROR_HOTKEY_ALREADY_REGISTERED, which is the only failure a user can act on --
        //and the only one worth naming, because the action is "pick another combination".
        return error == 1409
            ? $"{gesture.Display} is already in use by another application."
            : $"{gesture.Display} could not be registered (Windows error {error}).";
    }

    /// <summary>
    /// The one message this window cares about.
    ///
    /// Receiving <c>WM_HOTKEY</c> credits this thread with the input, which is what lets the popup
    /// call <c>SetForegroundWindow</c> successfully. An Explorer-scoped hook would post its own
    /// message here and would carry no such credit — see <c>QuickCommitWindowHost.ShowAsync</c>,
    /// which checks rather than assumes.
    /// </summary>
    private nint OnMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmHotkey && _handlers.TryGetValue((int)wParam, out Action<nint>? handler))
        {
            handled = true;

            try
            {
                //The foreground window, read *here* and passed on. By the time anything else runs,
                //FlickGit's own popup may be the foreground window -- it is hidden rather than
                //closed between triggers -- and then asking Windows again answers "us" instead of
                //"the Explorer window the user was looking at".
                handler(GetForegroundWindow());
            }
            catch (Exception ex)
            {
                //Never let an exception escape into a window procedure. WPF would surface it on the
                //dispatcher, and this one arrives on every keypress of the trigger.
                _log.Error($"Trigger handler failed: {ex}");
            }
        }

        return 0;
    }

    public void Dispose()
    {
        foreach (int id in _handlers.Keys)
            UnregisterHotKey(_source.Handle, id);

        _handlers.Clear();

        _source.RemoveHook(OnMessage);
        _source.Dispose();
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hwnd, int id);
}
