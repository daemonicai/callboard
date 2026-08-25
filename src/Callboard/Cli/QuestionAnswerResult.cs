using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>question answer</c> command's success result (§9 block D).
/// </summary>
internal sealed class QuestionAnswerResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("decisionId")]
    public string? DecisionId { get; init; }

    [JsonPropertyName("inlineAnswer")]
    public string? InlineAnswer { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.QuestionAnswerResult);
}
