namespace Callboard.Cards;

/// <summary>
/// The frontmatter fields known only on a <c>rule</c>, <c>hazard</c>, <c>obligation</c> or
/// <c>decision</c> card (§7 block A). Not part of <see cref="CardFrontmatter"/> — see that type's
/// doc comment for why kind-specific fields live in their own type, the same reason
/// <see cref="BlockCardFields"/>, <see cref="SectionCardFields"/> and <see cref="FindingCardFields"/>
/// are their own types.
///
/// <para>
/// <b><see cref="Condition"/> and <see cref="Cadence"/> are hazard-only (register: "Hazards carry a
/// verification condition").</b> Every other register kind always carries them <see langword="null"/>
/// — <see cref="CardStore.CreateCard"/> is the only writer, and it refuses to construct a
/// <c>hazard</c> without both (register: "the system refuses and states the condition it
/// requires"), so a hazard actually written by this build always has both set, and a rule/
/// obligation/decision never does. Optional here regardless, rather than split onto a fifth type,
/// because the block A brief scopes this block to the two-state lifecycle plus these two fields —
/// <c>owed_by</c> (obligation), <c>supersedes</c>/<c>superseded_by</c> (decision) and
/// <c>earned_from</c> (rule) are later blocks' additions to this same type, not separate types of
/// their own.
/// </para>
///
/// <para>
/// <b><see cref="DischargedBy"/>/<see cref="DischargedAt"/> mirror <see cref="SectionCardFields.
/// ClosedBy"/>/<see cref="SectionCardFields.ClosedAt"/></b> — set together, only by
/// <see cref="CardStore.DischargeRegisterCardUnderExistingLock"/>, recording who discharged the card
/// and when, the same "record the acting role and the time" discipline work-lifecycle already
/// requires for a section's own close. Both <see langword="null"/> while the card is open.
/// </para>
/// </summary>
internal sealed record RegisterCardFields
{
    /// <summary>The condition under which this hazard can be verified still to hold, or
    /// <see langword="null"/> for any non-hazard register card, or a hazard older than this
    /// field.</summary>
    internal string? Condition { get; init; }

    /// <summary>The cadence at which <see cref="Condition"/> is re-checked, or
    /// <see langword="null"/> for the same reasons as <see cref="Condition"/>.</summary>
    internal string? Cadence { get; init; }

    /// <summary>The role that discharged this card, or <see langword="null"/> while it is still
    /// open. Set together with <see cref="DischargedAt"/>, never independently.</summary>
    internal CardOwner? DischargedBy { get; init; }

    /// <summary>When this card was discharged, or <see langword="null"/> while it is still
    /// open.</summary>
    internal DateTimeOffset? DischargedAt { get; init; }

    /// <summary>The four fields, all unset — every card that is not one of the four register
    /// kinds, and a brand-new register card with no condition/cadence declared and not yet
    /// discharged.</summary>
    internal static readonly RegisterCardFields Empty = new(null, null, null, null);

    internal RegisterCardFields(string? Condition, string? Cadence, CardOwner? DischargedBy, DateTimeOffset? DischargedAt)
    {
        this.Condition = Condition;
        this.Cadence = Cadence;
        this.DischargedBy = DischargedBy;
        this.DischargedAt = DischargedAt;
    }
}
