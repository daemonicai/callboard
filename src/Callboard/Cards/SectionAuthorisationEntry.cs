namespace Callboard.Cards;

/// <summary>
/// One entry in a <c>section</c> card's append-only Product-Owner-authorisation history
/// (work-lifecycle: "Remediation beyond the second round requires recorded authorisation" — "The
/// authorisation SHALL be part of the record, not a permission granted out of band", §8a block C).
/// Modelled the same way as <see cref="SectionVerdictEntry"/> — its sibling append-only sequence on
/// the same card, same reasoning: a section that fails to converge more than once needs more than
/// one authorisation, each with its own reason, so this cannot be a pair of overwritable scalars.
///
/// <para>
/// <b>Recorded on the section card, not a separate <c>decision</c> card — the block's own weighed
/// choice (§8a block C DEVLOG post).</b> The scenario text is "the authorisation and its reason are
/// readable <em>from the section</em>" — a single-entity read, the same guarantee
/// <see cref="SectionCardFields"/>'s own doc comment already gives verdicts and closure. A
/// <c>decision</c> card scoped to the section would need a link back (the section naming the
/// decision, or the decision naming the section and a reader walking the directory to find it) —
/// exactly the two-card-consistency shape this section's supersession work already had to guard
/// with a rollback-capable multi-card write. An authorisation has no symmetric counterpart to keep
/// in step the way a superseding decision's <c>supersedes</c>/<c>superseded_by</c> pair does, so
/// there is nothing here that write needs to coordinate — a single-card append is both the simpler
/// mechanism and the one the scenario text already names.
/// </para>
///
/// <para>
/// <b>Spent-ness is derived, never stored (work-lifecycle: same discipline as the verdict count
/// itself).</b> No field on this type or <see cref="SectionCardFields"/> ever says "this
/// authorisation has been spent" — <see cref="CardStore.RecordSectionVerdictUnderExistingLock"/>
/// computes it at the moment a third-or-later <c>request-changes</c> verdict is attempted, from
/// nothing but the section's own retained <see cref="SectionCardFields.Verdicts"/> and
/// <see cref="SectionCardFields.Authorisations"/> counts: authorisations are consumed in the order
/// recorded, one per over-the-bound <c>request-changes</c> verdict, in the order those verdicts
/// were themselves recorded — see that method's own doc comment for the derivation. Recording more
/// authorisations than have yet been needed is legal (a Product Owner may authorise ahead of the
/// round that will spend it) and costs nothing to represent, because nothing here is a boolean flag
/// that could drift from what the record actually shows.
/// </para>
/// </summary>
/// <param name="By">The role that recorded this authorisation. <see cref="CardStore.
/// RecordSectionAuthorisationUnderExistingLock"/> refuses to record one unless this is
/// <see cref="CardOwner.ProductOwner"/> — the one permission in the system that exists to be
/// granted from outside the agents (§8a block C brief) — but the value is still carried on the
/// entry itself, the same "recorded rather than assumed" discipline every other <c>By</c> field
/// here already follows, rather than the type silently implying who it must have been.</param>
/// <param name="Reason">Why the bound was pushed further — free text, never empty or
/// whitespace-only (<see cref="IsValidReasonValue"/>). Work-lifecycle: "the reason it was pushed
/// further SHALL be legible later."</param>
/// <param name="Timestamp">When this authorisation was recorded.</param>
/// <param name="UnknownFields">Authorisation-line fields this build's parser does not recognise,
/// captured verbatim (raw key, raw value) in the order they were read, and re-emitted the same
/// way on write — the same extensibility rule <see cref="SectionVerdictEntry.UnknownFields"/>
/// applies. Empty for every entry this build itself constructs.</param>
internal sealed record SectionAuthorisationEntry(
    CardOwner By,
    string Reason,
    DateTimeOffset Timestamp,
    IReadOnlyList<(string Key, string RawValue)> UnknownFields)
{
    /// <summary>A reason is never empty or whitespace-only — the same discipline
    /// <see cref="SectionVerdictEntry.IsValidRangeValue"/> applies to its own wire values, checked
    /// on both doors into this value: <see cref="Callboard.Cli.CommandParser"/>'s <c>section
    /// authorise</c> parse arm before <see cref="CardStore.RecordSectionAuthorisation"/> is ever
    /// called, and <see cref="CardFileParser"/>'s own pre-construction check on a value read
    /// straight off the wire.</summary>
    internal static bool IsValidReasonValue(string value) => !string.IsNullOrWhiteSpace(value);

    // Same reason as SectionVerdictEntry's own override: the compiler-generated equality compares
    // UnknownFields by reference, which would make a freshly-constructed entry never equal to the
    // same entry after a parse round trip even when every field genuinely matches.
    public bool Equals(SectionAuthorisationEntry? other) =>
        other is not null
        && By == other.By
        && Reason == other.Reason
        && Timestamp == other.Timestamp
        && UnknownFields.SequenceEqual(other.UnknownFields);

    public override int GetHashCode() =>
        HashCode.Combine(By, Reason, Timestamp, UnknownFields.Count);
}
