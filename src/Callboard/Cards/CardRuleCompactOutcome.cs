namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.CompactRules"/> (§7 block F, register: "The system
/// SHALL support compacting several rules into a family rule stating what they share ... every
/// absorbed rule SHALL remain retrievable") can end. Same shape and reasoning as <see cref="
/// CardDecisionSupersedeOutcome"/>, generalised from a two-card write to an N+1-card one: <see
/// cref="Compacted"/> carries the family card and every absorbed card as written, since a caller
/// reporting the result needs the whole set, not just the family it happened to name on the command
/// line.
///
/// <para>
/// <b>No <c>InvalidStatus</c> case (§12 block A).</b> See <see cref="CardRegisterDischargeOutcome"/>'s
/// own doc comment: <see cref="CardFileParser"/> now validates a register card's own <c>status</c>
/// at the parse door, so <see cref="CardCorrupt"/> carries that refusal's reason instead — for every
/// card this verb resolves, since <see cref="CardStore.CompactRules"/> confirms each is a
/// <c>rule</c> card before any status was ever inspected.
/// </para>
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
        Func<ResolvedSelfAbsorption, TResult> onResolvedSelfAbsorption,
        Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule,
        Func<ResolvedDuplicateAbsorbedRule, TResult> onResolvedDuplicateAbsorbedRule,
        Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged,
        Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged,
        Func<NotARuleCard, TResult> onNotARuleCard,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState);

    /// <param name="FamilyCard">The family rule, now carrying <c>absorbs</c> naming every member.
    /// </param>
    /// <param name="AbsorbedCards">Every absorbed rule, now carrying <c>status: discharged</c> and
    /// <c>superseded_by</c> naming the family, in the order <see cref="CardStore.CompactRules"/> was
    /// given them.</param>
    internal sealed record Compacted(CardFile FamilyCard, IReadOnlyList<CardFile> AbsorbedCards) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<ResolvedSelfAbsorption, TResult> onResolvedSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<ResolvedDuplicateAbsorbedRule, TResult> onResolvedDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
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
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<ResolvedSelfAbsorption, TResult> onResolvedSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<ResolvedDuplicateAbsorbedRule, TResult> onResolvedDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onRoleNotPermitted(this);
    }

    /// <summary>No rule ids were given to absorb — "a family with no members is not a family" (§7
    /// block F brief item 5). Refusal-shaped. The CLI already refuses this at parse time
    /// (argv-decidable, the same discipline <c>--earned-from</c> uses), so this is reachable only
    /// through <see cref="CardStore.CompactRules"/> called directly.</summary>
    internal sealed record EmptyAbsorbSet : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<ResolvedSelfAbsorption, TResult> onResolvedSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<ResolvedDuplicateAbsorbedRule, TResult> onResolvedDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onEmptyAbsorbSet(this);
    }

    /// <summary>The family's own id equals one it was about to absorb, checked on caller-supplied
    /// path text alone, before any lock is requested (<see cref="CardStore.CompactRules"/>). No card
    /// has been resolved yet at this point, so there is nothing to record against (§9 architect
    /// ruling: "only a card-addressed refusal records") — not <see cref="ICardRefusalReason"/>. See
    /// <see cref="ResolvedSelfAbsorption"/> for the sibling occurrence once locks are held.
    /// </summary>
    internal sealed record SelfAbsorption(string Id) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<ResolvedSelfAbsorption, TResult> onResolvedSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<ResolvedDuplicateAbsorbedRule, TResult> onResolvedDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onSelfAbsorption(this);
    }

    /// <summary>The family's own id equals one it was about to absorb, discovered by the id-based
    /// recheck in <see cref="CardStore.CompactRulesUnderLocks"/> — <b>after</b> both the family and
    /// the member have been read and every one of the N+1 locks is held (§9 block A2 remediation,
    /// reviewer/Architect ruling: "a refusal against two resolved, locked cards is a recordable
    /// refusal", split from <see cref="SelfAbsorption"/> rather than sharing its blanket
    /// non-recording disposition). This is the case a caller-supplied path pair that resolves to the
    /// same id under different spellings (e.g. differing only in case, or one relative/one absolute)
    /// reaches — <see cref="SelfAbsorption"/>'s own path-string check cannot catch it, so when this
    /// fires it is, by construction, exactly the refusal that would otherwise leave no trail.
    /// Refusal-shaped.</summary>
    internal sealed record ResolvedSelfAbsorption(string Id) : CardRuleCompactOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<ResolvedSelfAbsorption, TResult> onResolvedSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<ResolvedDuplicateAbsorbedRule, TResult> onResolvedDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onResolvedSelfAbsorption(this);

        public string RefusingRule => "register: a family with no members, or one absorbing itself, is not a family";

        public string Remedy => $"remove '{Id}' from the absorb set; a family cannot absorb itself.";
    }

    /// <summary>The same rule id was named more than once in the absorb set, checked on
    /// caller-supplied path text, before any lock is requested — locking the same path twice within
    /// one call would hang this call against itself. No card has been resolved yet at this point, so
    /// there is nothing to record against — not <see cref="ICardRefusalReason"/>. See
    /// <see cref="ResolvedDuplicateAbsorbedRule"/> for the sibling occurrence once locks are held.
    /// </summary>
    internal sealed record DuplicateAbsorbedRule(string Id) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<ResolvedSelfAbsorption, TResult> onResolvedSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<ResolvedDuplicateAbsorbedRule, TResult> onResolvedDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onDuplicateAbsorbedRule(this);
    }

    /// <summary>The same rule was named more than once in the absorb set, discovered by the
    /// id-based recheck in <see cref="CardStore.CompactRulesUnderLocks"/> — <b>after</b> the member
    /// has been read and every lock is held (§9 block A2 remediation — see
    /// <see cref="ResolvedSelfAbsorption"/>'s own doc comment for the shared reasoning, split from
    /// <see cref="DuplicateAbsorbedRule"/> the same way). Reachable when two differently-spelled
    /// caller-supplied paths resolve to the same rule id, which the pre-lock path-string check
    /// cannot catch. Refusal-shaped.</summary>
    internal sealed record ResolvedDuplicateAbsorbedRule(string Id) : CardRuleCompactOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<ResolvedSelfAbsorption, TResult> onResolvedSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<ResolvedDuplicateAbsorbedRule, TResult> onResolvedDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onResolvedDuplicateAbsorbedRule(this);

        public string RefusingRule => "register: the same rule cannot be named twice in one absorb set";

        public string Remedy => $"name '{Id}' only once in the absorb set.";
    }

    /// <summary>The family rule is itself already discharged (already absorbed by another family) —
    /// a discharged rule is not the record's current word on its own matter, so it cannot newly act
    /// as a family. This is also the check that keeps compaction acyclic — see <see cref="
    /// CardStore.CompactRules"/>'s own doc comment for the proof. Refusal-shaped, same code as
    /// <see cref="AbsorbedAlreadyDischarged"/> (Architect ruling on decisions, reused here:
    /// compaction sets the state block A already shipped, it does not introduce a parallel one).
    /// </summary>
    internal sealed record FamilyAlreadyDischarged(string FilePath) : CardRuleCompactOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<ResolvedSelfAbsorption, TResult> onResolvedSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<ResolvedDuplicateAbsorbedRule, TResult> onResolvedDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onFamilyAlreadyDischarged(this);

        public string RefusingRule => "register: a discharged rule cannot newly act as a compaction family";

        public string Remedy => $"'{FilePath}' is already discharged; name a rule that is still open as the family.";
    }

    /// <summary>One of the rules named to be absorbed is already discharged — "absorbing an
    /// already-discharged rule" is a refusal (§7 block F brief item 5), not a re-absorption.
    /// Refusal-shaped.</summary>
    internal sealed record AbsorbedAlreadyDischarged(string FilePath) : CardRuleCompactOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<ResolvedSelfAbsorption, TResult> onResolvedSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<ResolvedDuplicateAbsorbedRule, TResult> onResolvedDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onAbsorbedAlreadyDischarged(this);

        public string RefusingRule => "register: absorbing an already-discharged rule is a refusal, not a re-absorption";

        public string Remedy => $"'{FilePath}' is already discharged; remove it from the absorb set.";
    }

    /// <summary>One of the resolved cards is not a <c>rule</c>. Refusal-shaped.</summary>
    internal sealed record NotARuleCard(string FilePath, CardKind Kind) : CardRuleCompactOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<ResolvedSelfAbsorption, TResult> onResolvedSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<ResolvedDuplicateAbsorbedRule, TResult> onResolvedDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onNotARuleCard(this);

        public string RefusingRule => "register: compaction applies only to rule cards";

        public string Remedy => $"'{FilePath}' is a '{Kind.ToWireString()}' card, not a 'rule' card; target rules on both the family and absorb sides.";
    }

    /// <summary>One of the resolved paths no longer has a card on disk (a race between resolution
    /// and locking). Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<ResolvedSelfAbsorption, TResult> onResolvedSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<ResolvedDuplicateAbsorbedRule, TResult> onResolvedDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardNotFound(this);
    }

    /// <summary>One of the resolved paths does not resolve under the given root/scope/change
    /// (<see cref="AnchoredCardPath.TryCreate"/>) — includes a rule that is not change-scoped, or
    /// that is change-scoped but belongs to a different change than the one named. Refusal-shaped.
    /// </summary>
    internal sealed record LayoutMismatch(string Reason) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<ResolvedSelfAbsorption, TResult> onResolvedSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<ResolvedDuplicateAbsorbedRule, TResult> onResolvedDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onLayoutMismatch(this);
    }

    /// <summary>One of the cards exists but could not be parsed. Neither refusal nor tool-failure.
    /// </summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<ResolvedSelfAbsorption, TResult> onResolvedSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<ResolvedDuplicateAbsorbedRule, TResult> onResolvedDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardCorrupt(this);
    }

    /// <summary>working-context: "No figure SHALL be hand-entered anywhere in the system" (§10
    /// block C) — <paramref name="Key"/> names a reserved derived-state field (<see
    /// cref="DerivedStateFieldKeys.All"/>) present on the target card's <see cref="CardFile.
    /// UnknownFrontmatterFields"/>, the door a hand-edited card's frontmatter uses to reach this far
    /// at all (nothing this build's own CLI ever writes one). Refusal-shaped, card-addressed (§9
    /// block A3): checked immediately once the card is read, before the compaction is allowed to
    /// proceed, so this write never re-emits (and never launders forward) a hand-entered count or
    /// next-step pin it did not itself write. See <see cref="CardWriteResult.HandEnteredDerivedState"/>
    /// for the sibling case on the generic comment/handover surface.</summary>
    internal sealed record HandEnteredDerivedState(string FilePath, string Key) : CardRuleCompactOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<ResolvedSelfAbsorption, TResult> onResolvedSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<ResolvedDuplicateAbsorbedRule, TResult> onResolvedDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onHandEnteredDerivedState(this);

        public string RefusingRule => "working-context: no figure shall be hand-entered";

        public string Remedy =>
            $"'{FilePath}' carries a hand-entered reserved derived-state field '{Key}' in its frontmatter; " +
            "remove it — this state is derived at request time, never stored, and is available from 'callboard state'.";
    }

    /// <summary>Enforcement itself is unavailable: one of the N+1 locks could not be acquired within
    /// its timeout, or an I/O error occurred while writing. Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardRuleCompactOutcome
    {
        internal override TResult Match<TResult>(Func<Compacted, TResult> onCompacted, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<EmptyAbsorbSet, TResult> onEmptyAbsorbSet, Func<SelfAbsorption, TResult> onSelfAbsorption, Func<ResolvedSelfAbsorption, TResult> onResolvedSelfAbsorption, Func<DuplicateAbsorbedRule, TResult> onDuplicateAbsorbedRule, Func<ResolvedDuplicateAbsorbedRule, TResult> onResolvedDuplicateAbsorbedRule, Func<FamilyAlreadyDischarged, TResult> onFamilyAlreadyDischarged, Func<AbsorbedAlreadyDischarged, TResult> onAbsorbedAlreadyDischarged, Func<NotARuleCard, TResult> onNotARuleCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onToolFailure(this);
    }
}
