using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>nit disposition</c> command's success result (§8 block B): the disposition recorded, the
/// block card it was recorded against, whether it also applied the <c>fix-before-land</c> flow edge
/// (<see cref="Cards.CardNitDispositionOutcome.Dispositioned.Transitioned"/> — see that type's own
/// doc comment for when it is and is not the case), and the raised card's id when this disposition
/// was <c>defer</c>/<c>decline</c>.
/// </summary>
internal sealed class NitDispositionResult : ICommandResult
{
    [JsonPropertyName("nitId")]
    public required string NitId { get; init; }

    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("disposition")]
    public required string Disposition { get; init; }

    [JsonPropertyName("transitioned")]
    public required bool Transitioned { get; init; }

    [JsonPropertyName("round")]
    public int? Round { get; init; }

    [JsonPropertyName("raisedCardId")]
    public string? RaisedCardId { get; init; }

    [JsonPropertyName("raisedCardFilePath")]
    public string? RaisedCardFilePath { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.NitDispositionResult);
}
