namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.CompactRules"/> (§7 block F, register: "The system
/// SHALL support compacting several rules into a family rule stating what they share ... every
/// absorbed rule SHALL remain retrievable") can end. Same shape and reasoning as <see cref="
/// CardDecisionSupersedeOutcome"/>, generalised from a two-card write to an N+1-card one: <see
/// cref="Compacted"/> carries the family card and every absorbed card as written, since a caller
/// reporting the result needs the whole set, not just the family it happened to name on the command
/// line.
/// </summary>
internal abstract record CardRuleCompactOutcome
{
    private CardRuleCompactOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Compacted, TResult> onCompacted,
        Func<RoleNotPermitted, TResult> onRoleNotPermitted,
        Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet,
        Func<SelfAbsorption, TResult> onSelfAbsorption,
        Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule,
        Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged,
        Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged,
        Func<InvalidStatus, TResult> onInvalidStatus,
        Func<NotARuleCard, TResult> onNotARuleCard,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="FamilyCard">The family rule, now carrying <c>absorbs</c> naming every member.
    /// </param>
    /// <param name="AbsorbedCards">Every absorbed rule, now carrying <c>status: discharged</c> and
    /// <c>superseded_by</c> naming the family, in the order <see cref="CardStore.CompactRules"/> was
    /// given them.</param>
    internal sealed record Compacted(CardFile FamilyCard, IReadOnlyList<CardFile> AbsorbedCards) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCompacted(this);
    }

    /// <summary>The acting role is not permitted to perform this operation — register: "Compaction
    /// of change-scoped rules SHALL be performed by the architect at archive". Checked first, ahead
    /// of every other check, directly in <see cref="CardStore.CompactRules"/> — not at either call
    /// site — so <c>rule compact</c> and the <c>change archive --compact-family/--absorbs</c> hook
    /// inherit the identical enforcement rather than each re-implementing it (Architect ruling: "the
    /// constraint belongs to the operation, not to one entry point"). Refusal-shaped. Repository-
    /// scoped compaction is out of scope for this type entirely (block G, 7.9) — this case says
    /// nothing about who may perform that, only about the change-scoped compaction this type
    /// exists to write.</summary>
    internal sealed record RoleNotPermitted(CardOwner AttemptedRole, CardOwner RequiredRole) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onRoleNotPermitted(this);
    }

    /// <summary>No rule ids were given to absorb — "a family with no members is not a family" (§7
    /// block F brief item 5). Refusal-shaped. The CLI already refuses this at parse time
    /// (argv-decidable, the same discipline <c>--earned-from</c> uses), so this is reachable only
    /// through <see cref="CardStore.CompactRules"/> called directly.</summary>
    internal sealed record EmptyAbsorbSet : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onEmptyAbsorbSet(this);
    }

    /// <summary>The family's own id appears in its own absorb set — a family absorbing itself is
    /// not a coherent record, the same fact <see cref="CardDecisionSupersedeOutcome.
    /// SelfSupersession"/> already names for decisions. Refusal-shaped.</summary>
    internal sealed record SelfAbsorption(string Id) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onSelfAbsorption(this);
    }

    /// <summary>The same rule id was named more than once in the absorb set — locking the same
    /// path twice within one call would hang this call against itself (the same reasoning
    /// <see cref="CardDecisionSupersedeOutcome.SelfSupersession"/> is checked ahead of locking for).
    /// Refusal-shaped.</summary>
    internal sealed record DuplicateAbsorbedRule(string Id) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onDuplicateAbsorbedRule(this);
    }

    /// <summary>The family rule is itself already discharged (already absorbed by another family) —
    /// a discharged rule is not the record's current word on its own matter, so it cannot newly act
    /// as a family. This is also the check that keeps compaction acyclic — see <see cref="
    /// CardStore.CompactRules"/>'s own doc comment for the proof. Refusal-shaped, same code as
    /// <see cref="AbsorbedAlreadyDischarged"/> (Architect ruling on decisions, reused here:
    /// compaction sets the state block A already shipped, it does not introduce a parallel one).
    /// </summary>
    internal sealed record FamilyAlreadyDischarged(string FilePath) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onFamilyAlreadyDischarged(this);
    }

    /// <summary>One of the rules named to be absorbed is already discharged — "absorbing an
    /// already-discharged rule" is a refusal (§7 block F brief item 5), not a re-absorption.
    /// Refusal-shaped.</summary>
    internal sealed record AbsorbedAlreadyDischarged(string FilePath) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onAbsorbedAlreadyDischarged(this);
    }

    /// <summary>One of the cards' own <c>status</c> does not parse as <see cref="
    /// RegisterLifecycleState"/> — register: "SHALL NOT occupy flow states". Refusal-shaped.
    /// </summary>
    internal sealed record InvalidStatus(string FilePath, string Status) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onInvalidStatus(this);
    }

    /// <summary>One of the resolved cards is not a <c>rule</c>. Refusal-shaped.</summary>
    internal sealed record NotARuleCard(string FilePath, CardKind Kind) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotARuleCard(this);
    }

    /// <summary>One of the resolved paths no longer has a card on disk (a race between resolution
    /// and locking). Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardNotFound(this);
    }

    /// <summary>One of the resolved paths does not resolve under the given root/scope/change
    /// (<see cref="AnchoredCardPath.TryCreate"/>) — includes a rule that is not change-scoped, or
    /// that is change-scoped but belongs to a different change than the one named. Refusal-shaped.
    /// </summary>
    internal sealed record LayoutMismatch(string Reason) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>One of the cards exists but could not be parsed. Neither refusal nor tool-failure.
    /// </summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardCorrupt(this);
    }

    /// <summary>Enforcement itself is unavailable: one of the N+1 locks could not be acquired within
    /// its timeout, or an I/O error occurred while writing. Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
