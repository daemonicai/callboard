namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.DispositionNit"/> (§8 block B, <c>nit disposition</c>)
/// can end. Same refusal/tool-failure/reported-failure discipline every other outcome type in this
/// codebase applies — see <see cref="CardApprovalOutcome"/>'s own doc comment for the general
/// shape. <see cref="RoleNotPermitted"/>, <see cref="NitNotFound"/>, <see cref="AlreadyDispositioned"/>,
/// <see cref="NotABlockCard"/>, <see cref="CardNotFound"/>, <see cref="LayoutMismatch"/>,
/// <see cref="RaisedCardLayoutMismatch"/> and <see cref="RaisedCardAlreadyExists"/> are
/// refusal-shaped (caller-correctable); <see cref="CardCorrupt"/> and <see cref="ToolFailure"/> are
/// not — a caller wired over this type (<see cref="Callboard.Cli.CommandDispatcher.RunNitDisposition"/>)
/// must route those two to a tool-failure exit, never a refusal.
/// </summary>
internal abstract record CardNitDispositionOutcome
{
    private CardNitDispositionOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Dispositioned, TResult> onDispositioned,
        Func<RoleNotPermitted, TResult> onRoleNotPermitted,
        Func<NotABlockCard, TResult> onNotABlockCard,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<NitNotFound, TResult> onNitNotFound,
        Func<AlreadyDispositioned, TResult> onAlreadyDispositioned,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch,
        Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState);

    /// <param name="Card">The block card as written: the appended disposition comment, and — for
    /// <c>fix-before-land</c>, only when this call is the one that leaves it live in <c>in-review</c>
    /// with every other nit already dispositioned — the new status, <c>round</c> and appended
    /// <see cref="CardBlockTransitionEntry"/>.</param>
    /// <param name="DispositionComment">The disposition comment recorded by this call.</param>
    /// <param name="RaisedCard">The card raised alongside the disposition, for <c>defer</c>/
    /// <c>decline</c>; <see langword="null"/> for <c>fix-before-land</c>.</param>
    /// <param name="Transitioned"><see langword="true"/> when this call also applied the
    /// <c>fix-before-land</c> edge (work-lifecycle: "<c>in-review → briefed</c> …
    /// <c>round += 1</c>"). See <see cref="CardStore.DispositionNit"/>'s own doc comment for when
    /// that is and is not the case.</param>
    internal sealed record Dispositioned(CardFile Card, CardComment DispositionComment, CardFile? RaisedCard, string? RaisedCardFilePath, bool Transitioned) : CardNitDispositionOutcome
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onDispositioned(this);
    }

    /// <summary>review-certification: "Every nit SHALL receive a disposition chosen by the
    /// architect" — the Architect's reading of "chosen by the architect" as role-bounding the verb
    /// (§8 block B brief item 6, the reading most open to challenge).</summary>
    internal sealed record RoleNotPermitted(CardOwner AttemptedRole) : CardNitDispositionOutcome
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onRoleNotPermitted(this);
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>block</c>.
    /// Refusal-shaped: caller pointed the verb at the wrong card. Card-addressed (§9 block A3):
    /// resolved under lock before this check runs.</summary>
    internal sealed record NotABlockCard(CardKind Kind) : CardNitDispositionOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onNotABlockCard(this);

        public string RefusingRule => "review-certification: nit dispositions only apply to a block card";

        public string Remedy => "target a card whose kind is 'block'.";
    }

    /// <summary>No card exists at the target path. Refusal-shaped: caller-correctable.</summary>
    internal sealed record CardNotFound(string FilePath) : CardNitDispositionOutcome
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardNotFound(this);
    }

    /// <summary>The block card was found, but no comment on it carries <c>NitId</c> as a live nit —
    /// checked again under the lock (<see cref="NitResolver"/> found it before the lock was
    /// acquired). Refusal-shaped: caller-correctable. Card-addressed (§9 block A3): the block card
    /// is resolved before this check runs.</summary>
    internal sealed record NitNotFound(string NitId) : CardNitDispositionOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onNitNotFound(this);

        public string RefusingRule => "review-certification: a disposition targets a live nit on the card";

        public string Remedy => $"'{NitId}' is not a live nit on this card; raise it first, or correct the id.";
    }

    /// <summary>The nit already carries a disposition (review-certification: "A nit SHALL cease to
    /// be live only through one of these three dispositions" — implying exactly one). Refusal-
    /// shaped: caller-correctable. Card-addressed (§9 block A3): the block card is resolved and the
    /// nit's own disposition already read before this check runs.</summary>
    internal sealed record AlreadyDispositioned(string NitId) : CardNitDispositionOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onAlreadyDispositioned(this);

        public string RefusingRule => "review-certification: a nit ceases to be live only through one of its three dispositions";

        public string Remedy => $"nit '{NitId}' already carries a disposition; it cannot be dispositioned again.";
    }

    /// <summary>The block card's own target path does not resolve under the given root/scope/change
    /// name. Refusal-shaped: caller-correctable.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardNitDispositionOutcome
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onLayoutMismatch(this);
    }

    /// <summary>The raised (obligation/decision) card's own target path does not resolve under the
    /// given root/scope/change name. Refusal-shaped: caller-correctable.</summary>
    internal sealed record RaisedCardLayoutMismatch(string Reason) : CardNitDispositionOutcome
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onRaisedCardLayoutMismatch(this);
    }

    /// <summary>A card already exists at the raised card's target path. Refusal-shaped: caller-
    /// correctable. Card-addressed (§9 block A3), against the block card the disposition targets —
    /// not the colliding path, which is never read or parsed here (only <see cref="File.Exists(
    /// string)"/> checked) and so has nothing of its own to record against, the same "already
    /// exists" reasoning <see cref="CardRulePromoteOutcome.TargetAlreadyExists"/> already applies
    /// against its own source card. The block card is already resolved and anchored by the time
    /// this check runs.</summary>
    internal sealed record RaisedCardAlreadyExists(string FilePath) : CardNitDispositionOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onRaisedCardAlreadyExists(this);

        public string RefusingRule => "card-model: identities are never recycled, and a raised card must not overwrite an unrelated one";

        public string Remedy => $"'{FilePath}' already exists at the raised card's target path; resolve the collision before retrying.";
    }

    /// <summary>The card exists but its content could not be parsed, or carries a <c>status</c> this
    /// build does not recognise. Neither refusal nor tool-failure.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardNitDispositionOutcome
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardCorrupt(this);
    }

    /// <summary>work-lifecycle: "Stored round agrees with the transition history" (8a.17) — the
    /// block card's stored <c>round</c> does not equal one plus the number of round-incrementing
    /// transitions (<see cref="BlockFlowTransitions.RoundIncrementingTransitionNames"/>) in its own
    /// <see cref="CardFile.Transitions"/> history. Refusal-shaped: neither figure is privileged and
    /// neither is altered — a stored count ahead of the history and a history ahead of the count are
    /// different failures, and guessing which is right would silently destroy the evidence of
    /// whichever was correct.</summary>
    internal sealed record RoundDisagreesWithHistory(int StoredRound, int ExpectedRound) : CardNitDispositionOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onRoundDisagreesWithHistory(this);

        public string RefusingRule => "work-lifecycle: stored round agrees with the transition history";

        public string Remedy =>
            $"the recorded round ({StoredRound}) disagrees with the transition history ({ExpectedRound}); " +
            "correct whichever was altered outside the tool before this disposition can proceed.";
    }

    /// <summary>process-enforcement: "A verdict cannot leave threads unanswered" — this disposition
    /// would apply the <c>fix-before-land</c> edge (leaving <c>in-review</c>: see <see cref="
    /// CardStore.DispositionNitUnderLocks"/>'s own doc comment on when that is and is not the case)
    /// while the card carries at least one live thread (<see cref="CardCommentRouting.
    /// LiveThreadIdsAddressedTo"/>) addressed to <paramref name="ActorRole"/> — always <see
    /// cref="CardOwner.Architect"/> here, the only role <see cref="CardStore.IsArchitectRole"/>
    /// admits to this verb. The whole call is refused before any write, including the raised card
    /// for a <c>defer</c>/<c>decline</c> disposition that happens to be the one emptying the live
    /// nit set this round: a refusal must prevent the side effect it refuses, not merely follow it
    /// (ADR-0001), and here the side effect includes the disposition itself, not only the
    /// transition — the nit stays live and undispositioned until the thread is answered and the
    /// call is retried. Refusal-shaped and card-addressed.</summary>
    internal sealed record UnresolvedThreadsAddressedToActor(CardOwner ActorRole, IReadOnlyList<string> ThreadIds) : CardNitDispositionOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onUnresolvedThreadsAddressedToActor(this);

        public string RefusingRule => "process-enforcement: a verdict cannot leave threads unanswered";

        public string Remedy =>
            $"resolve the following thread(s) addressed to '{ActorRole.ToWireString()}' (with 'comment resolve') before " +
            $"this disposition can proceed: {string.Join(", ", ThreadIds)}.";
    }

    /// <summary>working-context: "No figure SHALL be hand-entered anywhere in the system" (§10
    /// block C) — <paramref name="Key"/> names a reserved derived-state field (<see
    /// cref="DerivedStateFieldKeys.All"/>) present on the target card's <see cref="CardFile.
    /// UnknownFrontmatterFields"/>, the door a hand-edited card's frontmatter uses to reach this far
    /// at all (nothing this build's own CLI ever writes one). Refusal-shaped, card-addressed (§9
    /// block A3): checked immediately once the card is read, before dispositioning the nit is allowed to
    /// proceed, so this write never re-emits (and never launders forward) a hand-entered count or
    /// next-step pin it did not itself write. See <see cref="CardWriteResult.HandEnteredDerivedState"/>
    /// for the sibling case on the generic comment/handover surface.</summary>
    internal sealed record HandEnteredDerivedState(string Key) : CardNitDispositionOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onHandEnteredDerivedState(this);

        public string RefusingRule => "working-context: no figure shall be hand-entered";

        public string Remedy =>
            $"'{Key}' is a reserved derived-state field name; remove it from this card's frontmatter — " +
            "this state is derived at request time, never stored, and is available from 'callboard state'.";
    }

    /// <summary>Enforcement itself is unavailable: a lock could not be acquired within its timeout,
    /// or an I/O error occurred while writing. Tool-failure-shaped — the board is not refusing
    /// anything.</summary>
    internal sealed record ToolFailure(string Reason) : CardNitDispositionOutcome
    {
        internal override TResult Match<TResult>(Func<Dispositioned, TResult> onDispositioned, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NitNotFound, TResult> onNitNotFound, Func<AlreadyDispositioned, TResult> onAlreadyDispositioned, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onToolFailure(this);
    }
}
