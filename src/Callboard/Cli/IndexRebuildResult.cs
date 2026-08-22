using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>index rebuild</c> command's result: where the rebuilt database landed, how much it
/// indexed, and every card that failed to parse along the way. A rebuild that indexed some cards
/// and failed to parse others is still a <em>success</em> carrying <see cref="Failures"/> — never
/// a refusal — per record-retrieval's degraded-mode requirement: a corrupt card must not stop the
/// loop.
/// </summary>
internal sealed class IndexRebuildResult : ICommandResult
{
    [JsonPropertyName("databasePath")]
    public required string DatabasePath { get; init; }

    [JsonPropertyName("indexedCardCount")]
    public required int IndexedCardCount { get; init; }

    [JsonPropertyName("indexedCommentCount")]
    public required int IndexedCommentCount { get; init; }

    [JsonPropertyName("failures")]
    public required IReadOnlyList<IndexRebuildFailure> Failures { get; init; }

    /// <summary>
    /// Every kind whose committed identity counter (4.2) has fallen behind the highest identity
    /// number this rebuild actually observed on disk for that kind. Reported, never a refusal —
    /// the same category as <see cref="Failures"/>, and not a member of §9's closed refusal set.
    /// </summary>
    [JsonPropertyName("identityCounterViolations")]
    public required IReadOnlyList<IndexRebuildIdentityCounterViolation> IdentityCounterViolations { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.IndexRebuildResult);
}

/// <summary>One card file <see cref="IndexRebuildResult"/> could not parse: where it lives and why.</summary>
internal sealed class IndexRebuildFailure
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}

/// <summary>
/// One kind's committed identity counter reported behind the highest identity number observed on
/// disk for that kind — see <see cref="Callboard.Cards.CardIdentityAllocator.VerifyCounters"/>.
/// </summary>
internal sealed class IndexRebuildIdentityCounterViolation
{
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("counterValue")]
    public required int CounterValue { get; init; }

    [JsonPropertyName("observedMaxId")]
    public required int ObservedMaxId { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}
