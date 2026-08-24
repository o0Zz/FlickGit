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
/// One placement, because there is one caller: the palette. The cursor-anchored placement beside it
/// belonged to the quick-commit popup, and went when that surface did — the commit window keeps the
/// position WPF gives it.
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
