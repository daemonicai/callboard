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

    /// <summary>The stated character budget this response was measured against, and what — if
    /// anything — was shortened to fit it (§10 block B; working-context: "the budget SHALL be a
    /// requirement of the response and not a target it may exceed" / "truncation is never
    /// silent"). Always present, even when nothing was truncated — the budget is stated
    /// unconditionally, not only when it binds.</summary>
    [JsonPropertyName("budget")]
    public required ContextBudgetResult Budget { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.ContextResult);
}

/// <summary>
/// The character budget a <see cref="ContextResult"/> was measured against (D6, §10 block B) —
/// the constants and prose from <see cref="WorkingContextBudget"/>, plus what this particular
/// response actually measured and truncated. <see cref="ExceededCeiling"/> is the one case the
/// spec accepts the response failing its own budget: register and brief may never shorten, so
/// when they alone exceed <see cref="CharacterCeiling"/>, the only honest move left is to drop
/// every narrative comment body and say so (working-context: "the budget SHALL be a requirement
/// ... it SHALL state explicitly that it has truncated and what").
/// </summary>
internal sealed class ContextBudgetResult
{
    [JsonPropertyName("tokenBudget")]
    public required int TokenBudget { get; init; }

    [JsonPropertyName("charactersPerToken")]
    public required double CharactersPerToken { get; init; }

    [JsonPropertyName("marginFraction")]
    public required double MarginFraction { get; init; }

    [JsonPropertyName("characterCeiling")]
    public required int CharacterCeiling { get; init; }

    /// <summary>The measured character length of this response as actually emitted (with
    /// whatever narrative truncation below already applied) — the same
    /// <see cref="System.Text.Json.JsonSerializer"/> encoding the caller receives, not an
    /// approximation of it.</summary>
    [JsonPropertyName("characterCount")]
    public required int CharacterCount { get; init; }

    [JsonPropertyName("statement")]
    public required string Statement { get; init; }

    /// <summary><see langword="true"/> exactly when at least one comment body was shortened
    /// (dropped) to fit the ceiling — see <see cref="OmittedNarrativeCommentIds"/> for which.
    /// </summary>
    [JsonPropertyName("truncated")]
    public required bool Truncated { get; init; }

    /// <summary>States what was truncated, in prose, when <see cref="Truncated"/> is
    /// <see langword="true"/> — working-context: "truncation is never silent".</summary>
    [JsonPropertyName("truncationStatement")]
    public string? TruncationStatement { get; init; }

    /// <summary>The ids of every unresolved-thread comment whose body was dropped for budget.
    /// Each still appears in <see cref="ContextTopItemResult.UnresolvedThreadsAddressedToCaller"/>
    /// with its structural fields intact (id, author, timestamp) and <see cref="
    /// ContextThreadResult.Truncated"/> set — only the body text is withheld.</summary>
    [JsonPropertyName("omittedNarrativeCommentIds")]
    public required IReadOnlyList<string> OmittedNarrativeCommentIds { get; init; }

    /// <summary><see langword="true"/> when the register and brief alone — before any narrative
    /// is even considered — already exceed <see cref="CharacterCeiling"/>. The only case where
    /// this response cannot satisfy its own stated budget: neither may shorten, so the response
    /// is delivered whole anyway, over budget, with <see cref="OverageStatement"/> saying by how
    /// much. A signal the register needs size compaction, not a defect in this measurement.
    /// </summary>
    [JsonPropertyName("exceededCeiling")]
    public required bool ExceededCeiling { get; init; }

    /// <summary>States the overage, in characters, when <see cref="ExceededCeiling"/> is
    /// <see langword="true"/>.</summary>
    [JsonPropertyName("overageStatement")]
    public string? OverageStatement { get; init; }
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

/// <summary>One unresolved thread addressed to the role that requested this context — the
/// comment itself, since this is the top item's own detail (part 3), not narrative from another
/// queue member. <see cref="CommentId"/>, <see cref="Author"/> and <see cref="Timestamp"/> are
/// structural routing facts (§10 block B: "a thread's structural facts ... are routing, not
/// narrative, and are kept") and are always present, even when <see cref="Truncated"/> is
/// <see langword="true"/> — a caller must still know a thread exists and is theirs even when its
/// text was shortened for budget.</summary>
internal sealed class ContextThreadResult
{
    [JsonPropertyName("commentId")]
    public required string CommentId { get; init; }

    [JsonPropertyName("author")]
    public required string Author { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The comment's text — the narrative this response can shorten for budget.
    /// <see langword="null"/> exactly when <see cref="Truncated"/> is <see langword="true"/>;
    /// never empty otherwise (record-retrieval guarantees no comment is ever empty-bodied).
    /// </summary>
    [JsonPropertyName("body")]
    public string? Body { get; init; }

    /// <summary><see langword="true"/> when <see cref="Body"/> was dropped to fit the response's
    /// character budget (<see cref="ContextBudgetResult"/>) — the structural fields above still
    /// identify the thread; only its text is withheld.</summary>
    [JsonPropertyName("truncated")]
    public required bool Truncated { get; init; }
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
