using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// <c>view --out &lt;path&gt;</c>'s success result (§12 block B, record-retrieval: "a local,
/// read-only, human-readable view of the board"). Reports the path written to, never the
/// document itself — the same "the rendered artefact is a file, not a field" shape <see
/// cref="SectionExportResult"/>/<see cref="ChangeExportResult"/> already use, for the same
/// reason: <see cref="CliEnvelope"/> is the one JSON line every machine-facing command emits.
/// </summary>
internal sealed class ViewResult : ICommandResult
{
    [JsonPropertyName("outputPath")]
    public required string OutputPath { get; init; }

    [JsonPropertyName("cardCount")]
    public required int CardCount { get; init; }

    /// <summary>Every card file this read walked and could not parse, with the parser's own
    /// reason (§13.5) — empty when the whole record parsed. The same set the rendered document itself lists, so the JSON caller and the human reading the board are told the same thing. Reported, never refused: one corrupt
    /// card narrows this answer, it does not halt the query (record-retrieval, "Damage is
    /// contained").</summary>
    [JsonPropertyName("unreadable")]
    public required IReadOnlyList<UnreadableCardResult> Unreadable { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.ViewResult);
}
