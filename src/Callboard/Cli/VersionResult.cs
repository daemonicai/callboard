using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>version</c> command's result — a trivial placeholder that proves the CLI's shape
/// end to end (argument parsing, dispatch, JSON envelope) with no card-kind vocabulary
/// implied. The verb vocabulary is decided per-section (design.md Open Question 1).
/// </summary>
internal sealed class VersionResult : ICommandResult
{
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.VersionResult);
}
