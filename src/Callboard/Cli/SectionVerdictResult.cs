using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>section verdict</c> command's success result (§5 block E, work-lifecycle: "the verdict,
/// the range and the acting role are recorded against that section entity") — every field taken
/// from the entry actually appended, not from the parsed argv, the same discipline
/// <see cref="BlockGateResult"/> already applies.
/// </summary>
internal sealed class SectionVerdictResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("verdict")]
    public required string Verdict { get; init; }

    [JsonPropertyName("rangeFrom")]
    public required string RangeFrom { get; init; }

    [JsonPropertyName("rangeTo")]
    public required string RangeTo { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.SectionVerdictResult);
}
