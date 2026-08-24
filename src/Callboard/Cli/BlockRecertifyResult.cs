using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>block recertify</c> command's result (§8 block C, review-certification: "Recertification
/// re-asserts an existing claim set"): which of the current approval's claims were asserted and
/// which were refused, whether the refusal moved the block back to <c>briefed</c>
/// (<see cref="Transitioned"/>), and the <c>reviewed_state</c> the card now carries — the amended
/// state when every claim was asserted, or the previously certified one, left untouched, when any
/// claim was refused (<see cref="Cards.CardRecertificationOutcome.ClaimsRefused"/>'s own doc
/// comment).
/// </summary>
internal sealed class BlockRecertifyResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("reviewedState")]
    public required string ReviewedState { get; init; }

    [JsonPropertyName("assertedClaimIds")]
    public required IReadOnlyList<string> AssertedClaimIds { get; init; }

    [JsonPropertyName("refusedClaimIds")]
    public required IReadOnlyList<string> RefusedClaimIds { get; init; }

    [JsonPropertyName("transitioned")]
    public required bool Transitioned { get; init; }

    [JsonPropertyName("round")]
    public int? Round { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The unenforceable-by-construction obligation review-certification places on the
    /// human recording this call — "the reviewer SHALL re-derive each claim against the code" —
    /// surfaced in the response text where the reviewer reads it, since nothing on this type or
    /// <see cref="Cards.CardRecertificationOutcome"/> can verify it happened (Architect ruling, §8
    /// block C brief item 7: no flag or field here implies otherwise).</summary>
    [JsonPropertyName("notice")]
    public required string Notice { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.BlockRecertifyResult);
}
