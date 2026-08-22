using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>block gate</c> command's success result: the label and exit code recorded, and the
/// <see cref="Passed"/> flag derived directly from that exit code — nowhere else (§5 block D, work-
/// lifecycle: "A recorded exit code SHALL be the only accepted evidence that a gate passed").
/// </summary>
internal sealed class BlockGateResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("exitCode")]
    public required int ExitCode { get; init; }

    [JsonPropertyName("passed")]
    public required bool Passed { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.BlockGateResult);
}
