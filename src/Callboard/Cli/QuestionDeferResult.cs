using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>question defer</c> command's success result (§9 block D).
/// </summary>
internal sealed class QuestionDeferResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("deferredTarget")]
    public required string DeferredTarget { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.QuestionDeferResult);
}
