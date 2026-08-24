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
///                   ├──── recertification-refused ◀────────────┤
///                   └──── amendment-requested ◀─────────────────┘
///                       (round += 1 on all four)
/// </code>
///
/// <see cref="AvailableFrom"/> is the first-class query the brief asks for: what a caller — block
/// B's transition-applying code, or its refusal message — reads instead of restating the table.
/// It is total over every <see cref="BlockFlowState"/> because it is built with
/// <see cref="BlockFlowState.Match{TResult}"/>: every one of the seven cases supplies an arm, so
/// a case with no legal transitions (<c>closed</c>) has to say so explicitly (an empty list)
/// rather than the query silently falling through. Round application (the <c>round += 1</c> on
/// <c>changes-requested</c>/<c>fix-before-land</c>/<c>recertification-refused</c>/
/// <c>amendment-requested</c>) is applied by their own callers — this table only says which
/// edges exist and where each one lands. <c>approved</c> now has three edges:
/// <see cref="Land"/>, <see cref="RecertificationRefused"/> (§8 block C) and
/// <see cref="AmendmentRequested"/> (§8 block C remediation — work-lifecycle: "`amendment-
/// requested` is the architect deliberately reopening an approved block for a further
/// amendment"). A <em>successful</em> recertification leaves the card <c>approved</c> and is
/// never a table edge at all.
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
    /// NOT lapse by neglect" exists to prevent.
    /// </summary>
    private static readonly BlockFlowTransition FixBeforeLand = new("fix-before-land", BlockFlowState.InReview, BlockFlowState.Briefed);

    private static readonly BlockFlowTransition Land = new("land", BlockFlowState.Approved, BlockFlowState.Landed);
    private static readonly BlockFlowTransition Close = new("close", BlockFlowState.Landed, BlockFlowState.Closed);

    /// <summary>
    /// review-certification's own name for a refused recertification's edge (§8 block C,
    /// work-lifecycle's amended diagram: "<c>recertification-refused</c> … <c>approved →
    /// briefed</c>", <c>round += 1</c>). Reached only through <see cref="CardStore.
    /// RecordRecertification"/> — <c>block transition ... recertification-refused</c> is refused
    /// outright at parse, the same "one door" discipline block A's <c>approve</c> and block B's
    /// <c>fix-before-land</c> both established (<see cref="Callboard.Cli.CommandParser.
    /// ParseBlockTransition"/>): a bare transition through this edge would move a block back to
    /// <c>briefed</c> with no claim genuinely refused. A <em>successful</em> recertification is
    /// never a table edge at all — the card stays <c>approved</c>, so there is nothing for this
    /// table to say about it (see this type's own class doc comment).
    /// </summary>
    private static readonly BlockFlowTransition RecertificationRefused = new("recertification-refused", BlockFlowState.Approved, BlockFlowState.Briefed);

    /// <summary>
    /// review-certification's own name for the architect deliberately reopening an approved block
    /// (§8 block C remediation, work-lifecycle's amended diagram: "<c>amendment-requested</c>
    /// … <c>approved → briefed</c>", <c>round += 1</c>). Product Owner ruling: this is the route
    /// review-certification's "a further amendment after a recertification SHALL require a new
    /// round" delivers — once a block's single recertification is spent, <c>land</c> was the
    /// approved state's only remaining exit, so an amended block could only be landed carrying a
    /// <c>reviewed_state</c> that no longer described it. Invoked on purpose by the architect,
    /// never as the side effect of a refusal — a refused recertification attempt (a mechanical
    /// precondition, e.g. <see cref="Callboard.Cards.CardRecertificationOutcome.AlreadyRecertified"/>)
    /// leaves the card untouched and names this transition as the route back to <c>briefed</c>,
    /// it does not raise this edge itself. Reached only through <see cref="CardStore.
    /// RecordAmendmentRequest"/> — <c>block transition ... amendment-requested</c> is refused
    /// outright at parse, the same "one door" discipline every other named edge on this table
    /// established (<see cref="Callboard.Cli.CommandParser.ParseBlockTransition"/>).
    /// </summary>
    private static readonly BlockFlowTransition AmendmentRequested = new("amendment-requested", BlockFlowState.Approved, BlockFlowState.Briefed);

    /// <summary>
    /// The transitions legally available from <paramref name="state"/> — empty only for
    /// <c>closed</c>, the flow's one terminal state. <c>in-review</c> is the one state with three:
    /// <c>approve</c>, <c>changes-requested</c> and <c>fix-before-land</c> (§8 block B), the latter
    /// two landing on the same <c>briefed</c> destination as distinct named edges. <c>approved</c>
    /// has three: <c>land</c>, <c>recertification-refused</c> (§8 block C) and
    /// <c>amendment-requested</c> (§8 block C remediation).
    /// </summary>
    internal static IReadOnlyList<BlockFlowTransition> AvailableFrom(BlockFlowState state) => state.Match(
        onDrafting: static () => (IReadOnlyList<BlockFlowTransition>)[Brief],
        onBriefed: static () => [Claim],
        onBuilding: static () => [SubmitForReview],
        onInReview: static () => [Approve, ChangesRequested, FixBeforeLand],
        onApproved: static () => [Land, RecertificationRefused, AmendmentRequested],
        onLanded: static () => [Close],
        onClosed: static () => []);
}
