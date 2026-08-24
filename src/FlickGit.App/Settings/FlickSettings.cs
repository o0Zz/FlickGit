using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlickGit.App.Trigger;

namespace FlickGit.App.Settings;

/// <summary>
/// User settings, persisted to <c>%LOCALAPPDATA%\FlickGit\settings.json</c>.
///
/// Phase 1 carries only the settings Phase 1 has surfaces for. The two structural rules
/// from CLAUDE.md, "Persistence" are in place from the start because retrofitting either
/// one is a breaking change:
///
/// <list type="bullet">
/// <item><description><b><see cref="SchemaVersion"/> is written and checked.</b> A file
/// from a future version is refused with a clear message rather than silently migrated
/// downward — downgrading a settings file loses whatever the newer version
/// added.</description></item>
/// <item><description><b>API keys are never in this file.</b> Windows Credential Manager
/// or DPAPI only. There is no property here to put one in, which is the point.</description></item>
/// </list>
/// </summary>
public sealed class FlickSettings
{
    /// <summary>
    /// Bumped when a property is removed or its meaning changes. Adding one does not need a bump.
    ///
    /// 2 dropped <c>allowUpstreamCreation</c>, a dictionary keyed by repository path. The answer it
    /// held is a fact about a repository, so it moved into that repository's own config as
    /// <c>flickgit.allowUpstreamCreation</c> — where it cannot go stale when the repository is moved
    /// and can be seen and reset from the repository window. Per CLAUDE.md's Hard Requirement 1 the
    /// key is deleted rather than migrated: every repository asks once more, and then never again.
    ///
    /// 3 dropped <c>aiAllowDiffsToLeaveMachine</c> and <c>aiDiffConsentShown</c>. A provider that is
    /// named and has a key stored for it is already the consent: the only thing an AI provider does
    /// here is write a commit message, and the only way to write one is from the diff. A second
    /// switch in front of that gated the feature on something the user had already said, and — since
    /// it also gated the question meant to ask it — could only ever be answered in Settings.
    /// </summary>
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Override for git.exe. Empty means "search PATH, then the standard locations".</summary>
    public string GitPath { get; set; } = string.Empty;

    /// <summary>Empty means resolve per repository: remote HEAD, then main, then master.</summary>
    public string PrimaryBranch { get; set; } = string.Empty;

    /// <summary>Default on. CLAUDE.md: "This is the one case where the fast path deserves friction."</summary>
    public bool WarnWhenCommittingToPrimaryBranch { get; set; } = true;

    public bool CloseCommitWindowAfterSuccess { get; set; } = true;

    public bool ShowSuccessNotification { get; set; } = true;

    /// <summary>Interface language as a two-letter code. Empty follows Windows.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Diff pane font. Monospace or the alignment between the panes is meaningless.</summary>
    public string DiffFontFamily { get; set; } = "Cascadia Mono, Consolas, Courier New";

    public double DiffFontSize { get; set; } = 12.5;

    /// <summary>Writes the verbose Git timings to the log. Off by default — it is noisy.</summary>
    public bool VerboseLogging { get; set; }

    /// <summary>
    /// Which input opens the commit window.
    ///
    /// A global hotkey, or nothing. CLAUDE.md's two Explorer-scoped input hooks are not built yet,
    /// and there is no value here for them until they are: a setting that silently falls back to
    /// something else is worse than one that does not exist.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<TriggerKind>))]
    public TriggerKind Trigger { get; set; } = TriggerKind.Hotkey;

    /// <summary>
    /// The global hotkey, as "Ctrl+Alt+G". At least one modifier is required: a bare key claimed
    /// globally would be taken away from every application on the machine.
    /// </summary>
    public string HotkeyGesture { get; set; } = "Ctrl+Alt+G";

    /// <summary>
    /// The global hotkey that opens the repository palette.
    ///
    /// <b>Not Ctrl+Alt+G.</b> CLAUDE.md names that combination twice — once for the commit trigger
    /// and once here — and two <c>RegisterHotKey</c> calls for one combination cannot both succeed;
    /// the second fails with ERROR_HOTKEY_ALREADY_REGISTERED. The trigger keeps it, because it is the
    /// product's named feature and the section specifying it is the one that argues the choice
    /// through. The palette gets Ctrl+Alt+R, for repositories.
    ///
    /// Configurable for the same reason the trigger's is: the combination may already belong to
    /// something else on this machine, and that is not a thing a constant can fix.
    /// </summary>
    public string PaletteHotkeyGesture { get; set; } = "Ctrl+Alt+R";

