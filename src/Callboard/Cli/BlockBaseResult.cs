using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>block base</c> command's success result (§13, work-lifecycle: "Blocks carry their brief
/// context") — the commit recorded, and the role that recorded it (§5 remediation, DEVLOG §5
/// finding B1's own lesson: an acting role required and validated at the door should be surfaced
/// in the response, not silently dropped).
/// </summary>
internal sealed class BlockBaseResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("base")]
    public required string Base { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.BlockBaseResult);
}
