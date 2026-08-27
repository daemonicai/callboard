using System.Text.Json;
using System.Text.Json.Serialization;
using Callboard.Cards;

namespace Callboard.Cli;

/// <summary>
/// <c>card show &lt;id&gt;</c>'s success result (§11 block B, record-retrieval: "the system SHALL
/// return a card's full content, including every comment on it, given the card's identity"). Every
/// group <see cref="Cards.CardFile"/> itself carries is mirrored here one for one — frontmatter,
/// body, handovers, the five kind-specific field groups, transitions, claims, limits, refusals and
/// the complete comment thread — rather than a per-kind result type, so <see cref="CliJsonContext"/>
/// registers exactly one entry for this verb, not nine. A group that does not apply to this card's
/// <see cref="Cards.CardKind"/> is present and empty (<see cref="Cards.BlockCardFields.Empty"/> and
/// its four siblings' own convention), never omitted — the same "one shape, all groups present"
/// idiom <see cref="Cards.CardFile"/> itself follows.
///
/// <para>
/// <b>Never truncated (§11 block B brief).</b> The working-context character budget
/// (<see cref="ContextBudgetResult"/>) is a requirement of <c>context</c>'s response specifically —
/// this is the quotable path that budget exists to route a caller to, so it carries no budget field
/// and no truncation of its own.
/// </para>
/// </summary>
internal sealed class CardShowResult : ICommandResult
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("filePath")]
    public required string FilePath { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("owner")]
    public required string Owner { get; init; }

    [JsonPropertyName("scope")]
    public required string Scope { get; init; }

    [JsonPropertyName("section")]
    public required string Section { get; init; }

    [JsonPropertyName("created")]
    public required DateTimeOffset Created { get; init; }

    [JsonPropertyName("updated")]
    public required DateTimeOffset Updated { get; init; }

    [JsonPropertyName("unknownFrontmatterFields")]
    public required IReadOnlyList<CardShowUnknownFieldResult> UnknownFrontmatterFields { get; init; }

    [JsonPropertyName("body")]
    public required string Body { get; init; }

    [JsonPropertyName("handovers")]
    public required IReadOnlyList<CardShowHandoverResult> Handovers { get; init; }

    [JsonPropertyName("blockFields")]
    public required CardShowBlockFieldsResult BlockFields { get; init; }

    [JsonPropertyName("transitions")]
    public required IReadOnlyList<CardShowTransitionResult> Transitions { get; init; }

    [JsonPropertyName("claims")]
    public required IReadOnlyList<CardShowApprovalClaimResult> Claims { get; init; }

    [JsonPropertyName("limits")]
    public required IReadOnlyList<CardShowApprovalLimitResult> Limits { get; init; }

    [JsonPropertyName("sectionFields")]
    public required CardShowSectionFieldsResult SectionFields { get; init; }

    [JsonPropertyName("findingFields")]
    public required CardShowFindingFieldsResult FindingFields { get; init; }

    [JsonPropertyName("registerFields")]
    public required CardShowRegisterFieldsResult RegisterFields { get; init; }

    [JsonPropertyName("questionFields")]
    public required CardShowQuestionFieldsResult QuestionFields { get; init; }

    [JsonPropertyName("refusals")]
    public required IReadOnlyList<CardShowRefusalResult> Refusals { get; init; }

    [JsonPropertyName("comments")]
    public required IReadOnlyList<CardShowCommentResult> Comments { get; init; }

    public JsonElement ToJsonElement() => JsonSerializer.SerializeToElement(this, CliJsonContext.Default.CardShowResult);
}

/// <summary>One frontmatter, comment-header, handover-line, transition-line or entry-line field
/// this build's parser does not recognise — <see cref="Cards.CardFile.UnknownFrontmatterFields"/>
/// and its per-group siblings, carried verbatim.</summary>
internal sealed class CardShowUnknownFieldResult
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("rawValue")]
    public required string RawValue { get; init; }
}

/// <summary>One entry in the card's complete, append-only thread (<see cref="Cards.CardComment"/>) —
/// every field, including nit metadata and unknown headers, none held back for budget the way
/// <see cref="ContextThreadResult"/> may.</summary>
internal sealed class CardShowCommentResult
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("author")]
    public required string Author { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("body")]
    public required string Body { get; init; }

    [JsonPropertyName("replyTo")]
    public string? ReplyTo { get; init; }

    [JsonPropertyName("to")]
    public string? To { get; init; }

    [JsonPropertyName("resolves")]
    public string? Resolves { get; init; }

    [JsonPropertyName("isNit")]
    public required bool IsNit { get; init; }

    [JsonPropertyName("required")]
    public required bool Required { get; init; }

    [JsonPropertyName("sites")]
    public required IReadOnlyList<string> Sites { get; init; }

    [JsonPropertyName("disposition")]
    public string? Disposition { get; init; }

    [JsonPropertyName("unknownHeaderFields")]
    public required IReadOnlyList<CardShowUnknownFieldResult> UnknownHeaderFields { get; init; }
}

