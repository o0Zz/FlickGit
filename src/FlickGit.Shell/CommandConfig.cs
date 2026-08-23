using Microsoft.Win32;
using FlickGit.Shared;

namespace FlickGit.Shell;

/// <summary>
/// What one handler is: a label, a verb, an exe to run and two flags.
///
/// <b>Read from the registry rather than compiled in</b>, which is what keeps this DLL free of
/// interface text. The label arrives already localised, because the App wrote it from the
/// <c>.lang</c> file that was in force when the context menu was registered — so
/// <c>flick language fr</c> followed by a re-register changes the menu, and this assembly never
/// learns a word of French.
///
/// Cached for the life of the process, keyed by CLSID. These values change only when the menu is
/// re-registered, which rewrites the keys and cannot happen without the user asking for it — and
/// Explorer holds this DLL loaded until it exits anyway, so a stale entry would outlive the process
/// that could act on it.
/// </summary>
internal sealed record CommandConfig(
    string Label,
    string Exe,
    string Verb,
    string? Icon,
    bool ShowBranch,
    bool NeedsRepository)
{
    private static readonly Dictionary<Guid, CommandConfig?> Cache = [];
    private static readonly object Gate = new();

    /// <summary>
    /// The configuration for <paramref name="clsid"/>, or null when the key is not there or is
    /// missing something it cannot work without.
    ///
    /// Null is a real answer, not a failure to report: it means this CLSID is registered but not
    /// configured, and every method on the command then declines rather than guessing. That shows up
    /// as an entry Explorer does not draw, which is the correct outcome for a half-written
    /// registration.
    /// </summary>
    public static CommandConfig? For(Guid clsid)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(clsid, out CommandConfig? cached))
                return cached;

            CommandConfig? loaded = Load(clsid);
            Cache[clsid] = loaded;
            return loaded;
        }
    }

    private static CommandConfig? Load(Guid clsid)
    {
        try
        {
            //HKCU only. The whole install is per-user -- CLAUDE.md, "Per-user install only. No
            //administrator rights" -- so a machine-wide registration is not something to look for.
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\CLSID\{{{clsid.ToString().ToUpperInvariant()}}}");

            if (key is null)
                return null;

            string? label = key.GetValue(ShellCommandIds.ValueLabel) as string;
            string? exe = key.GetValue(ShellCommandIds.ValueExe) as string;
            string? verb = key.GetValue(ShellCommandIds.ValueVerb) as string;

            //A label with no verb would draw an entry that does nothing; a verb with no exe has
            //nothing to run. Either way there is no usable command here.
            if (string.IsNullOrEmpty(label) || string.IsNullOrEmpty(exe) || string.IsNullOrEmpty(verb))
                return null;

            return new CommandConfig(
                label,
                exe,
                verb,
                key.GetValue(ShellCommandIds.ValueIcon) as string is { Length: > 0 } icon ? icon : null,
                IsSet(key, ShellCommandIds.ValueShowBranch),
                IsSet(key, ShellCommandIds.ValueNeedsRepository));
        }
        catch
        {
            //A registry the process cannot read. Nothing to draw.
            return null;
        }
    }

    private static bool IsSet(RegistryKey key, string name) => key.GetValue(name) as string == "1";
}
