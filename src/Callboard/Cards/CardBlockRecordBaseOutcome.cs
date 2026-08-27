namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.RecordBase"/> can end (§13, work-lifecycle: "Blocks
/// carry their brief context" — "The system SHALL provide a command that records <c>base</c>
/// against a block ... base SHALL NOT be one a role writes by hand"). Same shape and reasoning as
/// <see cref="CardGateResultOutcome"/> — this block's own named model: a single-field recorder with
/// its own outcome type, reusing <see cref="CardStore.ReservedDerivedStateFieldKeyIn"/>/<see
/// cref="CardStore.IsBlockCard"/>/<see cref="CardStore.RoundAgreesWithHistory"/> by direct call
/// rather than a bare wrapper over <see cref="CardStore.ApplyBlockTransition"/>.
///
/// <para>
/// <b><see cref="BaseImmutable"/> is not a near-duplicate of <see cref="CardBlockTransitionOutcome.
/// BaseImmutable"/> (Architect ruling item 2: "reuse the existing refusals; do not mint parallel
/// ones").</b> The two are observably the same fact from an agent's point of view — same refusal
/// code (<c>base-immutable</c>), same rule text, both naming the recorded value and the attempted
/// one — even though they are structurally separate nested cases, the same "every outcome union
/// owns its own guard cases" shape <see cref="CardGateResultOutcome"/> and every sibling union in
/// this codebase already follow for <see cref="NotABlockCard"/>/<see cref="RoundDisagreesWithHistory"/>/<see
/// cref="HandEnteredDerivedState"/>. The remedy text differs only because the calling context does:
/// <see cref="CardBlockTransitionOutcome.BaseImmutable"/>'s own remedy ("omit <c>--base</c> to keep
/// it") answers a transition where <c>--base</c> is optional; this verb's <c>--base</c> is the
/// whole point of the call, so there is nothing to omit.
/// </para>
///
/// <para>
/// <b>The condition matches exactly, not only the code and rule text (Product Owner ruling,
/// remediation on 13.3).</b> The requirement prose says base SHALL NOT <em>change</em> once
/// recorded, twice — an identical resupply changes nothing, so <see cref="BaseImmutable"/> fires
/// only when the attempted value genuinely differs from the recorded one, the same mismatch-only
/// condition <see cref="CardStore.ApplyBlockTransitionUnderExistingLock"/> already applies to its
/// own <c>--base</c>. A retry that resupplies exactly what is already recorded is retry-safe: it
/// succeeds, the recorded value is unchanged, and nothing is refused or recorded against the card
/// — an agent that cannot tell whether an earlier call landed can ask again without being punished
/// for asking.
/// </para>
/// </summary>
internal abstract record CardBlockRecordBaseOutcome
{
    private CardBlockRecordBaseOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Recorded, TResult> onRecorded,
        Func<NotABlockCard, TResult> onNotABlockCard,
        Func<NotAtDrafting, TResult> onNotAtDrafting,
        Func<BaseImmutable, TResult> onBaseImmutable,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState,
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="Card">The card as written, carrying the newly recorded <c>base</c>.</param>
    /// <param name="Base">The commit recorded.</param>
    /// <param name="ActingRole">The role that recorded it — not persisted on the card (work-
    /// lifecycle only requires <c>base</c> itself), but required here so a caller mapping this
    /// outcome to a CLI result can report who recorded it, the same "carried on the outcome, not
    /// re-derived" shape <see cref="CardGateResultOutcome.Recorded.ActingRole"/> already has.</param>
    internal sealed record Recorded(CardFile Card, string Base, CardOwner ActingRole) : CardBlockRecordBaseOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<NotAtDrafting, TResult> onNotAtDrafting, Func<BaseImmutable, TResult> onBaseImmutable, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState, Func<ToolFailure, TResult> onToolFailure) =>
            onRecorded(this);
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>block</c> — a
    /// brief's base is only recorded on a block card. Refusal-shaped, card-addressed.</summary>
    internal sealed record NotABlockCard(CardKind Kind) : CardBlockRecordBaseOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<NotAtDrafting, TResult> onNotAtDrafting, Func<BaseImmutable, TResult> onBaseImmutable, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState, Func<ToolFailure, TResult> onToolFailure) =>
            onNotABlockCard(this);

        public string RefusingRule => "work-lifecycle: a brief's base only applies to a block card";

        public string Remedy => "target a card whose kind is 'block'.";
    }

    /// <summary>work-lifecycle: "Blocks carry their brief context" (Architect ruling item 3) — a
    /// base is recorded only while the card is still at <c>drafting</c>. After briefing, a base is
    /// already set by definition (<see cref="CardBlockTransitionOutcome.BaseNotRecorded"/> refuses
    /// the transition without one), so a late record could only ever attach a commit the brief was
    /// not carved against. Refusal-shaped, card-addressed.</summary>
    internal sealed record NotAtDrafting(BlockFlowState CurrentState) : CardBlockRecordBaseOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<NotAtDrafting, TResult> onNotAtDrafting, Func<BaseImmutable, TResult> onBaseImmutable, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState, Func<ToolFailure, TResult> onToolFailure) =>
            onNotAtDrafting(this);

        public string RefusingRule => "work-lifecycle: base is recorded only while a block is at 'drafting'";

        public string Remedy => $"this card is at '{CurrentState.ToWireString()}', not 'drafting'; base is recorded once, before the block is first briefed.";
    }

    /// <summary>work-lifecycle: "Once recorded, that command SHALL refuse to change it" — a base is
    /// already recorded against this card. See this type's own doc comment for why this is not a
    /// near-duplicate of <see cref="CardBlockTransitionOutcome.BaseImmutable"/> despite sharing its
    /// refusal code and rule text. Refusal-shaped, card-addressed.</summary>
    internal sealed record BaseImmutable(string RecordedBase, string AttemptedBase) : CardBlockRecordBaseOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<NotAtDrafting, TResult> onNotAtDrafting, Func<BaseImmutable, TResult> onBaseImmutable, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState, Func<ToolFailure, TResult> onToolFailure) =>
            onBaseImmutable(this);

        public string RefusingRule => "work-lifecycle: base SHALL NOT change across remediation rounds";

        public string Remedy => $"base is already recorded as '{RecordedBase}' and cannot be changed; 'block base' records it once, before the block is first briefed.";
    }

    /// <summary>No card exists at the target path. Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardBlockRecordBaseOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<NotAtDrafting, TResult> onNotAtDrafting, Func<BaseImmutable, TResult> onBaseImmutable, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState, Func<ToolFailure, TResult> onToolFailure) =>
            onCardNotFound(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>). Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardBlockRecordBaseOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<NotAtDrafting, TResult> onNotAtDrafting, Func<BaseImmutable, TResult> onBaseImmutable, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but could not be parsed. Neither refusal nor tool-failure.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardBlockRecordBaseOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<NotAtDrafting, TResult> onNotAtDrafting, Func<BaseImmutable, TResult> onBaseImmutable, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState, Func<ToolFailure, TResult> onToolFailure) =>
            onCardCorrupt(this);
    }

    /// <summary>work-lifecycle: "Stored round agrees with the transition history" (8a.17) — the
    /// same guard <see cref="CardGateResultOutcome.RoundDisagreesWithHistory"/> already applies to
    /// its own non-transition writer. Refusal-shaped, card-addressed.</summary>
    internal sealed record RoundDisagreesWithHistory(int StoredRound, int ExpectedRound) : CardBlockRecordBaseOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<NotAtDrafting, TResult> onNotAtDrafting, Func<BaseImmutable, TResult> onBaseImmutable, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState, Func<ToolFailure, TResult> onToolFailure) =>
            onRoundDisagreesWithHistory(this);

        public string RefusingRule => "work-lifecycle: stored round agrees with the transition history";

        public string Remedy =>
            $"the recorded round ({StoredRound}) disagrees with the transition history ({ExpectedRound}); " +
            "correct whichever was altered outside the tool before this base can be recorded.";
    }

    /// <summary>working-context: "No figure SHALL be hand-entered anywhere in the system" (§10
    /// block C) — the same guard every other write surface on <see cref="CardStore"/> applies.
    /// Refusal-shaped, card-addressed.</summary>
    internal sealed record HandEnteredDerivedState(string Key) : CardBlockRecordBaseOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<NotAtDrafting, TResult> onNotAtDrafting, Func<BaseImmutable, TResult> onBaseImmutable, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState, Func<ToolFailure, TResult> onToolFailure) =>
            onHandEnteredDerivedState(this);

        public string RefusingRule => "working-context: no figure shall be hand-entered";

        public string Remedy =>
            $"'{Key}' is a reserved derived-state field name; remove it from this card's frontmatter — " +
            "this state is derived at request time, never stored, and is available from 'callboard state'.";
    }

    /// <summary>Enforcement itself is unavailable — the lock could not be acquired, or the write
    /// failed after every check passed. Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardBlockRecordBaseOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<NotAtDrafting, TResult> onNotAtDrafting, Func<BaseImmutable, TResult> onBaseImmutable, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