    /// <summary>
    /// Where to look for repositories to show in the palette, beyond the ones already used.
    ///
    /// Empty by default, deliberately. Guessing a folder would either miss the user's repositories
    /// or walk a directory tree nobody asked to have walked; and the palette is not empty without
    /// this, because the most-recently-used list fills it as soon as the tool has been used once.
    /// The palette says so, and names this file, when it has nothing else to show.
    /// </summary>
    public List<string> PaletteScanRoots { get; set; } = [];

    /// <summary>
    /// Which service writes the commit message: <c>anthropic</c>, <c>openai</c>, <c>copilot</c>,
    /// <c>ollama</c> or <c>disabled</c>.
    ///
    /// <b>Naming one, with a key stored for it, is the consent.</b> There is nothing else a
    /// configured provider could be for: every message it writes is written from a diff.
    /// <c>ollama</c> needs no key and asks for no consent, because nothing it is sent leaves the
    /// machine.
    /// </summary>
    public string AiProvider { get; set; } = "anthropic";

    /// <summary>
    /// Empty means the provider's default — Haiku 4.5 for Anthropic.
    ///
    /// <b>Except for Ollama, which has no default and requires this.</b> Which models exist there is
    /// a fact about the user's own disk, so any guess would 404 for most people; `ollama list` says
    /// what to put here.
    /// </summary>
    public string AiModel { get; set; } = string.Empty;

    /// <summary>
    /// Where Ollama is listening.
    ///
    /// A setting rather than a constant because running the model on a bigger machine on the same
    /// network is the ordinary reason to use Ollama at all — and because with it hard-coded to
    /// loopback there would be no way to express that. Note that pointing it off this machine is
    /// exactly that: the diff then leaves this computer, even though it does not leave the network.
    /// </summary>
    public string AiOllamaUrl { get; set; } = FlickGit.Ai.AiOptions.DefaultOllamaUrl;

    /// <summary>OpenAI only. <c>none</c> is the latency baseline; <c>low</c> is the next step up.</summary>
    public string AiReasoningEffort { get; set; } = "none";

    /// <summary>
    /// CLAUDE.md's "Max diff size", defaulted low. Above it the payload becomes a file summary plus
    /// the first forty lines of each file's hunks, because latency scales with input size and a
    /// commit message does not need the whole diff.
    /// </summary>
    public int AiMaxDiffBytes { get; set; } = 12 * 1024;

    /// <summary>
    /// True to require Conventional Commits rather than leaving it to the model. Off by default:
    /// the prompt's own "when clearly appropriate" is the better answer for a mixed history.
    /// </summary>
    public bool AiConventionalCommits { get; set; }

    /// <summary>
    /// Most-recently-used repository roots, newest first. Behind the tray's Recent menu, and the MRU
    /// rank Phase 5's palette scores by.
    /// </summary>
    public List<string> RecentRepositories { get; set; } = [];

    [JsonIgnore]
    public static string DirectoryPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FlickGit");

    [JsonIgnore]
    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    /// <summary>
    /// The user's own actions, alongside the settings. CLAUDE.md, "Persistence".
    ///
    /// A separate file rather than a section of this one, because it is the one the user hand-edits
    /// and the one that can start arbitrary processes. Keeping them apart means a syntax error in a
    /// custom action cannot cost the user every other setting they have.
    /// </summary>
    public static string ActionsFilePath => Path.Combine(DirectoryPath, "actions.json");

    /// <summary>
    /// Loads the file, or returns defaults when it does not exist or cannot be read.
    ///
    /// A corrupt settings file must not stop the tool from starting: the user's real
    /// problem is that they want to commit, and defaults let them. The failure is reported
    /// through <paramref name="error"/> so the caller can surface it once rather than
    /// swallowing it.
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
                //Refused, not migrated. This build does not know what a newer file means, and
                //guessing would drop whatever it holds on the next save.
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
    /// Saves atomically: write a temp file in the same directory, then replace.
    ///
    /// A settings file truncated by a crash mid-write is worse than one that is a version
    /// behind, and this is the one write pattern that cannot leave a half-file on disk.
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

/// <summary>
/// Source-generated JSON, so no reflection-based serializer is pulled in at startup and
/// the same code compiles unchanged if this ever needs to be read from the AOT stub.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FlickSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
