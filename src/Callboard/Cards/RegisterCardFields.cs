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
///
/// <para>
/// <b><see cref="OwedBy"/> is obligation-only (§7 block C, register: "An obligation SHALL name the
/// section expected to discharge it").</b> Holds a <b>section card id</b>, resolved through
/// <see cref="CardIdentityResolver"/> before an obligation is ever created — never a free-text
/// label, the exact defect §7 was opened to stop repeating. Required at creation
/// (<c>CommandParser.ParseObligationCreate</c>), so an obligation actually written by this build
/// always has it set.
/// </para>
///
/// <para>
/// <b><see cref="Supersedes"/>/<see cref="SupersededBy"/> are decision-only (§7 block C, register:
/// "A decision MAY name the decision it supersedes and the decision that supersedes it").</b> Both
/// hold a <b>decision card id</b>. <see cref="Supersedes"/> is set on the successor decision by
/// <c>decision supersede</c>; <see cref="SupersededBy"/> is set on the earlier decision by the same
/// call, alongside discharging it — see <see cref="CardStore.SupersedeDecision"/> for the two-card
/// write that keeps both sides in agreement. Unlike <see cref="Condition"/>/<see cref="Cadence"/>,
/// these two are independent of each other: a decision can carry <see cref="Supersedes"/> without
/// ever being superseded itself, and a decision discharged by supersession carries
/// <see cref="SupersededBy"/> whether or not it ever superseded anything of its own.
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

    /// <summary>The id of the <c>section</c> card this obligation is owed to, or
    /// <see langword="null"/> for any non-obligation register card, or an obligation older than
    /// this field. Always resolved through <see cref="CardIdentityResolver"/> before being set —
    /// see this type's own doc comment.</summary>
    internal string? OwedBy { get; init; }

    /// <summary>The id of the <c>decision</c> card this decision supersedes, or
    /// <see langword="null"/> for any non-decision register card, or a decision that has not
    /// superseded anything.</summary>
    internal string? Supersedes { get; init; }

    /// <summary>The id of the <c>decision</c> card that supersedes this one, or
    /// <see langword="null"/> for any non-decision register card, or a decision not yet
    /// superseded. Set together with <see cref="DischargedBy"/>/<see cref="DischargedAt"/> by
    /// <see cref="CardStore.SupersedeDecision"/> — a decision is discharged for exactly one
    /// reason, being superseded, so all three are set in the same write.</summary>
    internal string? SupersededBy { get; init; }

    /// <summary>The seven fields, all unset — every card that is not one of the four register
    /// kinds, and a brand-new register card with none of them declared and not yet discharged.
    /// </summary>
    internal static readonly RegisterCardFields Empty = new(null, null, null, null);

    internal RegisterCardFields(
        string? Condition,
        string? Cadence,
        CardOwner? DischargedBy,
        DateTimeOffset? DischargedAt,
        string? OwedBy = null,
        string? Supersedes = null,
        string? SupersededBy = null)
    {
        this.Condition = Condition;
        this.Cadence = Cadence;
        this.DischargedBy = DischargedBy;
        this.DischargedAt = DischargedAt;
        this.OwedBy = OwedBy;
        this.Supersedes = Supersedes;
        this.SupersededBy = SupersededBy;
    }
}

/// <summary>
/// The single declaration of every frontmatter key <see cref="RegisterCardFields"/> can carry —
/// the fix for a reviewer-reproduced defect (§7 block C remediation): <see cref="CardFileWriter"/>
/// and <see cref="CardFileParser"/> used to spell these seven keys as independent string literals,
/// two lists that could silently drift. They drifted: <c>owed_by</c>/<c>supersedes</c>/
/// <c>superseded_by</c> were in the writer's list but not the parser's known-key set, so the
/// parser filed them as <see cref="CardFile.UnknownFrontmatterFields"/> — which the writer then
/// faithfully re-emitted <em>alongside</em> the known-field line it wrote from
/// <see cref="RegisterCardFields"/> itself. Every parse-then-write cycle duplicated the line, and
/// duplication compounded across repeated cycles (a decision superseded more than once; an
/// obligation later discharged) — silent, unbounded corruption of the primary record.
///
/// <para>
/// <b>The fix is structural, not three added strings.</b> <see cref="CardFileWriter.Serialize"/>
/// and <see cref="CardFileParser"/>'s <c>RegisterOnlyFrontmatterKeys</c> both read from
/// <see cref="All"/> now — there is exactly one place these seven names are spelled, so a writer
/// key and a parser key can no longer name the same field two different ways, or list seven keys
/// on one side and six on the other. <c>RegisterCardFieldsKeyCoverageTests</c> closes the
/// remaining gap this alone cannot: a <em>new</em> property added to <see cref="RegisterCardFields"/>
/// without ever being added to <see cref="All"/> at all — reflection over the record's own
/// properties, enumerated from the code rather than hand-listed, so that test fails the moment a
/// field is added here and forgotten there, before it ever reaches a writer/parser mismatch to
/// reproduce.
/// </para>
/// </summary>
internal static class RegisterCardFieldKeys
{
    internal const string Condition = "condition";
    internal const string Cadence = "cadence";
    internal const string OwedBy = "owed_by";
    internal const string Supersedes = "supersedes";
    internal const string SupersededBy = "superseded_by";
    internal const string DischargedBy = "discharged_by";
    internal const string DischargedAt = "discharged_at";

    /// <summary>Every register-only frontmatter key this build recognises, in the order
    /// <see cref="CardFileWriter"/> emits them. The one list <see cref="CardFileParser"/>'s known-
    /// key set is built from — see this type's own doc comment.</summary>
    internal static readonly IReadOnlyList<string> All =
        [Condition, Cadence, OwedBy, Supersedes, SupersededBy, DischargedBy, DischargedAt];
}
