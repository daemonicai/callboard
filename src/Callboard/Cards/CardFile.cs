namespace Callboard.Cards;

/// <summary>
/// A card as read from, or destined for, one file: frontmatter, a Markdown body, its append-only
/// comment thread, and its append-only ownership-handover sequence, in order. This is the
/// in-memory shape <see cref="CardFileParser"/> produces and <see cref="CardFileWriter"/> consumes —
/// the ADR-0003 record, not yet touching how it reaches disk (atomic rename and locking are
/// 2.5–2.6, block B).
/// </summary>
/// <param name="UnknownFrontmatterFields">
/// Frontmatter lines this build's parser does not recognise, captured verbatim (raw key, raw —
/// still frontmatter-escaped — value) in the order they were read, and re-emitted the same way
/// (after the known fields, before the closing fence) on write. This is the extensibility rule §2
/// owns for the format: a read-modify-write cycle (<c>AppendComment</c>) must never silently
/// destroy a field it does not itself understand — a §5/§6 field on a card written by a newer
/// build, or a line a human hand-added, survives an unrelated append instead of vanishing on the
/// next write. Preserving rather than refusing is the better fit for degraded mode (ADR-0003:
/// "legible without the tool" pairs with a record humans are expected to hand-edit). Always empty
/// for a card this build itself constructs from scratch.
/// </param>
/// <param name="Handovers">
/// The card's append-only ownership-handover history (card-model 4.5), one <see cref="CardHandover"/>
/// per <see cref="CardStore.TransferOwnership"/> call, oldest first — its own sequence, not part of
/// <paramref name="Comments"/> (see <see cref="CardHandover"/>'s own doc comment for why). The
/// constructor parameter accepts <see langword="null"/> only so a pre-existing four-argument
/// construction of this type still compiles (a default value must be a compile-time constant, which
/// an empty collection expression is not); the property itself, declared below to override the
/// positional auto-property, is never null — <see langword="null"/> normalises to empty once, here,
/// rather than every reader having to guard against it.
/// </param>
/// <param name="BlockFields">
/// The five §5 fields (<c>base</c>, <c>reviewed_state</c>, <c>tasks</c>, <c>round</c>,
/// <c>blocked_by</c>) known only on a <c>block</c> card — see <see cref="BlockCardFields"/>'s own
/// doc comment. <see cref="CardFileParser"/> only ever populates this with non-empty content when
/// <see cref="CardFrontmatter.Kind"/> is <see cref="CardKind.Block"/>; for every other kind it is
/// <see cref="BlockCardFields.Empty"/>, and any of the five keys found on such a card's frontmatter
/// land on <see cref="UnknownFrontmatterFields"/> instead, exactly as an unrecognised key would.
/// The constructor parameter accepts <see langword="null"/> for the same reason
/// <paramref name="Handovers"/>'s does — see that parameter's doc comment.
/// </param>
/// <param name="Transitions">
/// The block card's append-only flow-transition history (§5 block C, work-lifecycle: "Every
/// transition SHALL record the acting role and the time it occurred"), one
/// <see cref="CardBlockTransitionEntry"/> per applied <see cref="CardStore.ApplyBlockTransition"/>
/// call, oldest first — its own sequence, for the same reason <paramref name="Handovers"/> is its
/// own sequence rather than folded into <paramref name="Comments"/>. Never non-empty for a card
/// whose <see cref="CardFrontmatter.Kind"/> is not <see cref="CardKind.Block"/>. The constructor
/// parameter accepts <see langword="null"/> for the same reason <paramref name="Handovers"/>'s
/// does.
/// </param>
/// <param name="SectionFields">
/// The three §5 block E fields (<c>base</c>, <c>closed_by</c>, <c>closed_at</c>) known only on a
/// <c>section</c> card — see <see cref="Cards.SectionCardFields"/>'s own doc comment.
/// <see cref="CardFileParser"/> only ever populates this with non-empty content when
/// <see cref="CardFrontmatter.Kind"/> is <see cref="CardKind.Section"/>; for every other kind it is
/// <see cref="Cards.SectionCardFields.Empty"/>, exactly the same convention <paramref name="BlockFields"/>
/// already applies. The constructor parameter accepts <see langword="null"/> for the same reason
/// <paramref name="Handovers"/>'s does.
/// </param>
/// <param name="FindingFields">
/// The four §6 fields (<c>instrument</c>, <c>extent</c>, <c>verified_at</c>, <c>blind_spot</c>)
/// known only on a <c>finding</c> card — see <see cref="Cards.FindingCardFields"/>'s own doc
/// comment. <see cref="CardFileParser"/> only ever populates this with non-default content when
/// <see cref="CardFrontmatter.Kind"/> is <see cref="CardKind.Finding"/>; for every other kind it is
/// <see cref="Cards.FindingCardFields.Empty"/>, exactly the same convention <paramref name="BlockFields"/>
/// and <paramref name="SectionFields"/> already apply. The constructor parameter accepts
/// <see langword="null"/> for the same reason <paramref name="Handovers"/>'s does.
/// </param>
/// <param name="RegisterFields">
/// The §7 block A fields (<c>condition</c>, <c>cadence</c>, <c>discharged_by</c>,
/// <c>discharged_at</c>) known only on a <c>rule</c>, <c>hazard</c>, <c>obligation</c> or
/// <c>decision</c> card — see <see cref="Cards.RegisterCardFields"/>'s own doc comment.
/// <see cref="CardFileParser"/> only ever populates this with non-default content when
/// <see cref="CardFrontmatter.Kind"/> is one of those four, exactly the same convention
/// <paramref name="FindingFields"/> already applies. The constructor parameter accepts
/// <see langword="null"/> for the same reason <paramref name="Handovers"/>'s does.
/// </param>
/// <param name="Claims">
/// A <c>block</c> card's append-only approval-claim sequence (review-certification: "Certification
/// enumerates its claims", §8 block A) — one <see cref="CardApprovalClaim"/> per claim an
/// approval enumerated, oldest first, for the same reason <paramref name="Transitions"/> is its own
/// sequence rather than a scalar. Never non-empty for a card whose <see cref="CardFrontmatter.Kind"/>
/// is not <see cref="CardKind.Block"/>. The constructor parameter accepts <see langword="null"/> for
/// the same reason <paramref name="Handovers"/>'s does.
/// </param>
/// <param name="Limits">
/// A <c>block</c> card's append-only approval-limit sequence — one <see cref="CardApprovalLimit"/>
/// per limit an approval stated, oldest first, the same convention <paramref name="Claims"/> uses.
/// The constructor parameter accepts <see langword="null"/> for the same reason
/// <paramref name="Handovers"/>'s does.
/// </param>
/// <param name="Refusals">
/// The card's append-only refusal history (process-enforcement: "A refusal SHALL be recorded
/// against the card with the acting role and the time", §9 block A) — one
/// <see cref="CardRefusalEntry"/> per refusal-shaped outcome that resolved this card, oldest
/// first, for the same reason <paramref name="Transitions"/> is its own sequence rather than a
/// scalar. Not limited to a <c>block</c> card the way <paramref name="Transitions"/> is: any kind
/// of card can be the target of a refused attempt. The constructor parameter accepts
/// <see langword="null"/> for the same reason <paramref name="Handovers"/>'s does.
/// </param>
internal sealed record CardFile(
    CardFrontmatter Frontmatter,
    string Body,
    IReadOnlyList<CardComment> Comments,
    IReadOnlyList<(string Key, string RawValue)> UnknownFrontmatterFields,
    IReadOnlyList<CardHandover>? Handovers = null,
    BlockCardFields? BlockFields = null,
    IReadOnlyList<CardBlockTransitionEntry>? Transitions = null,
    SectionCardFields? SectionFields = null,
    FindingCardFields? FindingFields = null,
    RegisterCardFields? RegisterFields = null,
    IReadOnlyList<CardApprovalClaim>? Claims = null,
    IReadOnlyList<CardApprovalLimit>? Limits = null,
    IReadOnlyList<CardRefusalEntry>? Refusals = null)
{
    public IReadOnlyList<CardHandover> Handovers { get; init; } = Handovers ?? [];

    public BlockCardFields BlockFields { get; init; } = BlockFields ?? BlockCardFields.Empty;

    public IReadOnlyList<CardBlockTransitionEntry> Transitions { get; init; } = Transitions ?? [];

    public SectionCardFields SectionFields { get; init; } = SectionFields ?? Cards.SectionCardFields.Empty;

    public FindingCardFields FindingFields { get; init; } = FindingFields ?? Cards.FindingCardFields.Empty;

    public RegisterCardFields RegisterFields { get; init; } = RegisterFields ?? Cards.RegisterCardFields.Empty;

    public IReadOnlyList<CardApprovalClaim> Claims { get; init; } = Claims ?? [];

    public IReadOnlyList<CardApprovalLimit> Limits { get; init; } = Limits ?? [];

    public IReadOnlyList<CardRefusalEntry> Refusals { get; init; } = Refusals ?? [];
}
