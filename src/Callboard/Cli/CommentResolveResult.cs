using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>comment resolve</c> and <c>comment decline</c> commands' shared success result (§9
/// remediation, round two — S4). Both verbs end at the same appended-comment write (<see
/// cref="Cards.CardStore.ResolveComment"/>), differing only in whether a reason is mandatory —
/// there is nothing in the response itself that needs to say which verb produced it beyond what the
/// envelope's own <c>command</c> field already reports.
/// </summary>
internal sealed class CommentResolveResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("cardId")]
    public required string CardId { get; init; }

    [JsonPropertyName("commentId")]
    public required string CommentId { get; init; }

    [JsonPropertyName("resolvingCommentId")]
    public required string ResolvingCommentId { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("resolvedAt")]
    public required DateTimeOffset ResolvedAt { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.CommentResolveResult);
}
