using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace FlickGit.App.Views;

/// <summary>
/// The WPF end of the console pane: a plain child window for the console to live inside, and the two
/// messages that carry keyboard focus across the process boundary.
///
/// <b>It returns a container of its own rather than the console window, and that is structural.</b>
/// <see cref="HwndHost"/> subclasses whatever <see cref="BuildWindowCore"/> returns so that
/// <see cref="WndProc"/> receives its messages — which cannot be done to a window in another process,
/// and <see cref="WndProc"/> is where the whole focus mechanism lives. Three more reasons follow:
/// <see cref="DestroyWindowCore"/> could not work, since <c>DestroyWindow</c> only succeeds on the
/// creating thread; the console can exit while this element is still alive; and when the console host
/// is unavailable there is no window to return at all, and returning nothing is not a supported
/// contract. So the console is parented <i>into</i> this, and this is what WPF owns.
///
/// <b>Airspace.</b> A hosted HWND always paints over WPF content in its rectangle and ignores WPF
/// clipping, opacity and z-order. That is why the pane's status text is a sibling that replaces the
/// host rather than an overlay on top of it, and why nothing may animate or overlap here.
/// </summary>
internal sealed partial class ConsoleHost : HwndHost
{
    /// <summary>Arbitrary, and only has to be unique within this window.</summary>
    private const int EscapeHotkeyId = 0xC047;

    private nint _container;
    private bool _escapeRegistered;

    /// <summary>The window the console gets parented into. Zero before the element is laid out.</summary>
    public nint Container => _container;

    /// <summary>The user pressed a mouse button inside the console.</summary>
    public event Action? ClickedIn;

    /// <summary>The escape gesture was pressed while the console had focus.</summary>
    public event Action? EscapeRequested;

    /// <summary>
    /// The container took Win32 focus, and the console below it now needs to be handed the
    /// keyboard. <see cref="HwndHost"/> focuses the handle it was given whenever the element
    /// receives WPF keyboard focus, and the handle it was given is this container rather than the
    /// console.
    /// </summary>
    public event Action? FocusReceived;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        // Since Windows 10 1803 a window may host children of a different DPI awareness context only if
        // it was created while its thread had MIXED hosting behaviour. FlickGit is PerMonitorV2 and
        // conhost is not, so without this the SetParent in ConsoleSession is refused outright with
        // ERROR_ACCESS_DENIED. Thread state, so it is restored immediately.
        int previous = SetThreadDpiHostingBehavior(DpiHostingBehaviorMixed);

        try
        {
            // The predefined "static" class, so there is no window class to register and unregister.
            // WS_CLIPCHILDREN keeps it from painting over the console on every resize.
            _container = CreateWindowExW(
                0, "static", null,
                WsChild | WsVisible | WsClipChildren,
                0, 0, 1, 1,
                hwndParent.Handle, 0, 0, 0);
        }
        finally
        {
            SetThreadDpiHostingBehavior(previous);
        }

        return new HandleRef(this, _container);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        ReleaseEscape();

        if (_container != 0)
        {
            DestroyWindow(_container);
            _container = 0;
        }
    }

    protected override nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            // Sent to the parent when a mouse button goes down over a child window. It is the only way
            // a click in the console reaches managed code at all: the console is another process's
            // window, so WPF's own input pipeline never sees it.
            case WmParentNotify:
                int button = (int)(wParam & 0xFFFF);
                if (button is WmLButtonDown or WmRButtonDown or WmMButtonDown)
                    ClickedIn?.Invoke();
                break;

            // HwndHost puts Win32 focus on this container when the element gets WPF keyboard focus,
            // which is the half that makes WPF stop competing for the keyboard. It is not the half
            // the user wants, though -- a 'static' window renders nothing and eats every key -- so
            // the focus is passed straight down to the console.
            case WmSetFocus:
                FocusReceived?.Invoke();
                break;

            // WM_HOTKEY is delivered to the registering thread whatever has focus, which is what makes
            // it the only way out of a pane that swallows every key.
            case WmHotkey when (int)wParam == EscapeHotkeyId:
                handled = true;
                EscapeRequested?.Invoke();
                return 0;
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    /// <summary>
    /// Claims the escape gesture globally, and only for as long as the console holds focus.
    ///
    /// It has to be a real hotkey rather than a <c>KeyBinding</c>, because while the console has focus
    /// WPF receives no keystrokes to bind against. It has to be released the moment focus leaves, or
    /// FlickGit would be holding the combination away from every other application on the machine.
    /// </summary>
    public void ClaimEscape()
    {
        if (_escapeRegistered || _container == 0)
            return;

        _escapeRegistered = RegisterHotKey(_container, EscapeHotkeyId, ModControl | ModNoRepeat, VkOem3);
    }

    public void ReleaseEscape()
    {
        if (!_escapeRegistered)
            return;

        UnregisterHotKey(_container, EscapeHotkeyId);
        _escapeRegistered = false;
    }

    /// <summary>
    /// The container's client area in <b>physical pixels</b>, which is what <c>SetWindowPos</c> wants.
    /// WPF's ActualWidth is in device-independent units and would size the console wrong on any monitor
    /// that is not at 100%.
    /// </summary>
    public (int Width, int Height) ClientSize
    {
        get
        {
            if (_container == 0 || !GetClientRect(_container, out Rect rect))
                return (0, 0);

            return (rect.Right, rect.Bottom);
        }
    }

    /// <summary>
    /// WPF's tab ring cannot cross into another process — <c>IKeyboardInputSink</c> works by pumping a
    /// cooperating in-process child's messages, and there are none to pump here. Refusing is how focus
    /// skips the pane cleanly instead of landing on an element that cannot take it.
    /// </summary>
    protected override bool TabIntoCore(TraversalRequest request) => false;

    // ---- Win32 ----------------------------------------------------------------------------------

    private const int WmSetFocus = 0x0007;
    private const int WmParentNotify = 0x0210;
    private const int WmHotkey = 0x0312;
    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;

    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsClipChildren = 0x02000000;

    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;

    /// <summary>The <c>`</c> / <c>~</c> key.</summary>
    private const uint VkOem3 = 0xC0;

    private const int DpiHostingBehaviorMixed = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint handle);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint handle, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial int SetThreadDpiHostingBehavior(int value);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hwnd, int id);
}
