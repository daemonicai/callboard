namespace Callboard.Cards;

/// <summary>
/// The exhaustive, total transition table for <see cref="BlockFlowState"/>
/// (work-lifecycle: "Block cards move through a defined flow"):
///
/// <code>
/// drafting ──▶ briefed ──▶ building ──▶ in-review ──┬──▶ approved ──▶ landed ──▶ closed
///                   ▲                               │
///                   └──── changes-requested ◀───────┘
///                             (round += 1)
/// </code>
///
/// <see cref="AvailableFrom"/> is the first-class query the brief asks for: what a caller — block
/// B's transition-applying code, or its refusal message — reads instead of restating the table.
/// It is total over every <see cref="BlockFlowState"/> because it is built with
/// <see cref="BlockFlowState.Match{TResult}"/>: every one of the seven cases supplies an arm, so
/// a case with no legal transitions (<c>closed</c>) has to say so explicitly (an empty list)
/// rather than the query silently falling through. Round application (the <c>round += 1</c> on
/// <c>changes-requested</c>) is block B's job — this table only says which edges exist and where
/// each one lands.
/// </summary>
internal static class BlockFlowTransitions
{
    private static readonly BlockFlowTransition Brief = new("brief", BlockFlowState.Drafting, BlockFlowState.Briefed);
    private static readonly BlockFlowTransition Claim = new("claim", BlockFlowState.Briefed, BlockFlowState.Building);
    private static readonly BlockFlowTransition SubmitForReview = new("submit-for-review", BlockFlowState.Building, BlockFlowState.InReview);
    private static readonly BlockFlowTransition Approve = new("approve", BlockFlowState.InReview, BlockFlowState.Approved);

    /// <summary>
    /// work-lifecycle's own name for the one edge its spec text names explicitly: the in-review
    /// block returns to briefed, and (block B's job) its <c>round</c> increments.
    /// </summary>
    private static readonly BlockFlowTransition ChangesRequested = new("changes-requested", BlockFlowState.InReview, BlockFlowState.Briefed);

    private static readonly BlockFlowTransition Land = new("land", BlockFlowState.Approved, BlockFlowState.Landed);
    private static readonly BlockFlowTransition Close = new("close", BlockFlowState.Landed, BlockFlowState.Closed);

    /// <summary>
    /// The transitions legally available from <paramref name="state"/> — empty only for
    /// <c>closed</c>, the flow's one terminal state. <c>in-review</c> is the one state with two:
    /// <c>approve</c> and <c>changes-requested</c>, both landing on a different destination.
    /// </summary>
    internal static IReadOnlyList<BlockFlowTransition> AvailableFrom(BlockFlowState state) => state.Match(
        onDrafting: static () => (IReadOnlyList<BlockFlowTransition>)[Brief],
        onBriefed: static () => [Claim],
        onBuilding: static () => [SubmitForReview],
        onInReview: static () => [Approve, ChangesRequested],
        onApproved: static () => [Land],
        onLanded: static () => [Close],
        onClosed: static () => []);
}
