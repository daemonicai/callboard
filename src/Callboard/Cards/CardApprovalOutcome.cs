namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.RecordApproval"/> (§8 block A, <c>block approve</c>)
/// can end. Deliberately its own type rather than a reuse of <see cref="CardBlockTransitionOutcome"/>:
/// an approval is not a generic transition — it stamps <c>reviewed_state</c> and appends the
/// enumerated claim/limit sequence in the same write as the state change (Architect ruling: "the
/// certification is stamped in the same write as the transition", "one door, and the certification
/// is stamped in the same write as the transition"), and it carries its own role restriction
/// (review-certification: "Approval is role-bounded") that no other transition-applying verb does.
/// Same refusal/tool-failure/reported-failure discipline <see cref="CardBlockTransitionOutcome"/>'s
/// own doc comment establishes: <see cref="RoleNotPermitted"/>, <see cref="UndefinedTransition"/>,
/// <see cref="UndispositionedNits"/>, <see cref="NotABlockCard"/>, <see cref="CardNotFound"/>,
/// <see cref="LayoutMismatch"/>, <see cref="RoundDisagreesWithHistory"/> and
/// <see cref="UnresolvedThreadsAddressedToActor"/> are refusal-shaped (caller-correctable);
/// <see cref="CardCorrupt"/> and <see cref="ToolFailure"/> are not — a caller wired over this type
/// (<see cref="Callboard.Cli.CommandDispatcher.RunBlockApprove"/>) must route those two to a
/// tool-failure exit, never a refusal.
///
/// <para>
/// <b>Every refusal-shaped case implements <see cref="ICardRefusalReason"/> and records</b> — even
/// <see cref="RoleNotPermitted"/> (§9 block B, reviewer/architect ruling, overruling this block's own
/// first pass): unlike the identically-shaped pre-lock role checks in <see cref="
/// CardNitDispositionOutcome.RoleNotPermitted"/> and <c>CardRuleCompactOutcome.RoleNotPermitted</c>,
/// <see cref="CardStore.RecordApprovalUnderExistingLock"/>'s one lock is already held regardless of
/// where the role check sits, so there is no real cost to checking it after a successful <see
/// cref="CardStore.ReadCard"/> instead of before — and a pattern of wrong-role approval attempts is
/// exactly the pattern process-enforcement's "so that a pattern of refusals is itself visible" exists
/// to catch.
/// </para>
/// </summary>
internal abstract record CardApprovalOutcome
{
    private CardApprovalOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Approved, TResult> onApproved,
        Func<RoleNotPermitted, TResult> onRoleNotPermitted,
        Func<UndefinedTransition, TResult> onUndefinedTransition,
        Func<UndispositionedNits, TResult> onUndispositionedNits,
        Func<NotABlockCard, TResult> onNotABlockCard,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion);

    /// <param name="Card">The card as written: status <c>approved</c>, the stamped
    /// <c>reviewed_state</c>, and the appended <see cref="CardBlockTransitionEntry"/>.</param>
    /// <param name="Claims">The claims recorded by this approval, in the order given.</param>
    /// <param name="Limits">The limits recorded by this approval, in the order given.</param>
    internal sealed record Approved(CardFile Card, IReadOnlyList<CardApprovalClaim> Claims, IReadOnlyList<CardApprovalLimit> Limits) : CardApprovalOutcome
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion) =>
            onApproved(this);
    }

    /// <summary>review-certification: "Only the reviewer and supervisor roles SHALL record an
    /// approve verdict" (8.13, this block ships the refusal half — see the §8 block A brief for why
    /// 8.13 itself ticks in block C). Card-addressed (§9 block B, reviewer/architect ruling
    /// overruling this block's own first pass): checked immediately after a successful
    /// <see cref="CardStore.ReadCard"/>, not ahead of <see cref="File.Exists(string)"/> the way
    /// <see cref="CardRuleCompactOutcome.RoleNotPermitted"/> and <see cref="
    /// CardNitDispositionOutcome.RoleNotPermitted"/> check role — neither of those methods' reasons
    /// for checking early (an N+1 lock loop; an identity allocation plus lock acquisition) applies
    /// to <see cref="CardStore.RecordApprovalUnderExistingLock"/>, whose one lock is already held
    /// regardless of where the check sits. Recording this one matters more than most: a pattern of
    /// wrong-role approval attempts — an architect repeatedly attempting to approve its own work —
    /// is exactly the pattern process-enforcement's "so that a pattern of refusals is itself
    /// visible" exists to catch.</summary>
    internal sealed record RoleNotPermitted(CardOwner AttemptedRole) : CardApprovalOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion) =>
            onRoleNotPermitted(this);

        public string RefusingRule => "review-certification: approval is role-bounded";

        public string Remedy => $"only 'reviewer' or 'supervisor' may record an approval; '{AttemptedRole.ToWireString()}' attempted it.";
    }

    /// <param name="CurrentState">The state the card was actually in when the approval was
    /// attempted — <c>approve</c> is only a legal edge from <c>in-review</c>.</param>
    /// <param name="Available">The transitions legally available from <paramref name="CurrentState"/>
    /// — read from <see cref="BlockFlowTransitions.AvailableFrom"/>, the raw edge table (unlike
    /// <see cref="CardBlockTransitionOutcome.UndefinedTransition"/>, which since the §8a remediation
    /// reads <see cref="BlockFlowTransitions.GenericallyInvocableFrom"/> instead): <c>approve</c> is
    /// always this card's own dedicated door regardless of which edges a generic <c>block
    /// transition</c> could itself drive, so the full table is the honest answer to "what else is
    /// legal from here" here.</param>
    internal sealed record UndefinedTransition(BlockFlowState CurrentState, IReadOnlyList<BlockFlowTransition> Available) : CardApprovalOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion) =>
            onUndefinedTransition(this);

        public string RefusingRule => "work-lifecycle: block cards move through a defined flow";

        public string Remedy => Available.Count == 0
            ? $"no transition is available from '{CurrentState.ToWireString()}'."
            : $"call one of the transitions available from '{CurrentState.ToWireString()}': {string.Join(", ", Available.Select(static t => t.Name))}.";
    }

    /// <summary>review-certification: "Undispositioned nits block the verdict" (§8 block B) — an
    /// approval is one of the transitions "moved out of in-review" that requirement binds
    /// (<c>approve</c> is the other exit block A shipped; <c>changes-requested</c> is
    /// <see cref="CardBlockTransitionOutcome.UndispositionedNits"/>'s own case).</summary>
    internal sealed record UndispositionedNits(IReadOnlyList<string> NitIds) : CardApprovalOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion) =>
            onUndispositionedNits(this);

        public string RefusingRule => "review-certification: undispositioned nits block the verdict";

        public string Remedy => $"disposition the following nit(s) before this transition: {string.Join(", ", NitIds)}.";
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>block</c>.
    /// Refusal-shaped: caller pointed the verb at the wrong card.</summary>
    internal sealed record NotABlockCard(CardKind Kind) : CardApprovalOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion) =>
            onNotABlockCard(this);

        public string RefusingRule => "review-certification: approval only applies to a block card";

        public string Remedy => "target a card whose kind is 'block'.";
    }

    /// <summary>No card exists at the target path. Refusal-shaped: caller-correctable.</summary>
    internal sealed record CardNotFound(string FilePath) : CardApprovalOutcome
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion) =>
            onCardNotFound(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>). Refusal-shaped: caller-correctable.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardApprovalOutcome
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but its content could not be parsed, or carries a <c>status</c> this
    /// build does not recognise as a <see cref="BlockFlowState"/>. Neither refusal nor tool-failure —
    /// a reported problem with the record's own content. A caller wired over this type must not
    /// route it to a refusal exit.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardApprovalOutcome
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion) =>
            onCardCorrupt(this);
    }

    /// <summary>work-lifecycle: "Stored round agrees with the transition history" (8a.17) — the
    /// block card's stored <c>round</c> does not equal one plus the number of round-incrementing
    /// transitions (<see cref="BlockFlowTransitions.RoundIncrementingTransitionNames"/>) in its own
    /// <see cref="CardFile.Transitions"/> history. Refusal-shaped: neither figure is privileged and
    /// neither is altered — a stored count ahead of the history and a history ahead of the count are
    /// different failures, and guessing which is right would silently destroy the evidence of
    /// whichever was correct.</summary>
    internal sealed record RoundDisagreesWithHistory(int StoredRound, int ExpectedRound) : CardApprovalOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion) =>
            onRoundDisagreesWithHistory(this);

        public string RefusingRule => "work-lifecycle: stored round agrees with the transition history";

        public string Remedy =>
            $"the recorded round ({StoredRound}) disagrees with the transition history ({ExpectedRound}); " +
            "correct whichever was altered outside the tool before this transition can proceed.";
    }

    /// <summary>process-enforcement: "A verdict cannot leave threads unanswered" — <paramref
    /// name="ActorRole"/> attempted an <c>approve</c> verdict while the card carries at least one
    /// live thread (<see cref="CardCommentRouting.LiveThreadIdsAddressedTo"/>) addressed to that
    /// same role. A thread addressed to a <em>different</em> role does not block this — the
    /// requirement binds the acting role's own inbox, not every open thread on the card (§9 block B
    /// DEVLOG post: the reviewer is not the architect's postbox). Refusal-shaped and card-addressed:
    /// fires after the card is read and the nit/round checks above have already passed.</summary>
    internal sealed record UnresolvedThreadsAddressedToActor(CardOwner ActorRole, IReadOnlyList<string> ThreadIds) : CardApprovalOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion) =>
            onUnresolvedThreadsAddressedToActor(this);

        public string RefusingRule => "process-enforcement: a verdict cannot leave threads unanswered";

        public string Remedy =>
            $"resolve the following thread(s) addressed to '{ActorRole.ToWireString()}' (with 'comment resolve') before " +
            $"recording this verdict: {string.Join(", ", ThreadIds)}.";
    }

    /// <summary>process-enforcement: "Work cannot proceed past a stop-and-ask" (§9 block D, 9.8) —
    /// the card names <paramref name="QuestionId"/>, still <see cref="QuestionStatus.Open"/> and
    /// owned by <see cref="CardOwner.ProductOwner"/>, among its own <see cref="BlockCardFields.
    /// BlockedBy"/>. <c>approve</c> is always a forward transition — unlike <see cref="
    /// CardBlockTransitionOutcome"/>'s own copy of this case, there is no back-edge arm on this
    /// surface to exempt (Architect ruling, §9 block D). Refusal-shaped and card-addressed: fires
    /// after the nit/round/thread checks above have already passed — recorded.</summary>
    internal sealed record BlockedByOpenProductOwnerQuestion(string QuestionId, string QuestionTitle) : CardApprovalOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion) =>
            onBlockedByOpenProductOwnerQuestion(this);

        public string RefusingRule => "process-enforcement: work cannot proceed past a stop-and-ask";

        public string Remedy => $"question '{QuestionId}' (\"{QuestionTitle}\") is open and owned by the product owner; get it answered or deferred before this approval can be recorded.";
    }

    /// <summary>Enforcement itself is unavailable: the card's lock could not be acquired within its
    /// timeout, or an I/O error occurred while writing. Tool-failure-shaped — the board is not
    /// refusing anything. A caller wired over this type must let it reach a tool-failure exit
    /// (ADR-0001).</summary>
    internal sealed record ToolFailure(string Reason) : CardApprovalOutcome
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<UnresolvedThreadsAddressedToActor, TResult> onUnresolvedThreadsAddressedToActor,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion) =>
            onToolFailure(this);
    }
}
