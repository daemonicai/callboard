using System.Collections.Immutable;

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
///
/// <para>
/// <b><see cref="EarnedFrom"/> is rule-only (§7 block E, register: "Authoring a rule from findings
/// SHALL create a new card and SHALL record which findings it was earned from").</b> Holds
/// <b>finding card ids</b>, resolved through <see cref="CardIdentityResolver"/> before a rule is
/// authored — never a free-text label — and the findings it names may live in another section,
/// another change, or an archived change, since that cross-change reach is the whole rationale
/// register gives for this field existing at all. Required and non-empty at authoring
/// (<c>CommandParser.ParseRuleAuthor</c>), so a rule authored by that verb always carries at least
/// one; empty on every rule created by <c>rule create</c> and on every non-rule register card,
/// the same "empty is absence" convention <see cref="BlockCardFields.Tasks"/>/<see cref="BlockCardFields.
/// BlockedBy"/> already use — reused here rather than a second list-valued wire convention (§7 block
/// E brief). Immutable and validated the same "three-door" way those two fields are — see this
/// property's own accessor and <see cref="BlockCardFields"/>'s doc comment for why a constructor-only
/// check would not be enough.
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

    private readonly ImmutableArray<string> _earnedFrom;

    /// <summary>The ids of the findings this rule was earned from, in the order recorded. Never
    /// contains an empty or whitespace-only item — the same <see cref="BlockCardFields.
    /// IsValidListItem"/> discipline <see cref="BlockCardFields.Tasks"/>/<see cref="BlockCardFields.
    /// BlockedBy"/> already enforce, reused rather than restated, since a card identity is never
    /// empty or whitespace-only for either type. An empty array means "not authored from findings" —
    /// every rule created by <c>rule create</c> and every non-rule register card.</summary>
    internal ImmutableArray<string> EarnedFrom
    {
        get => _earnedFrom;
        init => _earnedFrom = RequireNoEmptyOrWhitespaceItems(value, nameof(EarnedFrom));
    }

    /// <summary>The eight fields, all unset — every card that is not one of the four register
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
        string? SupersededBy = null,
        IReadOnlyList<string>? EarnedFrom = null)
    {
        this.Condition = Condition;
        this.Cadence = Cadence;
        this.DischargedBy = DischargedBy;
        this.DischargedAt = DischargedAt;
        this.OwedBy = OwedBy;
        this.Supersedes = Supersedes;
        this.SupersededBy = SupersededBy;

        // .ToImmutableArray() copies EarnedFrom's current contents now, at construction time — the
        // same "a caller's later mutation of a retained List<T> cannot reach the built value"
        // guarantee BlockCardFields.Tasks/BlockedBy document for exactly this reason. The assignment
        // then runs through this type's own init accessor above, the one both the constructor and a
        // `with` expression are forced to pass.
        this.EarnedFrom = (EarnedFrom ?? []).ToImmutableArray();
    }

    /// <summary>The one predicate <see cref="EarnedFrom"/> reacts to, on every door in — the
    /// constructor (via the init accessor above) and a <c>with</c> expression (the same accessor,
    /// lowered). Delegates to <see cref="BlockCardFields.IsValidListItem"/> rather than restating it:
    /// the rule is identical ("never empty or whitespace-only"), and a card identity means the same
    /// thing regardless of which list-valued field is holding it.</summary>
    private static ImmutableArray<string> RequireNoEmptyOrWhitespaceItems(ImmutableArray<string> items, string paramName)
    {
        foreach (var item in items)
        {
            if (!BlockCardFields.IsValidListItem(item))
            {
                throw new ArgumentException(
                    $"'{paramName}' cannot contain an empty or whitespace-only item — a card identity is " +
                    "never empty, and an empty item is indistinguishable on the wire from an empty list.",
                    paramName);
            }
        }

        return items;
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
    internal const string EarnedFrom = "earned_from";

    /// <summary>Every register-only frontmatter key this build recognises, in the order
    /// <see cref="CardFileWriter"/> emits them. The one list <see cref="CardFileParser"/>'s known-
    /// key set is built from — see this type's own doc comment.</summary>
    internal static readonly IReadOnlyList<string> All =
        [Condition, Cadence, OwedBy, Supersedes, SupersededBy, DischargedBy, DischargedAt, EarnedFrom];
}
