using System.Text.Json;
using System.Text.Json.Serialization;

namespace Callboard.Cli;

/// <summary>
/// <c>rule review</c>'s success result (§10 block E, register: "Register size triggers review,
/// never eviction"). <see cref="Ceiling"/>/<see cref="CeilingSource"/> together are what makes the
/// ceiling "stated" — the register's own wording — rather than merely applied: a caller reading
/// this response can always tell which number was checked and whether it came from <c>--ceiling</c>
/// or the built-in default. <see cref="CeilingPassed"/> is <see cref="Cards.RuleCitations.
/// CeilingPassed"/>'s own verdict, named here so a caller does not have to recompute
/// <c>liveRuleCount &gt; ceiling</c> itself; it is a trigger for a human review, never an action
/// this command takes. <see cref="UncitedOpenRules"/> is the human review queue, in the exact set
/// <see cref="Cards.RuleCitations.UncitedOpenRules"/> returns — this call retires, discharges and
/// mutates nothing, so the same rules remain live and open the moment after this response is
/// printed as the moment before.
/// </summary>
internal sealed class RuleReviewResult : ICommandResult
{
    [JsonPropertyName("ceiling")]
    public required int Ceiling { get; init; }

    /// <summary>Either <c>"flag"</c> (the caller passed <c>--ceiling</c>) or <c>"default"</c>
    /// (<see cref="Cli.CommandDispatcher.DefaultRuleReviewCeiling"/> applied).</summary>
    [JsonPropertyName("ceilingSource")]
    public required string CeilingSource { get; init; }

    [JsonPropertyName("liveRuleCount")]
    public required int LiveRuleCount { get; init; }

    [JsonPropertyName("ceilingPassed")]
    public required bool CeilingPassed { get; init; }

    [JsonPropertyName("uncitedOpenRules")]
    public required IReadOnlyList<RuleReviewUncitedRuleResult> UncitedOpenRules { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.RuleReviewResult);
}

/// <summary>One live, open <c>rule</c> card that no other card anywhere in the record currently
/// cites — queued for a human to rule on, never retired by this or any other automated path.
/// </summary>
internal sealed class RuleReviewUncitedRuleResult
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }
}
