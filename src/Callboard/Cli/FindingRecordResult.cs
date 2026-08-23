using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>finding record</c> command's success result (§6 block B). <see cref="RaisedCardId"/>,
/// <see cref="RaisedCardFilePath"/> and <see cref="RaisedCardKind"/> are all <see langword="null"/>
/// together exactly when the finding declared <c>--blind-spot none</c>, and all set together when a
/// blind spot was raised — <see cref="Cards.CardStore.RecordFinding"/>'s own all-or-nothing write
/// guarantees the raised card exists whenever these are non-null.
/// </summary>
internal sealed class FindingRecordResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>The declaration actually recorded — <c>"none"</c> or <c>"raised-as"</c>, the two
    /// wire forms <see cref="Cards.FindingBlindSpotDeclaration"/> carries.</summary>
    [JsonPropertyName("blindSpot")]
    public required string BlindSpot { get; init; }

    [JsonPropertyName("raisedCardId")]
    public string? RaisedCardId { get; init; }

    [JsonPropertyName("raisedCardFilePath")]
    public string? RaisedCardFilePath { get; init; }

    [JsonPropertyName("raisedCardKind")]
    public string? RaisedCardKind { get; init; }

    /// <summary>The disposition actually recorded (§6 block C) — <c>"measured"</c> or
    /// <c>"argued-clean"</c>, the two wire forms <see cref="Cards.FindingDisposition"/>
    /// carries.</summary>
    [JsonPropertyName("disposition")]
    public required string Disposition { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.FindingRecordResult);
}
