using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>rule promote</c> command's success result (§7 block E, register: "Promoting a
/// change-scoped rule to repository scope SHALL move the same card, retaining its identity, text
/// and thread"). Carries both the old and new file paths — the whole point of this verb is that the
/// card moved, so a caller needs to see where it moved <em>from</em> as well as where it landed.
/// <see cref="ActingRole"/> was previously absent — the one §7 result that reported no acting role
/// at all (§7 remediation, blocker 3), even though <see cref="Cards.CardStore.PromoteRule"/> now
/// records it on the card's own comment thread; this field reports the same value.
/// </summary>
internal sealed class RulePromoteResult : ICommandResult
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("oldFilePath")]
    public required string OldFilePath { get; init; }

    [JsonPropertyName("newFilePath")]
    public required string NewFilePath { get; init; }

    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("promotedAt")]
    public required DateTimeOffset PromotedAt { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.RulePromoteResult);
}
