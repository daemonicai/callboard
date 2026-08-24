using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>rule propose-compact</c> command's success result (§7 block G, 7.9, register: "the
/// system records the proposal with its candidate text, backing set and citation counts, and
/// applies nothing until the Product Owner decides"). <see cref="Backing"/>/<see cref="
/// BackingFilePaths"/>/<see cref="CitationCounts"/> are three parallel lists, in the order
/// <c>--absorbs</c> named them — this reports exactly what was proposed and what the record showed
/// for it at request time, nothing more. <see cref="ActingRole"/> was previously spelled
/// <c>ProposedBy</c> — renamed to the one spelling §7's result types settle on for the acting role
/// (§7 remediation, blocker 3); the value is unchanged.
///
/// <para>
/// <b><see cref="ProposalId"/>/<see cref="ProposalFilePath"/> (§7 remediation, blocker 1).</b> The
/// one card this call writes: a <c>question</c> card, owned by the Product Owner, recording
/// <see cref="CandidateText"/>, <see cref="Backing"/> and <see cref="CitationCounts"/> in its body
/// — durable, attributed, and routable to the Product Owner by the same ownership routing every
/// other card uses, applying nothing to any card in <see cref="BackingFilePaths"/>. Every test that
/// reaches success also proves the backing rules unchanged on the bytes — "records the proposal"
/// and "applies nothing" are both true at once, not traded off against each other.
/// </para>
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

    [JsonPropertyName("proposalId")]
    public required string ProposalId { get; init; }

    [JsonPropertyName("proposalFilePath")]
    public required string ProposalFilePath { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("proposedAt")]
    public required DateTimeOffset ProposedAt { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.RuleProposeCompactResult);
}
