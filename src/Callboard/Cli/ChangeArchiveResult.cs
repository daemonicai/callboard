using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>change archive</c> command's success result (§7 block D, register: "the register lives
/// above the change"). <see cref="SettledObligationIds"/> is the acted-on list — the ids of every
/// change-scoped obligation actually discharged by this call, not a re-derived count — the same
/// "record what was actually done" discipline <see cref="CardRegisterDischargeResult"/> already
/// follows for a single card's discharge. <see cref="CompactedFamilyId"/>/<see cref="
/// CompactedRuleIds"/> report §7 block F's archive-time compaction hook — both <see langword="null"/>
/// for the ordinary case (a change with nothing to compact, or none requested), both set when
/// <c>--compact-family</c>/<c>--absorbs</c> were given.
/// </summary>
internal sealed class ChangeArchiveResult : ICommandResult
{
    [JsonPropertyName("changeName")]
    public required string ChangeName { get; init; }

    [JsonPropertyName("archivedDirectory")]
    public required string ArchivedDirectory { get; init; }

    [JsonPropertyName("settledObligationIds")]
    public required IReadOnlyList<string> SettledObligationIds { get; init; }

    [JsonPropertyName("compactedFamilyId")]
    public string? CompactedFamilyId { get; init; }

    [JsonPropertyName("compactedRuleIds")]
    public IReadOnlyList<string>? CompactedRuleIds { get; init; }

    [JsonPropertyName("archivedBy")]
    public required string ArchivedBy { get; init; }

    [JsonPropertyName("archivedAt")]
    public required DateTimeOffset ArchivedAt { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.ChangeArchiveResult);
}
