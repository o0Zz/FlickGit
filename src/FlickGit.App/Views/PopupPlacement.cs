using System.Runtime.InteropServices;

namespace FlickGit.App.Views;

/// <summary>
/// Places a window on the monitor the mouse pointer is on.
///
/// <b>All of it in physical pixels, and all of it through <c>SetWindowPos</c>.</b> That is not an
/// implementation preference. This process declares <c>PerMonitorV2</c> DPI awareness (see
/// <c>app.manifest</c>, where the diff renderer's crispness depends on it), while WPF's
/// <c>Window.Left</c>/<c>Top</c> and <c>SystemParameters.WorkArea</c> are device-independent units
/// derived from the <i>primary</i> monitor's scale. On the ordinary laptop-plus-monitor desktop
/// where one display is at 150% and the other at 100%, doing this arithmetic in DIPs puts the window
/// on the wrong monitor or off the edge of the screen.
///
/// Positioning before <c>Show()</c> has a second benefit: the window is already on the target
/// monitor when it first paints, so WPF resolves that monitor's scale factor rather than laying out
/// at the wrong one and rescaling on the <c>WM_DPICHANGED</c> that follows.
///
/// Two placements. <see cref="NearTopOfActiveScreen"/> is the palette's, and deliberately ignores
/// where the pointer is. <see cref="AtPoint"/> is the tray menu's, and is anchored to it.
///
/// The tray menu needs its own because H.NotifyIcon's is wrong across mixed scale factors, and
/// wrong in a way no setting can correct: it reports the click in physical pixels, divides by a DPI
/// factor taken from an unpositioned <c>HwndSource</c> — so always the <i>primary</i> monitor's — and
/// hands the result to <c>ContextMenu.HorizontalOffset</c>, which WPF multiplies back up by the
/// popup HWND's own DPI, i.e. the monitor it last lived on. Divide by one display's scale, multiply
/// by another's, and the menu lands a scale factor away from the pointer, often on a different
/// screen. So the menu is opened by the library and immediately repositioned here.
/// </summary>
internal static partial class PopupPlacement
{
    private const uint MonitorDefaultToNearest = 2;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>
    /// Centres <paramref name="handle"/> horizontally and puts it near the top of the monitor the
    /// pointer is on.
    ///
    /// The palette's placement, and deliberately not anchored to the cursor. A hotkey pressed from
    /// anywhere says nothing about where the pointer happens to be, so anchoring to it would put the
    /// palette somewhere different every time — and a surface driven entirely by the keyboard is
    /// usable without looking only if it appears in the same place twice.
    ///
    /// The monitor is still chosen by the pointer, because that is the only signal available about
    /// which screen the user is working on.
    /// </summary>
    public static void NearTopOfActiveScreen(nint handle)
    {
        try
        {
            if (handle == 0 || !GetCursorPos(out Point cursor))
                return;

            nint monitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

            if (monitor == 0 || !GetMonitorInfoW(monitor, ref info) || !GetWindowRect(handle, out Rect bounds))
                return;

            int width = bounds.Right - bounds.Left;
            int height = bounds.Bottom - bounds.Top;
            Rect work = info.Work;

            int x = work.Left + Math.Max(0, (work.Right - work.Left - width) / 2);

            //A fifth of the way down rather than centred: it leaves the list room to grow downwards
            //without the window moving, and it keeps the query box roughly at eye level.
            int y = work.Top + Math.Max(0, (work.Bottom - work.Top) / 5);

            y = Math.Min(y, Math.Max(work.Top, work.Bottom - height));

            SetWindowPos(handle, 0, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        catch (Exception)
        {
            //Placement is cosmetic. Nothing here is worth failing the hotkey over.
        }
    }

    /// <summary>
    /// The pointer, in physical pixels.
    ///
    /// Exposed so the tray menu can capture its anchor at the moment the click is reported rather
    /// than at the moment the menu paints: H.NotifyIcon defers a left-click open through its
    /// double-click timer, and the pointer can have moved by then.
    /// </summary>
    public static bool TryGetCursor(out int x, out int y)
    {
        bool ok = GetCursorPos(out Point cursor);

        x = cursor.X;
        y = cursor.Y;
        return ok;
    }

    /// <summary>
    /// Puts <paramref name="handle"/> at a point, in physical pixels, and keeps it inside the work
    /// area of the monitor that point is on.
    ///
    /// The tray menu's placement. It <b>flips</b> rather than slides: a menu that would run past the
    /// right or bottom edge is drawn to the left of, or above, the anchor. That is what every other
    /// notification-area menu does, and with the taskbar at the bottom it is the only behaviour that
    /// does not cover the icon that was just clicked. Sliding it up instead would leave the menu
    /// under the pointer, so the first item is highlighted before the user has aimed at anything.
    ///
    /// Safe to call more than once for the same menu: the size is re-read every time, so a second
    /// call after Windows has rescaled the popup for a new monitor simply corrects the flip.
    /// </summary>
    public static void AtPoint(nint handle, int x, int y)
    {
        try
        {
            if (handle == 0)
                return;

            var anchor = new Point { X = x, Y = y };
            nint monitor = MonitorFromPoint(anchor, MonitorDefaultToNearest);

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };

            if (monitor == 0 || !GetMonitorInfoW(monitor, ref info) || !GetWindowRect(handle, out Rect bounds))
                return;

            int width = bounds.Right - bounds.Left;
            int height = bounds.Bottom - bounds.Top;
            Rect work = info.Work;

            if (x + width > work.Right)
                x -= width;

            if (y + height > work.Bottom)
                y -= height;

            //After the flip, not instead of it. A menu taller than the work area has nowhere to go
            //either way, and starting it inside the screen is the least bad of the two.
            x = Math.Max(work.Left, Math.Min(x, Math.Max(work.Left, work.Right - width)));
            y = Math.Max(work.Top, Math.Min(y, Math.Max(work.Top, work.Bottom - height)));

            //SwpNoActivate because activating the popup is H.NotifyIcon's job -- it calls
            //SetForegroundWindow on this same handle, which is the only reason the menu closes when
            //the user clicks somewhere else.
            SetWindowPos(handle, 0, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        catch (Exception)
        {
            //Placement is cosmetic. Nothing here is worth failing the tray click over.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;

        /// <summary>The monitor minus the taskbar. What the window must stay inside.</summary>
        public Rect Work;

        public uint Flags;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(Point point, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint handle, out Rect rect);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int cx, int cy, uint flags);
}
