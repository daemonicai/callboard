using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>block approve</c> command's success result (§8 block A): the state certified, the claims
/// and limits this approval enumerated, who recorded it and when.
/// </summary>
internal sealed class BlockApproveResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("reviewedState")]
    public required string ReviewedState { get; init; }

    [JsonPropertyName("claims")]
    public required IReadOnlyList<BlockApprovalClaimResult> Claims { get; init; }

    [JsonPropertyName("limits")]
    public required IReadOnlyList<string> Limits { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("round")]
    public int? Round { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.BlockApproveResult);
}

/// <summary>One claim <see cref="BlockApproveResult"/> enumerated: its stable id and its
/// text.</summary>
internal sealed class BlockApprovalClaimResult
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }
}
