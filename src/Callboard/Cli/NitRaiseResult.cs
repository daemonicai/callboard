using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>nit raise</c> command's success result (§8 block B): the nit's own generated id (a
/// caller needs this to later disposition it), the block card it was raised against, and who
/// raised it and when.
/// </summary>
internal sealed class NitRaiseResult : ICommandResult
{
    [JsonPropertyName("nitId")]
    public required string NitId { get; init; }

    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("blockId")]
    public required string BlockId { get; init; }

    [JsonPropertyName("required")]
    public required bool Required { get; init; }

    [JsonPropertyName("sites")]
    public required IReadOnlyList<string> Sites { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.NitRaiseResult);
}
