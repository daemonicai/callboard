using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>comment promote</c> command's success result (§9 remediation, round two — S4). Same
/// "recorded, not merely echoed from argv" shape as <see cref="FindingRecordResult"/>'s own raised-
/// card fields — the promoted card's own id/path/kind are read back off <see cref="Cards.
/// CardCommentPromoteOutcome.Promoted.RaisedCard"/>, never re-derived from what the caller typed.
/// </summary>
internal sealed class CommentPromoteResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("cardId")]
    public required string CardId { get; init; }

    [JsonPropertyName("commentId")]
    public required string CommentId { get; init; }

    [JsonPropertyName("raisedCardId")]
    public required string RaisedCardId { get; init; }

    [JsonPropertyName("raisedCardFilePath")]
    public required string RaisedCardFilePath { get; init; }

    [JsonPropertyName("raisedCardKind")]
    public required string RaisedCardKind { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("promotedAt")]
    public required DateTimeOffset PromotedAt { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.CommentPromoteResult);
}
