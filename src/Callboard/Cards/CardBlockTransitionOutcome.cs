namespace Callboard.Cards;

/// <summary>
/// Closed union over how applying a block flow transition (5.2, 5.3, 5.5) can end. Distinct from
/// <see cref="CardWriteResult"/> — that type only distinguishes success from an opaque string
/// failure, which is enough for <see cref="CardStore.AppendComment"/> and
/// <see cref="CardStore.TransferOwnership"/> because nothing downstream of them needs to tell
/// their failure reasons apart. A transition's caller does: the CLI boundary (5.2) has to mint a
/// distinct refusal code for an undefined transition versus a missing <c>base</c> versus an
/// attempt to change an already-recorded one, and modelling those as one more string would put the
/// classification back in prose a caller has to parse instead of a case a compiler can force
/// exhaustive handling of (ADR-0001: a refusal is a returned result).
///
/// <para>
/// <b>Refusal, tool-failure and reported-failure stay distinct cases (reviewer finding, first
/// remediation round):</b> the first shape shipped folded "no card at that path", "layout
/// mismatch", "corrupt card" and "lock timeout / I/O failure while writing" into one
/// <c>WriteFailed(string Reason)</c> case, which the CLI boundary then mapped, wholesale, to one
/// refusal code (<c>card-write-failed</c>). That inverted §3's own standing rule: refusal means
/// "stop, you are wrong"; tool-failure means "enforcement is unavailable, proceed unenforced"; a
/// corrupt card is neither. Telling a caller to stop when the truth is "the tool broke" is the
/// exact failure class this codebase exists to prevent. <see cref="CardNotFound"/> and
/// <see cref="LayoutMismatch"/> are refusal-shaped (caller-correctable); <see cref="CardCorrupt"/>
/// and <see cref="ToolFailure"/> are not — a caller wired over this type (see
/// <see cref="Callboard.Cli.CommandDispatcher.RunBlockTransition"/>) must route those two to a
/// tool-failure exit, never a refusal, or the same defect recurs one layer up.
/// </para>
/// </summary>
internal abstract record CardBlockTransitionOutcome
{
    private CardBlockTransitionOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Applied, TResult> onApplied,
        Func<UndefinedTransition, TResult> onUndefinedTransition,
        Func<BaseNotRecorded, TResult> onBaseNotRecorded,
        Func<BaseImmutable, TResult> onBaseImmutable,
        Func<UndispositionedNits, TResult> onUndispositionedNits,
        Func<NotABlockCard, TResult> onNotABlockCard,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState);

    /// <param name="Card">The card as written: new status, updated <c>base</c>/<c>round</c>, and
    /// the appended <see cref="CardBlockTransitionEntry"/>.</param>
    /// <param name="Transition">The edge that was applied.</param>
    internal sealed record Applied(CardFile Card, BlockFlowTransition Transition) : CardBlockTransitionOutcome
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onApplied(this);
    }

    /// <param name="CurrentState">The state the card was actually in when the transition was
    /// attempted.</param>
    /// <param name="Available">The transitions a bare <c>block transition</c> call may itself drive
    /// from <paramref name="CurrentState"/> — read from <see cref="BlockFlowTransitions.
    /// GenericallyInvocableFrom"/>, not <see cref="BlockFlowTransitions.AvailableFrom"/> (§8a
    /// remediation): the wider table can carry an edge (e.g. <c>finding-recurred</c>) that is legal
    /// from this state but reached only through its own dedicated door, and naming it here would
    /// advertise a door this same refusal would then itself refuse.</param>
    internal sealed record UndefinedTransition(BlockFlowState CurrentState, IReadOnlyList<BlockFlowTransition> Available) : CardBlockTransitionOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onUndefinedTransition(this);

        public string RefusingRule => "work-lifecycle: block cards move through a defined flow";

        public string Remedy => Available.Count == 0
            ? $"no transition is available from '{CurrentState.ToWireString()}'."
            : $"call one of the transitions available from '{CurrentState.ToWireString()}': {string.Join(", ", Available.Select(static t => t.Name))}.";
    }

    /// <summary>work-lifecycle: "base SHALL be recorded before the block is briefed" — neither
    /// already recorded on the card nor supplied with this call.</summary>
    internal sealed record BaseNotRecorded : CardBlockTransitionOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onBaseNotRecorded(this);

        public string RefusingRule => "work-lifecycle: base SHALL be recorded before the block is briefed";

        public string Remedy => "pass --base with the commit this block was carved against, or record one before briefing.";
    }

    /// <summary>work-lifecycle: "SHALL NOT change across remediation rounds" — a caller supplied a
    /// <c>base</c> that disagrees with the one already recorded on the card.</summary>
    internal sealed record BaseImmutable(string Recorded, string Attempted) : CardBlockTransitionOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onBaseImmutable(this);

        public string RefusingRule => "work-lifecycle: base SHALL NOT change across remediation rounds";

        public string Remedy => $"use the recorded base '{Recorded}', or omit --base to keep it.";
    }

    /// <summary>review-certification: "Undispositioned nits block the verdict" (§8 block B) —
    /// <c>changes-requested</c> is one of the transitions "moved out of in-review" that requirement
    /// binds; <c>approve</c> is <see cref="CardApprovalOutcome.UndispositionedNits"/>'s own case,
    /// and <c>fix-before-land</c> is refused at parse before it ever reaches this table
    /// (<see cref="Callboard.Cli.CommandParser.ParseBlockTransition"/>).</summary>
    internal sealed record UndispositionedNits(IReadOnlyList<string> NitIds) : CardBlockTransitionOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onUndispositionedNits(this);

        public string RefusingRule => "review-certification: undispositioned nits block the verdict";

        public string Remedy => $"disposition the following nit(s) before this transition: {string.Join(", ", NitIds)}.";
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>block</c> — flow
    /// transitions are only defined for a block card. Refusal-shaped: caller pointed the verb at
    /// the wrong card.</summary>
    internal sealed record NotABlockCard(CardKind Kind) : CardBlockTransitionOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onNotABlockCard(this);

        public string RefusingRule => "work-lifecycle: block flow transitions apply only to a block card";

        public string Remedy => "target a card whose kind is 'block'.";
    }

    /// <summary>No card exists at the target path. Refusal-shaped: caller-correctable, same class
    /// as <c>repo-root-not-found</c>.</summary>
    internal sealed record CardNotFound(string FilePath) : CardBlockTransitionOutcome
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardNotFound(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>). Refusal-shaped: caller-correctable.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardBlockTransitionOutcome
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but its content could not be parsed, or (specifically for a block
    /// card) carries a <c>status</c> this build does not recognise as a <see cref="BlockFlowState"/>.
    /// Neither refusal nor tool-failure — a reported problem with the record's own content
    /// (record-retrieval's degraded-mode requirement). A caller wired over this type must not
    /// route it to a refusal exit.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardBlockTransitionOutcome
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardCorrupt(this);
    }

    /// <summary>work-lifecycle: "Stored round agrees with the transition history" (8a.17) — the
    /// block card's stored <c>round</c> does not equal one plus the number of round-incrementing
    /// transitions (<see cref="BlockFlowTransitions.RoundIncrementingTransitionNames"/>) in its own
    /// <see cref="CardFile.Transitions"/> history. Refusal-shaped: neither figure is privileged and
    /// neither is altered — a stored count ahead of the history and a history ahead of the count are
    /// different failures, and guessing which is right would silently destroy the evidence of
    /// whichever was correct.</summary>
    internal sealed record RoundDisagreesWithHistory(int StoredRound, int ExpectedRound) : CardBlockTransitionOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onRoundDisagreesWithHistory(this);

        public string RefusingRule => "work-lifecycle: stored round agrees with the transition history";

        public string Remedy =>
            $"the recorded round ({StoredRound}) disagrees with the transition history ({ExpectedRound}); " +
            "correct whichever was altered outside the tool before this transition can proceed.";
    }

    /// <summary>process-enforcement: "A verdict cannot leave threads unanswered" — <paramref
    /// name="ActorRole"/> attempted <c>changes-requested</c>, the generic transition table's own
    /// door out of <c>in-review</c>, while the card carries at least one live thread (<see
    /// cref="CardCommentRouting.LiveThreadIdsAddressedTo"/>) addressed to that same role. A thread
    /// addressed to a different role does not block this (§9 block B DEVLOG post). Refusal-shaped
    /// and card-addressed: fires after the transition itself has already been resolved as legal,
    /// under the same lock, so it is checked only when <see cref="BlockFlowTransition.From"/> is
    /// <see cref="BlockFlowState.InReview"/> — the only case this generic applier can ever resolve
    /// from that state.</summary>
    internal sealed record UnresolvedThreadsAddressedToActor(CardOwner ActorRole, IReadOnlyList<string> ThreadIds) : CardBlockTransitionOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onUnresolvedThreadsAddressedToActor(this);

        public string RefusingRule => "process-enforcement: a verdict cannot leave threads unanswered";

        public string Remedy =>
            $"resolve the following thread(s) addressed to '{ActorRole.ToWireString()}' (with 'comment resolve') before " +
            $"recording this verdict: {string.Join(", ", ThreadIds)}.";
    }

    /// <summary>process-enforcement: "Work cannot proceed past a stop-and-ask" (§9 block D, 9.8) —
    /// the card names <paramref name="QuestionId"/>, not closed under <see cref="CardLifecycle.
    /// IsClosed"/> (open or deferred — §10 remediation, round two: deferring a Product Owner
    /// question does not lift the halt) and owned by <see cref="CardOwner.ProductOwner"/>, among
    /// its own <see cref="BlockCardFields.BlockedBy"/>. Fires only for a forward transition — never for <c>changes-requested</c>, the
    /// one back-edge this generic applier itself resolves (Architect ruling, §9 block D: back-edges
    /// return a card to earlier work, they do not advance it past the blocker; see
    /// <see cref="CardStore.ApplyBlockTransitionUnderExistingLock"/>'s own guard for the exact
    /// <see cref="BlockFlowTransitions.RoundIncrementingTransitionNames"/> test this reads).
    /// Refusal-shaped and card-addressed: fires after the transition itself has already been
    /// resolved as legal, under the same lock — recorded.</summary>
    internal sealed record BlockedByOpenProductOwnerQuestion(string QuestionId, string QuestionTitle) : CardBlockTransitionOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onBlockedByOpenProductOwnerQuestion(this);

        public string RefusingRule => "process-enforcement: work cannot proceed past a stop-and-ask";

        public string Remedy => $"question '{QuestionId}' (\"{QuestionTitle}\") is open and owned by the product owner; get it answered or deferred before this card advances.";
    }

    /// <summary>working-context: "No figure SHALL be hand-entered anywhere in the system" (§10
    /// block C) — <paramref name="Key"/> names a reserved derived-state field (<see
    /// cref="DerivedStateFieldKeys.All"/>) present on the target card's <see cref="CardFile.
    /// UnknownFrontmatterFields"/>, the door a hand-edited card's frontmatter uses to reach this far
    /// at all (nothing this build's own CLI ever writes one). Refusal-shaped, card-addressed (§9
    /// block A3): checked immediately once the card is read, before the transition is allowed to
    /// proceed, so this write never re-emits (and never launders forward) a hand-entered count or
    /// next-step pin it did not itself write. See <see cref="CardWriteResult.HandEnteredDerivedState"/>
    /// for the sibling case on the generic comment/handover surface.</summary>
    internal sealed record HandEnteredDerivedState(string Key) : CardBlockTransitionOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onHandEnteredDerivedState(this);

        public string RefusingRule => "working-context: no figure shall be hand-entered";

        public string Remedy =>
            $"'{Key}' is a reserved derived-state field name; remove it from this card's frontmatter — " +
            "this state is derived at request time, never stored, and is available from 'callboard state'.";
    }

    /// <summary>Enforcement itself is unavailable: the card's lock could not be acquired within its
    /// timeout, or an I/O error occurred while writing. Tool-failure-shaped — the board is not
    /// refusing anything. A caller wired over this type must let it reach a tool-failure exit
    /// (ADR-0001), the same route <c>index rebuild</c>'s SQLite I/O failures already take by
    /// letting the exception escape to <see cref="Callboard.Cli.CommandDispatcher.Run"/>'s own
    /// catch, never fold it into a refusal.</summary>
    internal sealed record ToolFailure(string Reason) : CardBlockTransitionOutcome
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onToolFailure(this);
    }
}
