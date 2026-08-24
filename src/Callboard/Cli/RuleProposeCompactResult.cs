using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>rule propose-compact</c> command's success result (§7 block G, 7.9, register: "the
/// system records the proposal with its candidate text, backing set and citation counts, and
/// applies nothing until the Product Owner decides"). <see cref="Backing"/>/<see cref="
/// BackingFilePaths"/>/<see cref="CitationCounts"/> are three parallel lists, in the order
/// <c>--absorbs</c> named them — no card is written by this call, so there is nothing to read a
/// family's own <c>absorbs</c> field back from the way <see cref="RuleCompactResult.Absorbs"/>
/// does; this reports exactly what was proposed and what the record showed for it at request time,
/// nothing more.
/// </summary>
internal sealed class RuleProposeCompactResult : ICommandResult
{
    [JsonPropertyName("candidateText")]
    public required string CandidateText { get; init; }

    [JsonPropertyName("backing")]
    public required IReadOnlyList<string> Backing { get; init; }

    [JsonPropertyName("backingFilePaths")]
    public required IReadOnlyList<string> BackingFilePaths { get; init; }

    [JsonPropertyName("citationCounts")]
    public required IReadOnlyList<int> CitationCounts { get; init; }

    [JsonPropertyName("proposedBy")]
    public required string ProposedBy { get; init; }

    [JsonPropertyName("proposedAt")]
    public required DateTimeOffset ProposedAt { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.RuleProposeCompactResult);
}