/// <summary>One entry in the card's append-only ownership-handover sequence (<see cref="Cards.
/// CardHandover"/>).</summary>
internal sealed class CardShowHandoverResult
{
    [JsonPropertyName("by")]
    public required string By { get; init; }

    [JsonPropertyName("to")]
    public required string To { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("unknownFields")]
    public required IReadOnlyList<CardShowUnknownFieldResult> UnknownFields { get; init; }
}

/// <summary>The five <c>block</c>-only fields (<see cref="Cards.BlockCardFields"/>) — present and
/// at their <see cref="Cards.BlockCardFields.Empty"/> defaults for every other kind, the same
/// convention that type's own doc comment states.</summary>
internal sealed class CardShowBlockFieldsResult
{
    [JsonPropertyName("base")]
    public string? Base { get; init; }

    [JsonPropertyName("reviewedState")]
    public string? ReviewedState { get; init; }

    [JsonPropertyName("tasks")]
    public required IReadOnlyList<string> Tasks { get; init; }

    [JsonPropertyName("round")]
    public int? Round { get; init; }

    [JsonPropertyName("blockedBy")]
    public required IReadOnlyList<string> BlockedBy { get; init; }

    [JsonPropertyName("gateResults")]
    public required IReadOnlyList<CardShowGateResultResult> GateResults { get; init; }

    [JsonPropertyName("findingKey")]
    public string? FindingKey { get; init; }
}

/// <summary>One recorded gate result (<see cref="Cards.GateResult"/>).</summary>
internal sealed class CardShowGateResultResult
{
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("exitCode")]
    public required int ExitCode { get; init; }

    [JsonPropertyName("round")]
    public required int Round { get; init; }
}

/// <summary>One entry in a <c>block</c> card's append-only flow-transition history (<see cref="
/// Cards.CardBlockTransitionEntry"/>).</summary>
internal sealed class CardShowTransitionResult
{
    [JsonPropertyName("by")]
    public required string By { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("from")]
    public required string From { get; init; }

    [JsonPropertyName("to")]
    public required string To { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("unknownFields")]
    public required IReadOnlyList<CardShowUnknownFieldResult> UnknownFields { get; init; }
}

/// <summary>One enumerated approval claim (<see cref="Cards.CardApprovalClaim"/>).</summary>
internal sealed class CardShowApprovalClaimResult
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("round")]
    public required int Round { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("unknownFields")]
    public required IReadOnlyList<CardShowUnknownFieldResult> UnknownFields { get; init; }
}

/// <summary>One stated approval limit (<see cref="Cards.CardApprovalLimit"/>).</summary>
internal sealed class CardShowApprovalLimitResult
{
    [JsonPropertyName("round")]
    public required int Round { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("unknownFields")]
    public required IReadOnlyList<CardShowUnknownFieldResult> UnknownFields { get; init; }
}

/// <summary>The three <c>section</c>-only fields plus their two append-only sequences (<see
/// cref="Cards.SectionCardFields"/>) — present and at their <see cref="Cards.SectionCardFields.
/// Empty"/> defaults for every other kind.</summary>
internal sealed class CardShowSectionFieldsResult
{
    [JsonPropertyName("base")]
    public string? Base { get; init; }

    [JsonPropertyName("closedBy")]
    public string? ClosedBy { get; init; }

    [JsonPropertyName("closedAt")]
    public DateTimeOffset? ClosedAt { get; init; }

    [JsonPropertyName("verdicts")]
    public required IReadOnlyList<CardShowSectionVerdictResult> Verdicts { get; init; }

    [JsonPropertyName("authorisations")]
    public required IReadOnlyList<CardShowSectionAuthorisationResult> Authorisations { get; init; }
}

/// <summary>One recorded supervisor verdict (<see cref="Cards.SectionVerdictEntry"/>).</summary>
internal sealed class CardShowSectionVerdictResult
{
    [JsonPropertyName("by")]
    public required string By { get; init; }

    [JsonPropertyName("verdict")]
    public required string Verdict { get; init; }

    [JsonPropertyName("rangeFrom")]
    public required string RangeFrom { get; init; }

    [JsonPropertyName("rangeTo")]
    public required string RangeTo { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("unknownFields")]
    public required IReadOnlyList<CardShowUnknownFieldResult> UnknownFields { get; init; }
}

/// <summary>One recorded Product Owner authorisation (<see cref="Cards.SectionAuthorisationEntry"/>).
/// </summary>
internal sealed class CardShowSectionAuthorisationResult
{
    [JsonPropertyName("by")]
    public required string By { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("unknownFields")]
    public required IReadOnlyList<CardShowUnknownFieldResult> UnknownFields { get; init; }
}

