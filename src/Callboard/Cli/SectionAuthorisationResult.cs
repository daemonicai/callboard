using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>section authorise</c> command's success result (§8a block C, work-lifecycle:
/// "Remediation beyond the second round requires recorded authorisation") — every field taken from
/// the entry actually appended, not from the parsed argv, the same discipline
/// <see cref="SectionVerdictResult"/> already applies.
/// </summary>
internal sealed class SectionAuthorisationResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.SectionAuthorisationResult);
}
