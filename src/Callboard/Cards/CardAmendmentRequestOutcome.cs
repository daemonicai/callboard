namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.RecordAmendmentRequest"/> (§8 block C remediation,
/// work-lifecycle: "`amendment-requested` is the architect deliberately reopening an approved
/// block" — the only route from <c>approved</c> back to work that is not a supervisor's
/// recurrence) can end. Its own type rather than a reuse of
/// <see cref="CardBlockTransitionOutcome"/>: this verb is role-bounded to <c>architect</c> — the
/// one fact <see cref="CardBlockTransitionOutcome"/>'s generic path never checks, since every
/// other edge on that table records the acting role rather than restricting it — so this type
/// carries its own <see cref="RoleNotPermitted"/> case, the same split
/// <see cref="CardApprovalOutcome"/> already uses for its own role-bounded verb.
/// <see cref="RoleNotPermitted"/>, <see cref="NotABlockCard"/>,
/// <see cref="CardNotFound"/>, <see cref="UndefinedTransition"/> and <see cref="LayoutMismatch"/>
/// are refusal-shaped (caller-correctable); <see cref="CardCorrupt"/> and <see cref="ToolFailure"/>
/// are not — a caller wired over this type (see <see cref="Callboard.Cli.CommandDispatcher.
/// RunBlockAmendmentRequested"/>) must route those two to a tool-failure exit, never a refusal.
/// </summary>
internal abstract record CardAmendmentRequestOutcome
{
    private CardAmendmentRequestOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Requested, TResult> onRequested,
        Func<RoleNotPermitted, TResult> onRoleNotPermitted,
        Func<NotABlockCard, TResult> onNotABlockCard,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<UndefinedTransition, TResult> onUndefinedTransition,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="Card">The card as written: status <c>briefed</c>, <c>round</c> incremented by
    /// one, the appended <see cref="CardBlockTransitionEntry"/> for <c>amendment-requested</c>.
    /// Nothing else on the card changes — <c>reviewed_state</c>, claims and limits are left
    /// exactly as they were, the same way every other route back to <c>briefed</c> leaves them.</param>
    internal sealed record Requested(CardFile Card) : CardAmendmentRequestOutcome
    {
        internal override TResult Match<TResult>(Func<Requested, TResult> onRequested, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onRequested(this);
    }

    /// <summary>work-lifecycle: "`amendment-requested` is the architect deliberately reopening an
    /// approved block" — the only role permitted to invoke this verb.</summary>
    internal sealed record RoleNotPermitted(CardOwner AttemptedRole) : CardAmendmentRequestOutcome
    {
        internal override TResult Match<TResult>(Func<Requested, TResult> onRequested, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onRoleNotPermitted(this);
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>block</c>.
    /// Refusal-shaped: caller pointed the verb at the wrong card.</summary>
    internal sealed record NotABlockCard(CardKind Kind) : CardAmendmentRequestOutcome
    {
        internal override TResult Match<TResult>(Func<Requested, TResult> onRequested, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotABlockCard(this);
    }

    /// <summary>No card exists at the target path. Refusal-shaped: caller-correctable.</summary>
    internal sealed record CardNotFound(string FilePath) : CardAmendmentRequestOutcome
    {
        internal override TResult Match<TResult>(Func<Requested, TResult> onRequested, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardNotFound(this);
    }

    /// <summary>The card's current <see cref="BlockFlowState"/> carries no <c>amendment-requested</c>
    /// edge (only <see cref="BlockFlowState.Approved"/> does) — read from <see cref="
    /// BlockFlowTransitions.AvailableFrom"/>, the one table this type and every other transition
    /// caller reads rather than a second hand-maintained list of the same facts.</summary>
    internal sealed record UndefinedTransition(BlockFlowState CurrentState, IReadOnlyList<BlockFlowTransition> Available) : CardAmendmentRequestOutcome
    {
        internal override TResult Match<TResult>(Func<Requested, TResult> onRequested, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onUndefinedTransition(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>). Refusal-shaped: caller-correctable.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardAmendmentRequestOutcome
    {
        internal override TResult Match<TResult>(Func<Requested, TResult> onRequested, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but its content could not be parsed, or carries a <c>status</c>
    /// this build does not recognise as a <see cref="BlockFlowState"/>. Neither refusal nor
    /// tool-failure — a reported problem with the record's own content. A caller wired over this
    /// type must not route it to a refusal exit.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardAmendmentRequestOutcome
    {
        internal override TResult Match<TResult>(Func<Requested, TResult> onRequested, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardCorrupt(this);
    }

    /// <summary>Enforcement itself is unavailable: the card's lock could not be acquired within its
    /// timeout, or an I/O error occurred while writing. Tool-failure-shaped — the board is not
    /// refusing anything. A caller wired over this type must let it reach a tool-failure exit
    /// (ADR-0001).</summary>
    internal sealed record ToolFailure(string Reason) : CardAmendmentRequestOutcome
    {
        internal override TResult Match<TResult>(Func<Requested, TResult> onRequested, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
