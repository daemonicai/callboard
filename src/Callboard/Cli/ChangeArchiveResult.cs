using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// The <c>change archive</c> command's success result (§7 block D, register: "the register lives
/// above the change"). <see cref="CompactedFamilyId"/>/<see cref="CompactedRuleIds"/> report §7
/// block F's archive-time compaction hook — both <see langword="null"/> for the ordinary case (a
/// change with nothing to compact, or none requested), both set when <c>--compact-family</c>/
/// <c>--absorbs</c> were given. <see cref="ActingRole"/> was previously spelled <c>ArchivedBy</c> —
/// renamed to the one spelling §7's result types settle on for the acting role (§7 remediation,
/// blocker 3); the value is unchanged. <c>settledObligationIds</c> is gone (§9 block F): archiving
/// no longer discharges anything of its own — an open change-scoped obligation either carries into
/// the archive untouched (owed by a section still open) or refuses the whole call
/// (<see cref="CommandOutcome.Refusal"/> from <see cref="Cards.ChangeArchiveOutcome.
/// OrphanedObligations"/>), so a successful response never has any settling to report.
/// </summary>
internal sealed class ChangeArchiveResult : ICommandResult
{
    [JsonPropertyName("changeName")]
    public required string ChangeName { get; init; }

    [JsonPropertyName("archivedDirectory")]
    public required string ArchivedDirectory { get; init; }

    [JsonPropertyName("compactedFamilyId")]
    public string? CompactedFamilyId { get; init; }

    [JsonPropertyName("compactedRuleIds")]
    public IReadOnlyList<string>? CompactedRuleIds { get; init; }

    [JsonPropertyName("actingRole")]
    public required string ActingRole { get; init; }

    [JsonPropertyName("archivedAt")]
    public required DateTimeOffset ArchivedAt { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.ChangeArchiveResult);
}