/// <summary>The four <c>finding</c>-only fields (<see cref="Cards.FindingCardFields"/>) — present
/// and at their <see cref="Cards.FindingCardFields.Empty"/> defaults for every other kind.
/// <see cref="Cards.FindingExtent"/> and <see cref="Cards.FindingBlindSpotDeclaration"/> are closed
/// unions with no wire-string extension of their own, so each is flattened here to a
/// <c>*Kind</c> discriminator plus the one payload field that kind carries, empty otherwise — the
/// same "one shape, all cases present, empty where inapplicable" idiom the rest of this type
/// follows.</summary>
internal sealed class CardShowFindingFieldsResult
{
    [JsonPropertyName("instrument")]
    public string? Instrument { get; init; }

    [JsonPropertyName("extentKind")]
    public required string ExtentKind { get; init; }

    [JsonPropertyName("extentInstrument")]
    public string? ExtentInstrument { get; init; }

    [JsonPropertyName("extentItems")]
    public required IReadOnlyList<string> ExtentItems { get; init; }

    [JsonPropertyName("verifiedAt")]
    public string? VerifiedAt { get; init; }

    [JsonPropertyName("blindSpotKind")]
    public required string BlindSpotKind { get; init; }

    [JsonPropertyName("blindSpotRaisedAsId")]
    public string? BlindSpotRaisedAsId { get; init; }

    [JsonPropertyName("extentFingerprintFiles")]
    public IReadOnlyList<CardShowFingerprintFileResult>? ExtentFingerprintFiles { get; init; }

    [JsonPropertyName("disposition")]
    public required string Disposition { get; init; }
}

/// <summary>One fingerprinted file's content state (<see cref="Cards.
/// FindingExtentFileFingerprint"/>).</summary>
internal sealed class CardShowFingerprintFileResult
{
    [JsonPropertyName("relativePath")]
    public required string RelativePath { get; init; }

    [JsonPropertyName("contentHash")]
    public string? ContentHash { get; init; }
}

/// <summary>The ten register-only fields shared by <c>rule</c>, <c>hazard</c>, <c>obligation</c>
/// and <c>decision</c> cards (<see cref="Cards.RegisterCardFields"/>) — present and at their
/// <see cref="Cards.RegisterCardFields.Empty"/> defaults for every other kind.</summary>
internal sealed class CardShowRegisterFieldsResult
{
    [JsonPropertyName("condition")]
    public string? Condition { get; init; }

    [JsonPropertyName("cadence")]
    public string? Cadence { get; init; }

    [JsonPropertyName("dischargedBy")]
    public string? DischargedBy { get; init; }

    [JsonPropertyName("dischargedAt")]
    public DateTimeOffset? DischargedAt { get; init; }

    [JsonPropertyName("owedBy")]
    public string? OwedBy { get; init; }

    [JsonPropertyName("declinedReason")]
    public string? DeclinedReason { get; init; }

    [JsonPropertyName("supersedes")]
    public string? Supersedes { get; init; }

    [JsonPropertyName("supersededBy")]
    public string? SupersededBy { get; init; }

    [JsonPropertyName("earnedFrom")]
    public required IReadOnlyList<string> EarnedFrom { get; init; }

    [JsonPropertyName("absorbs")]
    public required IReadOnlyList<string> Absorbs { get; init; }
}

/// <summary>The seven <c>question</c>-only fields (<see cref="Cards.QuestionCardFields"/>) —
/// present and at their <see cref="Cards.QuestionCardFields.Empty"/> defaults for every other
/// kind.</summary>
internal sealed class CardShowQuestionFieldsResult
{
    [JsonPropertyName("answeredBy")]
    public string? AnsweredBy { get; init; }

    [JsonPropertyName("answeredAt")]
    public DateTimeOffset? AnsweredAt { get; init; }

    [JsonPropertyName("answerDecisionId")]
    public string? AnswerDecisionId { get; init; }

    [JsonPropertyName("answerInline")]
    public string? AnswerInline { get; init; }

    [JsonPropertyName("deferredBy")]
    public string? DeferredBy { get; init; }

    [JsonPropertyName("deferredAt")]
    public DateTimeOffset? DeferredAt { get; init; }

    [JsonPropertyName("deferredTarget")]
    public string? DeferredTarget { get; init; }
}

/// <summary>One entry in the card's append-only refusal history (<see cref="Cards.
/// CardRefusalEntry"/>).</summary>
internal sealed class CardShowRefusalResult
{
    [JsonPropertyName("by")]
    public required string By { get; init; }

    [JsonPropertyName("rule")]
    public required string Rule { get; init; }

    [JsonPropertyName("remedy")]
    public required string Remedy { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("unknownFields")]
    public required IReadOnlyList<CardShowUnknownFieldResult> UnknownFields { get; init; }
}
