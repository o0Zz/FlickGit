namespace FlickGit.App.Trigger;

/// <summary>Which input opens the commit window.</summary>
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
