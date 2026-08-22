using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using FlickGit.Logging;

namespace FlickGit.App.Resident;

/// <summary>
/// The two things every resident window does: get warmed at logon, and get put on screen.
///
/// Both are the same for all three windows, and both are fiddly enough that having them written out
/// three times meant three chances to get the ordering wrong — the <c>GetForegroundWindow</c> import
/// was genuinely declared twice before this existed.
/// </summary>
internal static partial class ResidentWindow
{
    /// <summary>
    /// Builds the HWND and forces a full layout pass, without ever showing the window.
    ///
    /// This is the whole of the resident service's speed advantage. CLAUDE.md, "Resident Service": a
    /// cold WPF start pays CLR startup, JIT, PresentationFramework/PresentationCore/WindowsBase load,
    /// theme dictionary resolution, HWND creation and first render — 400–800 ms. Paying it once at
    /// logon is what makes the first right-click of a session cost a repaint instead.
    ///
    /// The measure and arrange are the point rather than the construction: they are what make WPF
    /// resolve the theme dictionaries, apply every control template and JIT the layout path.
    ///
    /// A failure is logged and swallowed — a service that could not pre-warm is merely as slow as no
    /// service at all, which is not worth failing startup over.
    /// </summary>
    public static bool TryWarm(Window window, string name, ILog log)
    {
        try
        {
            //The HWND now rather than on the first Show(). Window creation is a round trip into
            //user32 and the composition target, and it was the largest item left in the first
            //right-click of a session. It is also what lets the placement arithmetic run before the
            //window is visible: GetWindowRect needs a real window to measure.
            _ = new WindowInteropHelper(window).EnsureHandle();

            //A window that sizes to its content leaves that dimension NaN, and NaN in the arrange
            //rect poisons the very layout pass this exists to force. Measuring against infinity
            //instead lets the content decide, which is what the arrange then uses.
            var available = new Size(
                double.IsNaN(window.Width) ? double.PositiveInfinity : window.Width,
                double.IsNaN(window.Height) ? double.PositiveInfinity : window.Height);

            window.Measure(available);
            window.Arrange(new Rect(new Point(0, 0), window.DesiredSize));
            window.UpdateLayout();

            log.Info($"{name} pre-warmed.");
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"{name} pre-warm failed, so the first one will be slower: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Places, shows and activates <paramref name="window"/>, and says whether Windows agreed.
    /// </summary>
    /// <param name="place">
    /// Where to put it, in physical pixels, before it is shown — see <c>PopupPlacement</c>. Null for a
    /// window that keeps whatever position WPF gives it.
    /// </param>
    /// <returns>
    /// False when activation was refused. Windows refuses <c>SetForegroundWindow</c> when nothing has
    /// credited this process with the last input event, which happens whenever a verb is run from
    /// something that is not itself in the foreground.
    ///
    /// The state that must never exist is "on top of Explorer without keyboard focus": there, the
    /// user's Enter reaches Explorer's file list and opens whatever was selected. So a caller that set
    /// <c>Topmost</c> has to drop it when this returns false.
    /// </returns>
    public static bool Present(Window window, Action<nint>? place = null)
    {
        nint handle = new WindowInteropHelper(window).EnsureHandle();

        place?.Invoke(handle);

        window.Show();

        //Hidden rather than closed last time, so it may still be minimised from then.
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        //The foreground handover, cashed in. The stub granted this process the right before sending
        //the request; without Activate the window comes up behind Explorer.
        window.Activate();

        return GetForegroundWindow() == handle;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
}
