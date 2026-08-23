using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>decision supersede</c> command's success result (§7 block C, register: "A decision MAY
/// name the decision it supersedes and the decision that supersedes it") — both halves of the
/// two-card write, so a caller can see the successor and the discharged predecessor in one
/// envelope rather than having to separately re-resolve either id.
/// </summary>
internal sealed class DecisionSupersedeResult : ICommandResult
{
    [JsonPropertyName("supersedingId")]
    public required string SupersedingId { get; init; }

    [JsonPropertyName("supersedingFilePath")]
    public required string SupersedingFilePath { get; init; }

    [JsonPropertyName("supersededId")]
    public required string SupersededId { get; init; }

    [JsonPropertyName("supersededFilePath")]
    public required string SupersededFilePath { get; init; }

    [JsonPropertyName("dischargedBy")]
    public required string DischargedBy { get; init; }

    [JsonPropertyName("dischargedAt")]
    public required DateTimeOffset DischargedAt { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.DecisionSupersedeResult);
}
