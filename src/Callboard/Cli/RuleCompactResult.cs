using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>rule compact</c> command's success result (§7 block F, register: "A family rule SHALL
/// record the rules it absorbs, and every absorbed rule SHALL remain retrievable"). <see cref="
/// Absorbs"/> is read back off the written family card, not re-derived from <see cref="
/// AbsorbedFilePaths"/> — the same "report what was actually written" discipline <see cref="
/// DecisionSupersedeResult"/> already follows.
/// </summary>
internal sealed class RuleCompactResult : ICommandResult
{
    [JsonPropertyName("familyId")]
    public required string FamilyId { get; init; }

    [JsonPropertyName("familyFilePath")]
    public required string FamilyFilePath { get; init; }

    [JsonPropertyName("absorbs")]
    public required IReadOnlyList<string> Absorbs { get; init; }

    [JsonPropertyName("absorbedFilePaths")]
    public required IReadOnlyList<string> AbsorbedFilePaths { get; init; }

    [JsonPropertyName("dischargedBy")]
    public required string DischargedBy { get; init; }

    [JsonPropertyName("compactedAt")]
    public required DateTimeOffset CompactedAt { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.RuleCompactResult);
}
