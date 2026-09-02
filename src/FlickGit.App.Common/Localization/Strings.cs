using System.Globalization;
using System.IO;
using System.Reflection;

namespace FlickGit.App.Localization;

/// <summary>
/// The interface language: every piece of text the windows show.
///
/// One <c>key = value</c> file per language, embedded in the exe. <b>Not .resx</b>:
/// satellite assemblies are DLLs in per-culture subdirectories, and a plain text file is
/// something a translator can open without Visual Studio and send back as a diff.
///
/// <c>en.lang</c> is the master and the per-key fallback, so a half-finished translation
/// shows English rather than raw key names. Adding a language is adding a file: the csproj
/// embeds <c>Languages/*.lang</c> by wildcard and nothing here names them one by one.
///
/// <see cref="Available"/> enumerates what is embedded, which is what <c>flick language</c>
/// prints. It reads every file once at first use -- five small text files, on a verb that is not
/// on any latency path -- rather than keeping a second list of codes that could disagree with the
/// files actually shipped.
/// </summary>
public static class Strings
{
    /// <summary>Matches the <c>LogicalName</c> the csproj assigns.</summary>
    private const string ResourcePrefix = "FlickGit.Languages.";

    private const string ResourceSuffix = ".lang";

    /// <summary>The language every other one falls back to, key by key.</summary>
    private const string FallbackCode = "en";

    private static readonly Dictionary<string, string> English =
        Load(FallbackCode) ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private static Dictionary<string, string> _current = English;

    /// <summary>
    /// The language actually in use, as a two-letter code.
    ///
    /// Not what the settings file asked for: an unknown code falls back to English, and this says
    /// so. <c>flick language</c> prints it, so a typo in <c>settings.json</c> is answerable.
    /// </summary>
    public static string CurrentCode { get; private set; } = FallbackCode;

    /// <summary>An embedded language: its two-letter code, and its name for itself.</summary>
    /// <param name="Code">The value that goes in <c>settings.json</c>.</param>
    /// <param name="Name">
    /// <c>@name</c> from the file, never translated -- someone looking for their language in an
    /// interface they cannot read is looking for the word "Français", not for "French".
    /// </param>
    public readonly record struct Language(string Code, string Name);

    /// <summary>
    /// Every language embedded in the exe, English first and the rest by name.
    ///
    /// Discovered from the manifest resources rather than declared, so adding a language stays
    /// "add a file to Languages/" -- the csproj embeds them by wildcard for the same reason.
    /// </summary>
    public static IReadOnlyList<Language> Available { get; } = Discover();

    /// <summary>True when <paramref name="code"/> names a language that is actually embedded.</summary>
    public static bool Has(string code) =>
        Available.Any(language => language.Code.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Switches language. Pass null or empty to follow Windows; an unknown code falls
    /// back to English rather than failing.
    ///
    /// Must be called before the first window is constructed — every view reads its text
    /// on construction, and the resident service keeps window instances alive for the
    /// whole session, so a language chosen afterwards would arrive too late for them.
    /// </summary>
    public static void Use(string? code)
    {
        string resolved = string.IsNullOrWhiteSpace(code)
            ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            : code.Trim();

        Dictionary<string, string>? table = Load(resolved);

        _current = table ?? English;
        CurrentCode = table is null ? FallbackCode : resolved.ToLowerInvariant();
    }

    /// <summary>
    /// The text for <paramref name="key"/>, falling back to English and then to the key
    /// itself.
    ///
    /// Returning the key rather than throwing is deliberate: a missing string must show as
    /// an ugly label in a window the user can still use, never as an exception that takes
    /// the commit window down mid-commit.
    /// </summary>
    public static string Get(string key) =>
        _current.TryGetValue(key, out string? value) ? value
        : English.TryGetValue(key, out string? fallback) ? fallback
        : key;

    /// <summary>Formatted lookup. Placeholders are <c>{0}</c>-style, documented per key in the .lang file.</summary>
    public static string Get(string key, params object?[] args)
    {
        string format = Get(key);

        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            //A translation with a malformed placeholder must not crash a window. Showing
            //the unformatted text is bad; taking the app down is worse.
            return format;
        }
    }

    private static Dictionary<string, string>? Load(string code)
    {
        Assembly assembly = typeof(Strings).Assembly;

        using Stream? stream = assembly.GetManifestResourceStream(ResourcePrefix + code + ResourceSuffix);
        if (stream is null)
            return null;

        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);

        var table = new Dictionary<string, string>(StringComparer.Ordinal);

        while (reader.ReadLine() is { } line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            int separator = trimmed.IndexOf('=');
            if (separator <= 0)
                continue;

            string key = trimmed[..separator].Trim();

            //Everything after the *first* '=' is the value, so a value containing '='
            //needs no escaping -- which matters for strings that show a command line.
            string value = trimmed[(separator + 1)..].Trim().Replace("\\n", "\n");

            if (key.Length > 0)
                table[key] = value;
        }

        return table;
    }

    private static IReadOnlyList<Language> Discover()
    {
        Assembly assembly = typeof(Strings).Assembly;

        var found = new List<Language>();

        foreach (string resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !resource.EndsWith(ResourceSuffix, StringComparison.Ordinal))
                continue;

            string code = resource[ResourcePrefix.Length..^ResourceSuffix.Length];

            //The code, not the name, when a file forgot its @name line: a picker listing a blank
            //row is worse than one listing "pt".
            string name = Load(code)?.GetValueOrDefault("@name") is { Length: > 0 } declared
                ? declared
                : code;

            found.Add(new Language(code.ToLowerInvariant(), name));
        }

        return found
            .OrderByDescending(language => language.Code == FallbackCode)
            .ThenBy(language => language.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
