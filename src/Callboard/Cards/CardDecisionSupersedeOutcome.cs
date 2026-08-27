namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.SupersedeDecision"/> (§7 block C, register: "A
/// decision MAY name the decision it supersedes and the decision that supersedes it") can end.
/// Same shape and reasoning as <see cref="CardRegisterDischargeOutcome"/> — <see cref="Superseded"/>
/// carries both cards as written, since this is a two-card write and a caller reporting the result
/// needs both halves, not just the one it happened to name first on the command line.
///
/// <para>
/// <b>No <c>InvalidStatus</c> case (§12 block A).</b> See <see cref="CardRegisterDischargeOutcome"/>'s
/// own doc comment: <see cref="CardFileParser"/> now validates a register card's own <c>status</c>
/// at the parse door, so <see cref="CardCorrupt"/> carries that refusal's reason instead — for both
/// cards this verb resolves, since <see cref="CardStore.SupersedeDecision"/> confirms each is a
/// <c>decision</c> card before either status was ever inspected.
/// </para>
/// </summary>
internal abstract record CardDecisionSupersedeOutcome
{
    private CardDecisionSupersedeOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Superseded, TResult> onSuperseded,
        Func<SelfSupersession, TResult> onSelfSupersession,
        Func<ResolvedSelfSupersession, TResult> onResolvedSelfSupersession,
        Func<SupersededAlreadyDischarged, TResult> onSupersededAlreadyDischarged,
        Func<SupersedingAlreadyDischarged, TResult> onSupersedingAlreadyDischarged,
        Func<NotADecisionCard, TResult> onNotADecisionCard,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState);

    /// <param name="SupersedingCard">The successor decision, now carrying <c>supersedes</c>.
    /// </param>
    /// <param name="SupersededCard">The earlier decision, now carrying <c>status: discharged</c>
    /// and <c>superseded_by</c>.</param>
    internal sealed record Superseded(CardFile SupersedingCard, CardFile SupersededCard) : CardDecisionSupersedeOutcome
    {
        internal override TResult Match<TResult>(Func<Superseded, TResult> onSuperseded, Func<SelfSupersession, TResult> onSelfSupersession, Func<ResolvedSelfSupersession, TResult> onResolvedSelfSupersession, Func<SupersededAlreadyDischarged, TResult> onSupersededAlreadyDischarged, Func<SupersedingAlreadyDischarged, TResult> onSupersedingAlreadyDischarged, Func<NotADecisionCard, TResult> onNotADecisionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onSuperseded(this);
    }

    /// <summary>The superseding and superseded card names resolved to the identical card id, on
    /// caller-supplied path text alone, before any lock is requested (<see cref="CardStore.
    /// SupersedeDecision"/>'s own path-equality check) — "a decision superseding itself is not a
    /// coherent record" (§7 block C brief). No card has been resolved yet at this point, so there
    /// is nothing to record against — not <see cref="ICardRefusalReason"/>. See
    /// <see cref="ResolvedSelfSupersession"/> for the sibling occurrence once both locks are held.
    /// </summary>
    internal sealed record SelfSupersession(string Id) : CardDecisionSupersedeOutcome
    {
        internal override TResult Match<TResult>(Func<Superseded, TResult> onSuperseded, Func<SelfSupersession, TResult> onSelfSupersession, Func<ResolvedSelfSupersession, TResult> onResolvedSelfSupersession, Func<SupersededAlreadyDischarged, TResult> onSupersededAlreadyDischarged, Func<SupersedingAlreadyDischarged, TResult> onSupersedingAlreadyDischarged, Func<NotADecisionCard, TResult> onNotADecisionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onSelfSupersession(this);
    }

    /// <summary>The superseding and superseded card names resolved to the identical card id,
    /// discovered by the id-based recheck in <see cref="CardStore.SupersedeDecisionUnderLocks"/> —
    /// <b>after</b> both cards have been read and both locks are held (§9 block A2 remediation,
    /// reviewer/Architect ruling: "a refusal against two resolved, locked cards is a recordable
    /// refusal", split from <see cref="SelfSupersession"/> rather than sharing its blanket
    /// non-recording disposition). Reachable when two differently-spelled caller-supplied paths
    /// resolve to the same decision id, which <see cref="SelfSupersession"/>'s own path-string check
    /// cannot catch — when this fires it is, by construction, exactly the refusal that would
    /// otherwise leave no trail. Refusal-shaped.</summary>
    internal sealed record ResolvedSelfSupersession(string Id) : CardDecisionSupersedeOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Superseded, TResult> onSuperseded, Func<SelfSupersession, TResult> onSelfSupersession, Func<ResolvedSelfSupersession, TResult> onResolvedSelfSupersession, Func<SupersededAlreadyDischarged, TResult> onSupersededAlreadyDischarged, Func<SupersedingAlreadyDischarged, TResult> onSupersedingAlreadyDischarged, Func<NotADecisionCard, TResult> onNotADecisionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onResolvedSelfSupersession(this);

        public string RefusingRule => "register: a decision superseding itself is not a coherent record";

        public string Remedy => "name a different decision as the one being superseded.";
    }

    /// <summary>The decision to be superseded is already discharged — "superseding an
    /// already-discharged decision is a refusal, not a re-supersession" (§7 block C brief).
    /// Refusal-shaped, same code as <see cref="CardRegisterDischargeOutcome.AlreadyDischarged"/>
    /// (Architect ruling: supersession sets the state block A already shipped, it does not
    /// introduce a parallel one).</summary>
    internal sealed record SupersededAlreadyDischarged(string FilePath) : CardDecisionSupersedeOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Superseded, TResult> onSuperseded, Func<SelfSupersession, TResult> onSelfSupersession, Func<ResolvedSelfSupersession, TResult> onResolvedSelfSupersession, Func<SupersededAlreadyDischarged, TResult> onSupersededAlreadyDischarged, Func<SupersedingAlreadyDischarged, TResult> onSupersedingAlreadyDischarged, Func<NotADecisionCard, TResult> onNotADecisionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onSupersededAlreadyDischarged(this);

        public string RefusingRule => "register: superseding an already-discharged decision is a refusal, not a re-supersession";

        public string Remedy => $"'{FilePath}' is already discharged; name a decision that is still open as the one being superseded.";
    }

    /// <summary>The decision doing the superseding is itself already discharged (already
    /// superseded by something else) — a discharged decision is not the record's current word on
    /// its own matter, so it cannot newly become the successor to another. This is also the check
    /// that keeps <c>supersedes</c>/<c>superseded_by</c> acyclic — see
    /// <see cref="CardStore.SupersedeDecision"/>'s own doc comment for the proof. Refusal-shaped,
    /// same code as <see cref="SupersededAlreadyDischarged"/>, distinguished by message.</summary>
    internal sealed record SupersedingAlreadyDischarged(string FilePath) : CardDecisionSupersedeOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Superseded, TResult> onSuperseded, Func<SelfSupersession, TResult> onSelfSupersession, Func<ResolvedSelfSupersession, TResult> onResolvedSelfSupersession, Func<SupersededAlreadyDischarged, TResult> onSupersededAlreadyDischarged, Func<SupersedingAlreadyDischarged, TResult> onSupersedingAlreadyDischarged, Func<NotADecisionCard, TResult> onNotADecisionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onSupersedingAlreadyDischarged(this);

        public string RefusingRule => "register: a discharged decision cannot newly supersede another";

        public string Remedy => $"'{FilePath}' is already discharged; name a decision that is still open as the one doing the superseding.";
    }

    /// <summary>One of the two resolved cards is not a <c>decision</c>. Refusal-shaped.</summary>
    internal sealed record NotADecisionCard(string FilePath, CardKind Kind) : CardDecisionSupersedeOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Superseded, TResult> onSuperseded, Func<SelfSupersession, TResult> onSelfSupersession, Func<ResolvedSelfSupersession, TResult> onResolvedSelfSupersession, Func<SupersededAlreadyDischarged, TResult> onSupersededAlreadyDischarged, Func<SupersedingAlreadyDischarged, TResult> onSupersedingAlreadyDischarged, Func<NotADecisionCard, TResult> onNotADecisionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onNotADecisionCard(this);

        public string RefusingRule => "register: supersession applies only to decision cards";

        public string Remedy => $"'{FilePath}' is a '{Kind.ToWireString()}' card, not a 'decision' card; target a decision on both sides.";
    }

    /// <summary>One of the two resolved paths no longer has a card on disk (a race between
    /// resolution and locking). Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardDecisionSupersedeOutcome
    {
        internal override TResult Match<TResult>(Func<Superseded, TResult> onSuperseded, Func<SelfSupersession, TResult> onSelfSupersession, Func<ResolvedSelfSupersession, TResult> onResolvedSelfSupersession, Func<SupersededAlreadyDischarged, TResult> onSupersededAlreadyDischarged, Func<SupersedingAlreadyDischarged, TResult> onSupersedingAlreadyDischarged, Func<NotADecisionCard, TResult> onNotADecisionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardNotFound(this);
    }

    /// <summary>One of the two resolved paths does not resolve under the given root/scope
    /// (<see cref="AnchoredCardPath.TryCreate"/>). Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardDecisionSupersedeOutcome
    {
        internal override TResult Match<TResult>(Func<Superseded, TResult> onSuperseded, Func<SelfSupersession, TResult> onSelfSupersession, Func<ResolvedSelfSupersession, TResult> onResolvedSelfSupersession, Func<SupersededAlreadyDischarged, TResult> onSupersededAlreadyDischarged, Func<SupersedingAlreadyDischarged, TResult> onSupersedingAlreadyDischarged, Func<NotADecisionCard, TResult> onNotADecisionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onLayoutMismatch(this);
    }

    /// <summary>One of the two cards exists but could not be parsed. Neither refusal nor
    /// tool-failure.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardDecisionSupersedeOutcome
    {
        internal override TResult Match<TResult>(Func<Superseded, TResult> onSuperseded, Func<SelfSupersession, TResult> onSelfSupersession, Func<ResolvedSelfSupersession, TResult> onResolvedSelfSupersession, Func<SupersededAlreadyDischarged, TResult> onSupersededAlreadyDischarged, Func<SupersedingAlreadyDischarged, TResult> onSupersedingAlreadyDischarged, Func<NotADecisionCard, TResult> onNotADecisionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardCorrupt(this);
    }

    /// <summary>working-context: "No figure SHALL be hand-entered anywhere in the system" (§10
    /// block C) — <paramref name="Key"/> names a reserved derived-state field (<see
    /// cref="DerivedStateFieldKeys.All"/>) present on the target card's <see cref="CardFile.
    /// UnknownFrontmatterFields"/>, the door a hand-edited card's frontmatter uses to reach this far
    /// at all (nothing this build's own CLI ever writes one). Refusal-shaped, card-addressed (§9
    /// block A3): checked immediately once the card is read, before the supersession is allowed to
    /// proceed, so this write never re-emits (and never launders forward) a hand-entered count or
    /// next-step pin it did not itself write. See <see cref="CardWriteResult.HandEnteredDerivedState"/>
    /// for the sibling case on the generic comment/handover surface.</summary>
    internal sealed record HandEnteredDerivedState(string FilePath, string Key) : CardDecisionSupersedeOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Superseded, TResult> onSuperseded, Func<SelfSupersession, TResult> onSelfSupersession, Func<ResolvedSelfSupersession, TResult> onResolvedSelfSupersession, Func<SupersededAlreadyDischarged, TResult> onSupersededAlreadyDischarged, Func<SupersedingAlreadyDischarged, TResult> onSupersedingAlreadyDischarged, Func<NotADecisionCard, TResult> onNotADecisionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onHandEnteredDerivedState(this);

        public string RefusingRule => "working-context: no figure shall be hand-entered";

        public string Remedy =>
            $"'{FilePath}' carries a hand-entered reserved derived-state field '{Key}' in its frontmatter; " +
            "remove it — this state is derived at request time, never stored, and is available from 'callboard state'.";
    }

    /// <summary>Enforcement itself is unavailable: one of the two locks could not be acquired
    /// within its timeout, or an I/O error occurred while writing. Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardDecisionSupersedeOutcome
    {
        internal override TResult Match<TResult>(Func<Superseded, TResult> onSuperseded, Func<SelfSupersession, TResult> onSelfSupersession, Func<ResolvedSelfSupersession, TResult> onResolvedSelfSupersession, Func<SupersededAlreadyDischarged, TResult> onSupersededAlreadyDischarged, Func<SupersedingAlreadyDischarged, TResult> onSupersedingAlreadyDischarged, Func<NotADecisionCard, TResult> onNotADecisionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onToolFailure(this);
    }
}
