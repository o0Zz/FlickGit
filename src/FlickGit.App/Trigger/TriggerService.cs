using FlickGit.App.Settings;
using FlickGit.Logging;

namespace FlickGit.App.Trigger;

/// <summary>Which input opens the quick-commit popup.</summary>
public enum TriggerKind
{
    /// <summary>
    /// A global hotkey through <c>RegisterHotKey</c>. The default, and the only mechanism that
    /// installs no system-wide input hook — which is the whole reason it is the default: a global
    /// low-level hook on a first run by an unsigned binary is what EDR products flag.
    /// </summary>
    Hotkey,

    /// <summary>Nothing. The tray icon and the context menu are still there.</summary>
    None,
}

/// <param name="QuickCommit">What `flick diag doctor` prints for the quick-commit trigger.</param>
/// <param name="Palette">The same, for the palette's hotkey.</param>
/// <param name="Error">
/// Set when something could not be installed. Both reasons when both failed, because a user whose
/// two hotkeys are both taken needs to be told about both of them once rather than about one twice.
/// </param>
public readonly record struct TriggerStartup(string QuickCommit, string Palette, string? Error);

public sealed class TriggerService(FlickSettings settings, ILog log) : IDisposable
{
    private TriggerWindow? _window;

    /// <summary>
    /// Installs the configured trigger.
    ///
    /// Never throws and never prevents startup. A hotkey somebody else already owns must not cost
    /// the user their tray icon, their context menu or their pipe — CLAUDE.md, "Definition of Done":
    /// every feature works with the resident service stopped, so no feature may stop it starting.
    /// </summary>
    public TriggerStartup Start(Action<nint> onQuickCommit, Action<nint> onPalette)
    {
        try
        {
            _window = new TriggerWindow(log);

            var errors = new List<string>();

            //Two independent claims. TriggerKind.None turns off the quick-commit trigger, not the
            //palette: they are separate surfaces, and a user who wants only one should get only one.
            string quickCommit = settings.Trigger == TriggerKind.None
                ? "none"
                : Claim(settings.HotkeyGesture, HotkeyGesture.Default, onQuickCommit, errors);

            string palette = Claim(settings.PaletteHotkeyGesture, HotkeyGesture.DefaultPalette, onPalette, errors);

            return new TriggerStartup(
                quickCommit,
                palette,
                errors.Count == 0
                    ? null
                    : $"{string.Join("\n\n", errors)}\n\nChange it in:\n{FlickSettings.FilePath}");
        }
        catch (Exception ex)
        {
            log.Error($"The triggers could not be installed: {ex}");
            return new TriggerStartup("none", "none", $"FlickGit's hotkeys could not be installed:\n\n{ex.Message}");
        }
    }

    /// <summary>Claims one gesture, and describes what happened for `diag doctor`.</summary>
    private string Claim(string configured, HotkeyGesture fallback, Action<nint> onPressed, List<string> errors)
    {
        HotkeyGesture gesture = Resolve(configured, fallback);
        string? error = _window!.RegisterHotkey(gesture, onPressed);

        if (error is null)
            return $"{gesture.Display} (global hotkey)";

        errors.Add(error);
        return $"{gesture.Display} (FAILED)";
    }

    /// <summary>
    /// What `flick diag doctor` prints. Describes the configuration even when nothing started, so
    /// "why does my hotkey do nothing" has an answer in the place people look for one.
    /// </summary>
    public string Describe() => settings.Trigger == TriggerKind.None
        ? "none"
        : $"{Resolve(settings.HotkeyGesture, HotkeyGesture.Default).Display} ({(_window is null ? "not started" : "registered")})";

    /// <summary>The same, for the palette.</summary>
    public string DescribePalette() =>
        $"{Resolve(settings.PaletteHotkeyGesture, HotkeyGesture.DefaultPalette).Display} ({(_window is null ? "not started" : "registered")})";

    private HotkeyGesture Resolve(string configured, HotkeyGesture fallback)
    {
        if (HotkeyGesture.TryParse(configured, out HotkeyGesture parsed))
            return parsed;

        //A typo in a hand-edited settings file costs the configured combination, not the feature.
        log.Warn($"'{configured}' is not a valid hotkey; using {fallback.Display}.");
        return fallback;
    }

    public void Dispose()
    {
        _window?.Dispose();
        _window = null;
    }
}
