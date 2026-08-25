using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlickGit.App.Trigger;

namespace FlickGit.App.Settings;

/// <summary>
/// User settings, persisted to <c>%LOCALAPPDATA%\FlickGit\settings.json</c>.
///
/// <b><see cref="SchemaVersion"/> is written and checked.</b> A file from a future version is
/// refused rather than silently migrated downward, which would lose whatever it added.
///
/// <b>API keys are never in this file.</b> Windows Credential Manager only -- there is no property
/// here to put one in, which is the point.
/// </summary>
public sealed class FlickSettings
{
    /// <summary>Bumped when a property is removed or its meaning changes. Adding one does not.</summary>
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string GitPath { get; set; } = string.Empty;

    /// <summary>Empty means resolve per repository: remote HEAD, then main, then master.</summary>
    public string PrimaryBranch { get; set; } = string.Empty;

    public bool WarnWhenCommittingToPrimaryBranch { get; set; } = true;

    public bool CloseCommitWindowAfterSuccess { get; set; } = true;

    public bool ShowSuccessNotification { get; set; } = true;

    /// <summary>Interface language as a two-letter code. Empty follows Windows.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Monospace, or the alignment between the diff panes is meaningless.</summary>
    public string DiffFontFamily { get; set; } = "Cascadia Mono, Consolas, Courier New";

    public double DiffFontSize { get; set; } = 12.5;

    public bool VerboseLogging { get; set; }

    /// <summary>
    /// A global hotkey, or nothing. The two Explorer-scoped input hooks are not built, and there is no
    /// value here for them until they are: a setting that silently falls back to something else is
    /// worse than one that does not exist.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<TriggerKind>))]
    public TriggerKind Trigger { get; set; } = TriggerKind.Hotkey;

    /// <summary>
    /// At least one modifier is required: a bare key claimed globally would be taken away from every
    /// application on the machine.
    /// </summary>
    public string HotkeyGesture { get; set; } = "Ctrl+Alt+G";

    /// <summary>
    /// <b>Not Ctrl+Alt+G.</b> Two <c>RegisterHotKey</c> calls for one combination cannot both
    /// succeed -- the second fails with ERROR_HOTKEY_ALREADY_REGISTERED -- and the commit trigger
    /// keeps it. Configurable because the combination may already belong to something else on this
    /// machine, which is not a thing a constant can fix.
    /// </summary>
    public string PaletteHotkeyGesture { get; set; } = "Ctrl+Alt+R";

    /// <summary>
    /// Where to look for repositories beyond the ones already used. Empty by default: guessing a
    /// folder would either miss the user's repositories or walk a tree nobody asked to have walked,
    /// and the most-recently-used list fills the palette anyway.
    /// </summary>
    public List<string> PaletteScanRoots { get; set; } = [];

    /// <summary>
    /// Which service writes the commit message: <c>anthropic</c>, <c>openai</c>, <c>copilot</c>,
    /// <c>ollama</c> or <c>disabled</c>.
    ///
    /// <b>Naming one, with a key stored for it, is the consent.</b> There is nothing else a configured
    /// provider could be for: every message it writes is written from a diff. <c>ollama</c> needs no
    /// key and asks for no consent, because nothing sent to it leaves the machine.
    /// </summary>
    public string AiProvider { get; set; } = "anthropic";

    /// <summary>
    /// Empty means the provider's default -- <b>except for Ollama, which has none and requires this.</b>
    /// Which models exist there is a fact about the user's own disk, so any guess would 404 for most
    /// people; `ollama list` says what to put here.
    /// </summary>
    public string AiModel { get; set; } = string.Empty;

    /// <summary>
    /// Where Ollama is listening. A setting because running the model on a bigger machine on the same
    /// network is the ordinary reason to use Ollama at all -- and pointing it off this machine is
    /// exactly that: the diff then leaves this computer, even though it does not leave the network.
    /// </summary>
    public string AiOllamaUrl { get; set; } = FlickGit.Ai.AiOptions.DefaultOllamaUrl;

    /// <summary>OpenAI only. <c>none</c> is the latency baseline; <c>low</c> is the next step up.</summary>
    public string AiReasoningEffort { get; set; } = "none";

    /// <summary>
    /// Defaulted low. Above it the payload becomes a file summary plus the first forty lines of each
    /// file's hunks: latency scales with input size and a commit message does not need the whole diff.
    /// </summary>
    public int AiMaxDiffBytes { get; set; } = 12 * 1024;

    /// <summary>
    /// True to require Conventional Commits rather than leaving it to the model. Off by default: the
    /// prompt's own "when clearly appropriate" is the better answer for a mixed history.
    /// </summary>
    public bool AiConventionalCommits { get; set; }

    /// <summary>Newest first. Behind the tray's Recent menu, and the MRU rank the palette scores by.</summary>
    public List<string> RecentRepositories { get; set; } = [];

    [JsonIgnore]
    public static string DirectoryPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlickGit");

    [JsonIgnore]
    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    /// <summary>
    /// The user's own actions, in a separate file because it is the one they hand-edit and the one
    /// that can start arbitrary processes -- so a syntax error in a custom action cannot cost them
    /// every other setting they have.
    /// </summary>
    public static string ActionsFilePath => Path.Combine(DirectoryPath, "actions.json");

    /// <summary>
    /// Loads the file, or returns defaults when it does not exist or cannot be read. A corrupt
    /// settings file must not stop the tool from starting. The failure is reported through
    /// <paramref name="error"/> so the caller can surface it once rather than swallowing it.
    /// </summary>
    public static FlickSettings Load(out string? error)
    {
        error = null;

        try
        {
            if (!File.Exists(FilePath))
                return new FlickSettings();

            string json = File.ReadAllText(FilePath);
            FlickSettings? loaded = JsonSerializer.Deserialize(json, SettingsJson.Default.FlickSettings);

            if (loaded is null)
                return new FlickSettings();

            if (loaded.SchemaVersion > CurrentSchemaVersion)
            {
                //Refused, not migrated. This build does not know what a newer file means, and guessing would
                //drop whatever it holds on the next save.
                error =
                    $"settings.json was written by a newer version of FlickGit " +
                    $"(schema {loaded.SchemaVersion}, this build understands {CurrentSchemaVersion}).\n\n" +
                    $"Defaults are in use and the file has not been modified:\n{FilePath}";

                return new FlickSettings();
            }

            return loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            error = $"settings.json could not be read, so defaults are in use:\n\n{ex.Message}";
            return new FlickSettings();
        }
    }

    /// <summary>
    /// Saves atomically: temp file in the same directory, then replace. The one write pattern that
    /// cannot leave a half-file on disk.
    /// </summary>
    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);

        SchemaVersion = CurrentSchemaVersion;

        string json = JsonSerializer.Serialize(this, SettingsJson.Default.FlickSettings);
        string temporary = FilePath + ".tmp";

        File.WriteAllText(temporary, json);

        if (File.Exists(FilePath))
            File.Replace(temporary, FilePath, destinationBackupFileName: null);
        else
            File.Move(temporary, FilePath);
    }
}

/// <summary>Source-generated, so no reflection-based serializer is pulled in at startup.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FlickSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
