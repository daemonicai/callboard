using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>finding status</c> command's success result (§6 block C). <see cref="Staleness"/> is
/// always one of <c>"current"</c>, <c>"stale"</c>, <c>"not-measurable"</c> or <c>"not-applicable"</c>
/// — the four wire forms <see cref="Cards.FindingStalenessStatus"/> carries — and <see cref="
/// StalenessReason"/> is set whenever <see cref="Staleness"/> is not <c>"current"</c> (there is
/// nothing to explain about "current"; every other case names why). There is deliberately no field
/// here that could read as a verdict on whether the finding was <em>right</em> — see <see
/// cref="Cards.FindingStalenessStatus"/>'s own doc comment for why that is structural rather than an
/// omission this type happens to make.
/// </summary>
internal sealed class FindingStatusResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary><c>"measured"</c> or <c>"argued-clean"</c> — <see cref="Cards.FindingDisposition"/>'s
    /// two wire forms.</summary>
    [JsonPropertyName("disposition")]
    public required string Disposition { get; init; }

    [JsonPropertyName("verifiedAt")]
    public string? VerifiedAt { get; init; }

    /// <summary><c>"current"</c>, <c>"stale"</c>, <c>"not-measurable"</c> or
    /// <c>"not-applicable"</c> — <see cref="Cards.FindingStalenessStatus"/>'s four wire forms.</summary>
    [JsonPropertyName("staleness")]
    public required string Staleness { get; init; }

    [JsonPropertyName("stalenessReason")]
    public string? StalenessReason { get; init; }

    /// <summary><c>"live"</c>, <c>"degraded"</c> or <c>"unreadable"</c> — <see
    /// cref="Cards.FindingDegradationStatus"/>'s three wire forms (§6 block D, findings: "the
    /// finding is no longer offered as live"; §6 block D remediation for <c>"unreadable"</c> — a
    /// section card could not be ruled out but could not be read either, and that must not read
    /// identically to "no section card exists"). Derived from the section card's own
    /// <c>closed_at</c> every time this is read — never stored on the finding itself. Orthogonal to
    /// <see cref="Staleness"/>: a degraded finding still carries a staleness answer, and a stale
    /// finding is not necessarily degraded.</summary>
    [JsonPropertyName("degradation")]
    public required string Degradation { get; init; }

    [JsonPropertyName("degradationReason")]
    public string? DegradationReason { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.FindingStatusResult);
}
