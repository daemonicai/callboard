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
/// <see cref="NotABlockCard"/>, <see cref="CardNotFound"/> and <see cref="LayoutMismatch"/> are
/// refusal-shaped (caller-correctable); <see cref="CardCorrupt"/> and <see cref="ToolFailure"/> are
/// not — a caller wired over this type (<see cref="Callboard.Cli.CommandDispatcher.RunBlockApprove"/>)
/// must route those two to a tool-failure exit, never a refusal.
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
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory);

    /// <param name="Card">The card as written: status <c>approved</c>, the stamped
    /// <c>reviewed_state</c>, and the appended <see cref="CardBlockTransitionEntry"/>.</param>
    /// <param name="Claims">The claims recorded by this approval, in the order given.</param>
    /// <param name="Limits">The limits recorded by this approval, in the order given.</param>
    internal sealed record Approved(CardFile Card, IReadOnlyList<CardApprovalClaim> Claims, IReadOnlyList<CardApprovalLimit> Limits) : CardApprovalOutcome
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onApproved(this);
    }

    /// <summary>review-certification: "Only the reviewer and supervisor roles SHALL record an
    /// approve verdict" (8.13, this block ships the refusal half — see the §8 block A brief for why
    /// 8.13 itself ticks in block C).</summary>
    internal sealed record RoleNotPermitted(CardOwner AttemptedRole) : CardApprovalOutcome
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onRoleNotPermitted(this);
    }

    /// <param name="CurrentState">The state the card was actually in when the approval was
    /// attempted — <c>approve</c> is only a legal edge from <c>in-review</c>.</param>
    /// <param name="Available">The transitions legally available from <paramref name="CurrentState"/>
    /// — read from <see cref="BlockFlowTransitions.AvailableFrom"/>, the same table
    /// <see cref="CardBlockTransitionOutcome.UndefinedTransition"/> reads.</param>
    internal sealed record UndefinedTransition(BlockFlowState CurrentState, IReadOnlyList<BlockFlowTransition> Available) : CardApprovalOutcome
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onUndefinedTransition(this);
    }

    /// <summary>review-certification: "Undispositioned nits block the verdict" (§8 block B) — an
    /// approval is one of the transitions "moved out of in-review" that requirement binds
    /// (<c>approve</c> is the other exit block A shipped; <c>changes-requested</c> is
    /// <see cref="CardBlockTransitionOutcome.UndispositionedNits"/>'s own case).</summary>
    internal sealed record UndispositionedNits(IReadOnlyList<string> NitIds) : CardApprovalOutcome
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onUndispositionedNits(this);
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>block</c>.
    /// Refusal-shaped: caller pointed the verb at the wrong card.</summary>
    internal sealed record NotABlockCard(CardKind Kind) : CardApprovalOutcome
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onNotABlockCard(this);
    }

    /// <summary>No card exists at the target path. Refusal-shaped: caller-correctable.</summary>
    internal sealed record CardNotFound(string FilePath) : CardApprovalOutcome
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onCardNotFound(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>). Refusal-shaped: caller-correctable.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardApprovalOutcome
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but its content could not be parsed, or carries a <c>status</c> this
    /// build does not recognise as a <see cref="BlockFlowState"/>. Neither refusal nor tool-failure —
    /// a reported problem with the record's own content. A caller wired over this type must not
    /// route it to a refusal exit.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardApprovalOutcome
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onCardCorrupt(this);
    }

    /// <summary>work-lifecycle: "Stored round agrees with the transition history" (8a.17) — the
    /// block card's stored <c>round</c> does not equal one plus the number of round-incrementing
    /// transitions (<see cref="BlockFlowTransitions.RoundIncrementingTransitionNames"/>) in its own
    /// <see cref="CardFile.Transitions"/> history. Refusal-shaped: neither figure is privileged and
    /// neither is altered — a stored count ahead of the history and a history ahead of the count are
    /// different failures, and guessing which is right would silently destroy the evidence of
    /// whichever was correct.</summary>
    internal sealed record RoundDisagreesWithHistory(int StoredRound, int ExpectedRound) : CardApprovalOutcome
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onRoundDisagreesWithHistory(this);
    }

    /// <summary>Enforcement itself is unavailable: the card's lock could not be acquired within its
    /// timeout, or an I/O error occurred while writing. Tool-failure-shaped — the board is not
    /// refusing anything. A caller wired over this type must let it reach a tool-failure exit
    /// (ADR-0001).</summary>
    internal sealed record ToolFailure(string Reason) : CardApprovalOutcome
    {
        internal override TResult Match<TResult>(Func<Approved, TResult> onApproved, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onToolFailure(this);
    }
}
