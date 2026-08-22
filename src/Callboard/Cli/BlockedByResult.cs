using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>block add-blocker</c>/<c>block remove-blocker</c> commands' shared success result: the
/// resulting <c>blocked_by</c> set and <see cref="Blocked"/>, derived from whether that set is
/// non-empty (work-lifecycle: "Blocked is derived, not stored", §5 block D) — never a stored
/// status. <see cref="ActingRole"/> is the role <c>--role</c> named (§5 remediation, DEVLOG §5
/// finding B1) — both verbs required and validated that flag from their first version but, until
/// this fix, never surfaced it anywhere.
/// </summary>
internal sealed class BlockedByResult : ICommandResult
{
    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("blockedBy")]
    public required IReadOnlyList<string> BlockedBy { get; init; }

    [JsonPropertyName("blocked")]
    public required bool Blocked { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.BlockedByResult);
}
