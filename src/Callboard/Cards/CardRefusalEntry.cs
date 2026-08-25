namespace Callboard.Cards;

/// <summary>
/// One entry in a card's append-only refusal history (process-enforcement: "Refusals are explained
/// and attributable" — "A refusal SHALL be recorded against the card with the acting role and the
/// time, so that a pattern of refusals is itself visible", §9 block A). Modelled the same
/// self-contained, no-body, no-footer shape as <see cref="CardBlockTransitionEntry"/> and its
/// siblings — a refusal carries no prose beyond its own <see cref="Rule"/>/<see cref="Remedy"/>
/// text, only <c>by</c>/<c>rule</c>/<c>remedy</c>/<c>timestamp</c> fields — and, like every sibling
/// sequence on <see cref="CardFile"/>, it is its own append-only list rather than folded into
/// <see cref="CardComment"/>: a refusal is not addressed narrative that routes to a role's queue,
/// it is a fact about an attempt that did not succeed.
///
/// <para>
/// <b>Only a card-addressed refusal is ever recorded (Architect ruling, §9 base post).</b> A
/// refusal that never resolved a card that exists, parses, and anchors under its own scope has
/// nothing to record against — no repo root, no card at the path, a layout mismatch, or an
/// unparseable file — and is reported to the caller without a write. See
/// <see cref="ICardRefusalReason"/> for the interface a refusal-shaped outcome case implements to
/// supply <see cref="Rule"/> and <see cref="Remedy"/> here, and each outcome-specific recording
/// helper on <see cref="CardStore"/> (e.g. <see cref="CardStore.
/// ApplyBlockTransitionUnderExistingLock"/>'s own) for where the write happens — always under the
/// same lock that read the card and decided to refuse, and never reported to the caller until this
/// line is durable (ADR-0001: enforcement unavailable is a tool-failure, not a quieter refusal).
/// </para>
/// </summary>
/// <param name="By">The role whose attempt was refused.</param>
/// <param name="Rule">The rule that refused the attempt (<see cref="ICardRefusalReason.
/// RefusingRule"/>) — free text naming the requirement, never a code.</param>
/// <param name="Remedy">What would satisfy the rule that refused (<see cref="ICardRefusalReason.
/// Remedy"/>) — free text, stated concretely enough to act on.</param>
/// <param name="Timestamp">When the refusal occurred.</param>
/// <param name="UnknownFields">Refusal-line fields this build's parser does not recognise,
/// captured verbatim (raw key, raw value) in the order they were read, and re-emitted the same way
/// on write — the same extensibility rule every sibling sequence on <see cref="CardFile"/> applies.
/// Empty for every entry this build itself constructs.</param>
internal sealed record CardRefusalEntry(
    CardOwner By,
    string Rule,
    string Remedy,
    DateTimeOffset Timestamp,
    IReadOnlyList<(string Key, string RawValue)> UnknownFields)
{
    // Same reason as CardBlockTransitionEntry's own override: the compiler-generated equality
    // compares UnknownFields by reference, which would make a freshly-constructed entry never
    // equal to the same entry after a parse round trip even when every field genuinely matches.
    public bool Equals(CardRefusalEntry? other) =>
        other is not null
        && By == other.By
        && Rule == other.Rule
        && Remedy == other.Remedy
        && Timestamp == other.Timestamp
        && UnknownFields.SequenceEqual(other.UnknownFields);

    public override int GetHashCode() =>
        HashCode.Combine(By, Rule, Remedy, Timestamp, UnknownFields.Count);
}
