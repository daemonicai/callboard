using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// <c>comment add</c>'s success result (§13, card-model: "The verbs that dispose of a thread SHALL
/// NOT be the only ones that can start one"). <see cref="CommentId"/> is the minted comment's own
/// identity (Architect ruling item 5) — document-local (§11 ruling 2: a comment id is document-
/// local, so withholding it withholds the only handle), and the one field a caller needs to resolve
/// this thread later with <c>comment resolve</c>/<c>promote</c>/<c>decline</c>.
/// </summary>
internal sealed class CommentAddResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("cardId")]
    public required string CardId { get; init; }

    [JsonPropertyName("commentId")]
    public required string CommentId { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    /// <summary>The role this comment is addressed to, or <see langword="null"/> when it addresses
    /// no one in particular (Architect ruling item 1: an unaddressed comment is a note on the
    /// record, legitimate on its own).</summary>
    [JsonPropertyName("to")]
    public string? To { get; init; }

    /// <summary>The comment this one replies to, or <see langword="null"/> when it does not reply
    /// to anything.</summary>
    [JsonPropertyName("replyTo")]
    public string? ReplyTo { get; init; }

    [JsonPropertyName("addedAt")]
    public required DateTimeOffset AddedAt { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.CommentAddResult);
}
