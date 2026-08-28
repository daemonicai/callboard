using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// <c>change export &lt;change-name&gt; --out &lt;path&gt;</c>'s success result (§11 block C) —
/// <see cref="SectionExportResult"/>'s whole-change sibling. Same "report the path, not the
/// document" shape and the same reason.
/// </summary>
internal sealed class ChangeExportResult : ICommandResult
{
    [JsonPropertyName("changeName")]
    public required string ChangeName { get; init; }

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

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.ChangeExportResult);
}
