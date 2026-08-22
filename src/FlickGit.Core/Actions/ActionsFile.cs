using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlickGit.Actions;

/// <summary>
/// The on-disk shape of <c>actions.json</c>.
///
/// Source-generated, and not optional: <c>FlickGit.Core</c> sets <c>IsAotCompatible</c>, so a
/// reflection-based <c>JsonSerializer</c> call is an IL2026/IL3050 warning and this repository treats
/// warnings as errors.
/// </summary>
/// <param name="Id">
/// Required and unique. A user action's id is conventionally prefixed <c>custom.</c>, but that is a
/// convention rather than a rule — what matters is that it does not collide with a built-in, and a
/// collision is refused rather than silently overriding one.
/// </param>
/// <param name="Label">Shown verbatim. User text, so it is not localised and must not be.</param>
/// <param name="Icon">A file name inside the install's <c>icons\</c> directory.</param>
/// <param name="Run">What it does. See <see cref="ActionRunDto"/>.</param>
/// <param name="Surfaces">Any of <c>menu</c>, <c>palette</c>. Absent or unrecognised means both.</param>
/// <param name="RequiresRepo">Whether the folder has to be inside a working tree.</param>
/// <param name="ShowOutput"><c>toast</c>, <c>window</c> or <c>none</c>.</param>
public sealed record ActionDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("icon")] string? Icon,
    [property: JsonPropertyName("run")] ActionRunDto? Run,
    [property: JsonPropertyName("surfaces")] string[]? Surfaces,
    [property: JsonPropertyName("requiresRepo")] bool RequiresRepo,
    [property: JsonPropertyName("requiresConfirmation")] bool RequiresConfirmation,
    [property: JsonPropertyName("menuOrder")] int MenuOrder,
    [property: JsonPropertyName("inMore")] bool InMore,
    [property: JsonPropertyName("showOutput")] string? ShowOutput);

/// <param name="Type"><c>git</c>, <c>process</c>, <c>window</c> or <c>composite</c>.</param>
/// <param name="Args">The argument list. Never a command string — see <see cref="ActionRun"/>.</param>
/// <param name="File">The executable, for <c>process</c>.</param>
/// <param name="Verb">The FlickGit verb, for <c>window</c>.</param>
/// <param name="Steps">The ordered sequence, for <c>composite</c>.</param>
public sealed record ActionRunDto(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("args")] string[]? Args,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("verb")] string? Verb,
    [property: JsonPropertyName("steps")] ActionRunDto[]? Steps);

/// <summary>
/// How the user has changed a built-in. Hidden or reordered, never deleted — CLAUDE.md.
/// </summary>
public sealed record BuiltInOverrideDto(
    [property: JsonPropertyName("hidden")] bool Hidden,
    [property: JsonPropertyName("menuOrder")] int? MenuOrder,
    [property: JsonPropertyName("inMore")] bool? InMore,
    [property: JsonPropertyName("label")] string? Label);

/// <param name="SchemaVersion">
/// Checked, not migrated. An unknown future version is refused with a clear message rather than
/// silently read as something it is not — Hard Requirement 1: "Bump <c>schemaVersion</c> and let an
/// old file be refused. Do not write a migration."
/// </param>
public sealed record ActionsFileDto(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("actions")] ActionDto[]? Actions,
    [property: JsonPropertyName("builtIns")] Dictionary<string, BuiltInOverrideDto>? BuiltIns);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ActionsFileDto))]
internal sealed partial class ActionsJson : JsonSerializerContext;
