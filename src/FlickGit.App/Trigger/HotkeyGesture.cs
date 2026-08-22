using System.Windows.Input;

namespace FlickGit.App.Trigger;

/// <summary>
/// A key combination, in the form <c>RegisterHotKey</c> wants it.
///
/// Parsed from a settings string rather than offered as a picker, because the settings window is
/// Phase 5 and a key chooser is the one control that cannot be a text box. An unparseable value
/// falls back to the default and is logged — never refused, because a typo in a settings file must
/// not cost the user their trigger entirely.
/// </summary>
/// <param name="Modifiers">The <c>MOD_*</c> flags, already including <c>MOD_NOREPEAT</c>.</param>
/// <param name="VirtualKey">The <c>VK_*</c> code.</param>
/// <param name="Display">What to show a human, normalised: "Ctrl+Alt+G".</param>
public readonly record struct HotkeyGesture(uint Modifiers, uint VirtualKey, string Display)
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    /// <summary>
    /// Held down means one <c>WM_HOTKEY</c>, not a stream of them.
    ///
    /// Without it, resting on the combination opens a popup per auto-repeat — and each one resets
    /// the message box the user is typing into.
    /// </summary>
    private const uint ModNoRepeat = 0x4000;

    /// <summary>
    /// <c>Ctrl+Alt+G</c>, per CLAUDE.md. Chosen because nothing in Windows or the common developer
    /// tools claims it, which is the whole requirement for a global hotkey.
    /// </summary>
    public static HotkeyGesture Default { get; } =
        new(ModControl | ModAlt | ModNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(Key.G), "Ctrl+Alt+G");

    /// <summary>
    /// <c>Ctrl+Alt+R</c>, for the palette. Not <c>Ctrl+Alt+G</c>, which CLAUDE.md names for both
    /// surfaces but which only one of them can have — see <c>FlickSettings.PaletteHotkeyGesture</c>.
    /// </summary>
    public static HotkeyGesture DefaultPalette { get; } =
        new(ModControl | ModAlt | ModNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(Key.R), "Ctrl+Alt+R");

    /// <summary>
    /// Parses "Ctrl+Alt+G", "Ctrl+Shift+F12", "Win+Alt+C" and so on.
    ///
    /// At least one modifier is required. A bare key as a <i>global</i> hotkey would take it away
    /// from every application on the machine, which is exactly the problem the Explorer-scoped hook
    /// exists to solve — so this refuses rather than letting a settings file cause it.
    /// </summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        uint modifiers = 0;
        Key key = Key.None;
        var parts = new List<string>();

        foreach (string token in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    modifiers |= ModControl;
                    parts.Add("Ctrl");
                    break;

                case "alt":
                    modifiers |= ModAlt;
                    parts.Add("Alt");
                    break;

                case "shift":
                    modifiers |= ModShift;
                    parts.Add("Shift");
                    break;

                case "win" or "windows":
                    modifiers |= ModWin;
                    parts.Add("Win");
                    break;

                default:
                    //The last non-modifier token wins, so "Ctrl+G" and not "Ctrl+G+H".
                    if (!Enum.TryParse(token, ignoreCase: true, out key) || key == Key.None)
                        return false;

                    parts.Add(token.ToUpperInvariant().Length == 1 ? token.ToUpperInvariant() : key.ToString());
                    break;
            }
        }

        if (modifiers == 0 || key == Key.None)
            return false;

        gesture = new HotkeyGesture(
            modifiers | ModNoRepeat,
            (uint)KeyInterop.VirtualKeyFromKey(key),
            string.Join('+', parts));

        return true;
    }
}
