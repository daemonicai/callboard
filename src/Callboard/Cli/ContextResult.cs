using System.Text.Json;
using System.Text.Json.Serialization;
using Callboard.Cards;

namespace Callboard.Cli;

/// <summary>
/// <c>context --role &lt;role&gt;</c>'s success result (§10 block A, working-context: "given a
/// role, the system SHALL return that role's complete working context, composed of exactly" four
/// parts) — <see cref="LiveRules"/>/<see cref="LiveHazards"/> (part 1), <see cref="QueueOrder"/>/
/// <see cref="Queue"/> (part 2), and <see cref="TopItem"/> (part 3), in that order, mirroring
/// <see cref="Cards.WorkingContext"/>'s own field order exactly so block B can insert cumulative
/// measurement between them without reshaping this type. Nothing else is on this type — part 4,
/// "nothing else", is enforced by there being no further property to add narrative to, not by a
/// convention a later change could quietly break.
/// </summary>
internal sealed class ContextResult : ICommandResult
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("liveRules")]
    public required IReadOnlyList<ContextRegisterCardResult> LiveRules { get; init; }

    [JsonPropertyName("liveHazards")]
    public required IReadOnlyList<ContextRegisterCardResult> LiveHazards { get; init; }

    /// <summary>The ordering rule <see cref="Queue"/> is built under, stated in prose
    /// (<see cref="WorkingContextAssembler.QueueOrderDescription"/>) — working-context: "in a
    /// stated order" means the response says what the rule is, not merely that one exists.</summary>
    [JsonPropertyName("queueOrder")]
    public required string QueueOrder { get; init; }

    [JsonPropertyName("queue")]
    public required IReadOnlyList<ContextQueueEntryResult> Queue { get; init; }

    /// <summary><see cref="Queue"/>'s first element, expanded in full — <see langword="null"/>
    /// exactly when <see cref="Queue"/> is empty.</summary>
    [JsonPropertyName("topItem")]
    public ContextTopItemResult? TopItem { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.ContextResult);
}

/// <summary>One live <c>rule</c> or <c>hazard</c> card, delivered whole (register: "The register
/// SHALL be delivered rather than made available for search").</summary>
internal sealed class ContextRegisterCardResult
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("body")]
    public required string Body { get; init; }
}

/// <summary>One card in <see cref="ContextResult.Queue"/> — identity only, never its narrative
/// (record-retrieval: "no narrative from cards outside its queue appears" — narrative on a queue
/// member other than <see cref="ContextResult.TopItem"/> is still narrative this response does not
/// carry).</summary>
internal sealed class ContextQueueEntryResult
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("owner")]
    public required string Owner { get; init; }

    [JsonPropertyName("updated")]
    public required DateTimeOffset Updated { get; init; }
}

/// <summary>The top queue item, in full (working-context: "its body, base, referenced tasks,
/// constraints, unresolved threads addressed to the caller, and the previous round's verdict where
/// one exists"). <see cref="Base"/> and <see cref="ReferencedTasks"/> are only ever non-empty/
/// non-null for a <c>block</c> card — every other kind carries neither, the same "kind-specific
/// field, empty elsewhere" convention <see cref="Cards.BlockCardFields"/> itself follows.
/// <see cref="Constraints"/> is not kind-restricted — it is a view of part 1 (<see cref="
/// ContextResult.LiveRules"/>/<see cref="ContextResult.LiveHazards"/>), applicable to any top item
/// kind (Product Owner ruling, §10 block A review).</summary>
internal sealed class ContextTopItemResult
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("owner")]
    public required string Owner { get; init; }

    [JsonPropertyName("body")]
    public required string Body { get; init; }

    /// <summary>The commit the block's brief was carved against — <see cref="Cards.
    /// BlockCardFields.Base"/>.</summary>
    [JsonPropertyName("base")]
    public string? Base { get; init; }

    /// <summary>The task references this block implements — <see cref="Cards.BlockCardFields.
    /// Tasks"/>.</summary>
    [JsonPropertyName("referencedTasks")]
    public required IReadOnlyList<string> ReferencedTasks { get; init; }

    /// <summary>The ordering-rule-style prose <see cref="Constraints"/> is computed under
    /// (<see cref="Cards.WorkingContextAssembler.ConstraintsRuleDescription"/>), stated the same
    /// way <see cref="ContextResult.QueueOrder"/> states part 2's ordering rule.</summary>
    [JsonPropertyName("constraintsRule")]
    public required string ConstraintsRule { get; init; }

    /// <summary>"Constraints" (Product Owner ruling, §10 block A review): the live rule/hazard
    /// cards whose scope covers this item — not <see cref="Cards.BlockCardFields.BlockedBy"/>,
    /// which is untouched on the model and does not appear here under any name. A card-scoped
    /// subset of part 1 (<see cref="ContextResult.LiveRules"/>/<see cref="ContextResult.
    /// LiveHazards"/>), reusing the same <see cref="ContextRegisterCardResult"/> shape since these
    /// are literally register cards, not a new representation of them.</summary>
    [JsonPropertyName("constraints")]
    public required IReadOnlyList<ContextRegisterCardResult> Constraints { get; init; }

    [JsonPropertyName("unresolvedThreadsAddressedToCaller")]
    public required IReadOnlyList<ContextThreadResult> UnresolvedThreadsAddressedToCaller { get; init; }

    /// <summary>The claims and limits certified at the previous round, or <see langword="null"/>
    /// where none exist — <see cref="Cards.WorkingContextAssembler"/>'s own doc comment for
    /// <c>PreviousRoundVerdict</c> states the reading this follows.</summary>
    [JsonPropertyName("previousRoundVerdict")]
    public ContextVerdictResult? PreviousRoundVerdict { get; init; }
}

/// <summary>One unresolved thread addressed to the role that requested this context — the comment
/// itself, in full, since this is the top item's own detail (part 3), not narrative from another
/// queue member.</summary>
internal sealed class ContextThreadResult
{
    [JsonPropertyName("commentId")]
    public required string CommentId { get; init; }

    [JsonPropertyName("author")]
    public required string Author { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("body")]
    public required string Body { get; init; }
}

/// <summary>A block card's certification record from one round — <see cref="Cards.
/// CardApprovalClaim"/>/<see cref="Cards.CardApprovalLimit"/>, grouped by the round they share.
/// </summary>
internal sealed class ContextVerdictResult
{
    [JsonPropertyName("round")]
    public required int Round { get; init; }

    [JsonPropertyName("claims")]
    public required IReadOnlyList<string> Claims { get; init; }

    [JsonPropertyName("limits")]
    public required IReadOnlyList<string> Limits { get; init; }
}
