using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// <c>section export &lt;section-id&gt; --out &lt;path&gt;</c>'s success result (§11 block C).
/// Reports the path written to, never the document itself — <see cref="CliEnvelope"/> is "the one
/// JSON contract every machine-facing command emits on stdout: a single line", and the incumbent
/// measured 2.07 MB for one change (design.md D6), so the rendered document is a file, not a field.
/// </summary>
internal sealed class SectionExportResult : ICommandResult
{
    [JsonPropertyName("sectionId")]
    public required string SectionId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("outputPath")]
    public required string OutputPath { get; init; }

    [JsonPropertyName("cardCount")]
    public required int CardCount { get; init; }

    /// <summary>Every card file this read walked and could not parse, with the parser's own
    /// reason (§13.5) — empty when the whole record parsed. Reported, never refused: one corrupt
    /// card narrows this answer, it does not halt the query (record-retrieval, "Damage is
    /// contained").</summary>
    [JsonPropertyName("unreadable")]
    public required IReadOnlyList<UnreadableCardResult> Unreadable { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.SectionExportResult);
}
