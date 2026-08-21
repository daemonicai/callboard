using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>index rebuild</c> command's result: where the rebuilt database landed, how much it
/// indexed, and every card that failed to parse along the way. A rebuild that indexed some cards
/// and failed to parse others is still a <em>success</em> carrying <see cref="Failures"/> — never
/// a refusal — per record-retrieval's degraded-mode requirement: a corrupt card must not stop the
/// loop.
/// </summary>
internal sealed class IndexRebuildResult : ICommandResult
{
    [JsonPropertyName("databasePath")]
    public required string DatabasePath { get; init; }

    [JsonPropertyName("indexedCardCount")]
    public required int IndexedCardCount { get; init; }

    [JsonPropertyName("indexedCommentCount")]
    public required int IndexedCommentCount { get; init; }

    [JsonPropertyName("failures")]
    public required IReadOnlyList<IndexRebuildFailure> Failures { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.IndexRebuildResult);
}

/// <summary>One card file <see cref="IndexRebuildResult"/> could not parse: where it lives and why.</summary>
internal sealed class IndexRebuildFailure
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}
