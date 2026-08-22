using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>section status</c> command's success result (§5 block E, work-lifecycle: "the system
/// answers from the section entity without requiring its cards to be read") — every field read
/// from the one section card <see cref="CommandDispatcher.RunSectionStatus"/> opened, nothing else.
/// <see cref="VerdictCount"/> is a count, not the verdicts themselves — the full verdict history
/// (each entry's range and acting role) is retrieved by reading the card directly, the same
/// budget-bounded-summary convention record-retrieval already applies elsewhere; this command
/// answers "what is this section's status", not "give me its whole history".
/// </summary>
internal sealed class SectionStatusResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("base")]
    public string? Base { get; init; }

    [JsonPropertyName("closedBy")]
    public string? ClosedBy { get; init; }

    [JsonPropertyName("closedAt")]
    public DateTimeOffset? ClosedAt { get; init; }

    [JsonPropertyName("verdictCount")]
    public required int VerdictCount { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.SectionStatusResult);
}
