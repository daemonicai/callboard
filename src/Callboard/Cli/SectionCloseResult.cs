using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>section close</c> command's success result (§5 block E, work-lifecycle: "closing it
/// SHALL record the acting role and the time") — the acting role and time actually recorded on the
/// card, not merely echoed from argv, plus (§8a block A) every block the close landed.
/// </summary>
internal sealed class SectionCloseResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("closedBy")]
    public required string ClosedBy { get; init; }

    [JsonPropertyName("closedAt")]
    public required DateTimeOffset ClosedAt { get; init; }

    /// <summary>Every block this close moved onto <c>landed</c>, plus every block that was already
    /// <c>landed</c> when the close began — see <see cref="Callboard.Cards.CardSectionCloseOutcome.
    /// Closed.LandedBlocks"/>'s own doc comment for why the two are not distinguished here.</summary>
    [JsonPropertyName("landedBlockIds")]
    public required IReadOnlyList<string> LandedBlockIds { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.SectionCloseResult);
}
