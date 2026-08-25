using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>section status</c> command's success result (§5 block E, work-lifecycle: "the system
/// answers from the section entity without requiring its cards to be read") — every field except
/// <see cref="AgeingThreads"/> is read from the one section card <see cref="CommandDispatcher.
/// RunSectionStatus"/> opened, nothing else.
/// <see cref="VerdictCount"/> is a count, not the verdicts themselves — the full verdict history
/// (each entry's range and acting role) is retrieved by reading the card directly, the same
/// budget-bounded-summary convention record-retrieval already applies elsewhere; this command
/// answers "what is this section's status", not "give me its whole history".
///
/// <para>
/// <b><see cref="AgeingThreads"/> is the one field that reaches past the section's own card</b> (§9
/// block E, architect ruling on 9.6's ageing-thread prompt): process-enforcement's own words —
/// "to keep this gate from becoming a formality discharged in bulk at the moment of closing" —
/// name a close-time failure a close-time surfacing cannot prevent, so the prompt belongs to a
/// surface read during the section's life instead. <c>section status</c> is that surface. It scans
/// the section's own blocks (never itself, which carries no round for a thread on it to age
/// against) via <see cref="Callboard.Cards.CardStore.FindAgeingAddressedThreads"/> — read-only, no
/// lock — and reports nothing here that <see cref="Callboard.Cards.CardSectionCloseOutcome.
/// UnresolvedAddressedThread"/> would not also refuse on if a close were attempted right now; this
/// is a nudge to deal with it before that happens, not a second source of truth for whether it will.
/// </para>
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

    /// <summary>process-enforcement's ageing-thread prompt (§9 block E) — every live addressed
    /// thread, on any block this section owns, that has survived at least one round boundary on
    /// its own block. Never refuses anything; empty when nothing has aged past its own round.
    /// </summary>
    [JsonPropertyName("ageingThreads")]
    public required IReadOnlyList<AgeingThreadResult> AgeingThreads { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.SectionStatusResult);
}

/// <summary>One ageing thread <see cref="SectionStatusResult"/> surfaces — see <see cref="Callboard.
/// Cards.AgeingThread"/>, the domain type this mirrors.</summary>
internal sealed class AgeingThreadResult
{
    [JsonPropertyName("blockId")]
    public required string BlockId { get; init; }

    [JsonPropertyName("blockFilePath")]
    public required string BlockFilePath { get; init; }

    [JsonPropertyName("threadId")]
    public required string ThreadId { get; init; }

    [JsonPropertyName("addressedTo")]
    public required string AddressedTo { get; init; }
}
