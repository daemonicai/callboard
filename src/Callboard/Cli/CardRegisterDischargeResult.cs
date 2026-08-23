using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>rule|hazard|obligation|decision discharge</c> command's success result (§7 block A,
/// register: "Register kinds have a two-state lifecycle") — the acting role and time actually
/// recorded on the card, the same "recorded, not merely echoed from argv" discipline
/// <see cref="SectionCloseResult"/> already follows for a section's own close.
/// </summary>
internal sealed class CardRegisterDischargeResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("dischargedBy")]
    public required string DischargedBy { get; init; }

    [JsonPropertyName("dischargedAt")]
    public required DateTimeOffset DischargedAt { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.CardRegisterDischargeResult);
}
