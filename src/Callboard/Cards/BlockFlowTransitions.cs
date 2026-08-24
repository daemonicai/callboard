namespace Callboard.Cards;

/// <summary>
/// The exhaustive, total transition table for <see cref="BlockFlowState"/>
/// (work-lifecycle: "Block cards move through a defined flow"):
///
/// <code>
/// drafting ──▶ briefed ──▶ building ──▶ in-review ──┬──▶ approved ──▶ landed ──▶ closed
///                   ▲                               │        │
///                   ├──── changes-requested ◀───────┤        │
///                   ├──── fix-before-land ◀──────────┘        │
///                   └──── amendment-requested ◀────────────────┘
///                       (round += 1 on all three)
/// </code>
///
/// <see cref="AvailableFrom"/> is the first-class query the brief asks for: what a caller — block
/// B's transition-applying code, or its refusal message — reads instead of restating the table.
/// It is total over every <see cref="BlockFlowState"/> because it is built with
/// <see cref="BlockFlowState.Match{TResult}"/>: every one of the seven cases supplies an arm, so
/// a case with no legal transitions (<c>closed</c>) has to say so explicitly (an empty list)
/// rather than the query silently falling through. Round application (the <c>round += 1</c> on
/// <c>changes-requested</c>/<c>fix-before-land</c>/<c>amendment-requested</c>) is applied by their
/// own callers — this table only says which edges exist and where each one lands. <c>approved</c>
/// has two edges: <see cref="Land"/> and <see cref="AmendmentRequested"/> — the only route from
/// <c>approved</c> back to work that is not a supervisor's <c>finding-recurred</c> (work-lifecycle:
/// "`amendment-requested` is the architect deliberately reopening an approved block"). An approval
/// certifies one exact state; any change to that state spends it, and the block is reviewed afresh
/// rather than having a recertification re-assert its claims over the difference (Product Owner
/// ruling: cutting `recertify`).
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

    /// <summary>
    /// review-certification's own name for a dispositioned nit's own edge (§8 block B, work-
    /// lifecycle's amended diagram: "<c>fix-before-land</c> ◀── … <c>in-review → briefed</c>",
    /// <c>round += 1</c>). Shares its <c>From</c>/<c>To</c> with <see cref="ChangesRequested"/> but
    /// is a distinct named edge (Architect ruling, §8 base post): the transition name is persisted
    /// in the card's history (<see cref="CardBlockTransitionEntry.Name"/>), and recording a
    /// dispositioned nit as <c>changes-requested</c> would misreport what happened. Reached only
    /// through <see cref="CardStore.DispositionNit"/> — <c>block transition ... fix-before-land</c>
    /// is refused outright at parse, the same "one door" discipline §8 block A's brief established
    /// for <c>approve</c> (<see cref="Callboard.Cli.CommandParser.ParseBlockTransition"/>): a bare
    /// transition through this edge would move a block to <c>briefed</c> with no nit actually
    /// dispositioned as <c>fix-before-land</c>, exactly the neglect review-certification's "SHALL
    /// NOT lapse by neglect" exists to prevent. The amended state that fix produces is not
    /// recertified — it gets a fresh review, like every other return to <c>briefed</c>.
    /// </summary>
    private static readonly BlockFlowTransition FixBeforeLand = new("fix-before-land", BlockFlowState.InReview, BlockFlowState.Briefed);

    private static readonly BlockFlowTransition Land = new("land", BlockFlowState.Approved, BlockFlowState.Landed);
    private static readonly BlockFlowTransition Close = new("close", BlockFlowState.Landed, BlockFlowState.Closed);

    /// <summary>
    /// review-certification's own name for the architect deliberately reopening an approved block
    /// (§8 block C remediation, work-lifecycle's amended diagram: "<c>amendment-requested</c>
    /// … <c>approved → briefed</c>", <c>round += 1</c>). It is the <em>only</em> route from
    /// <c>approved</c> back to work that is not a supervisor's recurrence (work-lifecycle):
    /// a block sitting <c>approved</c> whose state has since changed — a sibling block touching its
    /// files, or any other amendment — has its certification spent, and this is how it is handed
    /// back for a fresh review rather than having its claims re-asserted over the difference.
    /// Invoked on purpose by the architect, never as the side effect of a refusal. Reached only
    /// through <see cref="CardStore.RecordAmendmentRequest"/> — <c>block transition ...
    /// amendment-requested</c> is refused outright at parse, the same "one door" discipline every
    /// other named edge on this table established (<see cref="Callboard.Cli.CommandParser.
    /// ParseBlockTransition"/>).
    /// </summary>
    private static readonly BlockFlowTransition AmendmentRequested = new("amendment-requested", BlockFlowState.Approved, BlockFlowState.Briefed);

    /// <summary>
    /// The transitions legally available from <paramref name="state"/> — empty only for
    /// <c>closed</c>, the flow's one terminal state. <c>in-review</c> is the one state with three:
    /// <c>approve</c>, <c>changes-requested</c> and <c>fix-before-land</c> (§8 block B), the latter
    /// two landing on the same <c>briefed</c> destination as distinct named edges. <c>approved</c>
    /// has two: <c>land</c> and <c>amendment-requested</c>.
    /// </summary>
    internal static IReadOnlyList<BlockFlowTransition> AvailableFrom(BlockFlowState state) => state.Match(
        onDrafting: static () => (IReadOnlyList<BlockFlowTransition>)[Brief],
        onBriefed: static () => [Claim],
        onBuilding: static () => [SubmitForReview],
        onInReview: static () => [Approve, ChangesRequested, FixBeforeLand],
        onApproved: static () => [Land, AmendmentRequested],
        onLanded: static () => [Close],
        onClosed: static () => []);
}
