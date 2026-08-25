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
///                   └──── finding-recurred ◀───────────────────┘
///                       (round += 1 on all three)
/// </code>
///
/// <b>Two distinct queries read this table (§8a remediation, supervisor finding: "`AvailableFrom`
/// was widened into a state predicate, and the generic applier is a second source of truth for the
/// round increment").</b> <see cref="AvailableFrom"/> answers "what edges exist from this state, on
/// this diagram, whichever door drives them" — the round-counting derivation
/// (<see cref="RoundIncrementingTransitionNames"/>), the "is this card approved?" question
/// (<see cref="CardStore.RecordSectionVerdictUnderExistingLock"/>, which now asks it as a state
/// comparison rather than a membership test against this table), and any other "is this edge legal
/// here" question read it. <see cref="GenericallyInvocableFrom"/> answers a narrower question — "what
/// may a bare <c>block transition</c> call itself drive from this state" — and is what <see cref="
/// CardStore.ApplyBlockTransitionUnderExistingLock"/> resolves a caller-supplied transition name
/// against, and what its <c>UndefinedTransition</c> refusal reports. The two used to be one table:
/// widening <see cref="AvailableFrom"/> to carry <c>finding-recurred</c> so the state-comparison
/// question above could be asked (§8a block B) made the generic applier able to resolve — but not
/// correctly apply — an edge its own <c>CommandParser</c> refuses by name at parse, producing
/// exactly the round/history disagreement "Stored round agrees with the transition history" (8a.17)
/// exists to refuse and forbids reconciling. Splitting the two questions apart removes that
/// possibility structurally: a one-door edge is no longer resolvable through the generic applier at
/// all, not merely refused by a second, separate check.
///
/// It is total over every <see cref="BlockFlowState"/> because it is built with
/// <see cref="BlockFlowState.Match{TResult}"/>: every one of the seven cases supplies an arm, so
/// a case with no legal transitions (<c>closed</c>) has to say so explicitly (an empty list) rather
/// than the query silently falling through. Round application (the <c>round += 1</c> on
/// <c>changes-requested</c>/<c>fix-before-land</c>/<c>finding-recurred</c>) is applied by their own
/// callers, driven by <see cref="RoundIncrementingTransitionNames"/> rather than restated per
/// caller — this table only says which edges exist and where each one lands.
///
/// <para>
/// <b><c>land</c> is on this diagram, and on <see cref="AvailableFrom"/>'s <c>approved</c> entry, but
/// not on <see cref="GenericallyInvocableFrom"/>'s (§8a block A, work-lifecycle: "Approval is
/// provisional until the section closes" — "<c>land</c> SHALL NOT be individually invocable").
/// <see cref="Land"/> still exists as a value</b> — it is what <see cref="CardStore.
/// CloseSectionUnderExistingLock"/> applies directly, via <see cref="LandTransition"/>, to every
/// approved block a section closes over — but no caller can reach it by naming a transition:
/// <c>GenericallyInvocableFrom</c> never offers it, and <c>block transition … land</c> is refused
/// outright at parse (<see cref="Callboard.Cli.CommandParser.ParseBlockTransition"/>), the same "one
/// door" discipline <c>fix-before-land</c> already established. It is the *invocation* that is gone,
/// not the edge: a block reaches <c>landed</c> only as the consequence of its whole section closing,
/// never as an act performed on that one card alone.
/// </para>
///
/// <para>
/// <b><c>approved</c> is terminal for a task-implementing block (§8a block A revision, Product
/// Owner ruling: "an approved block never goes back to work").</b> `AvailableFrom(Approved)` holds
/// exactly two edges — <see cref="Land"/> and <see cref="FindingRecurred"/> (§8a block B) — and
/// <c>GenericallyInvocableFrom(Approved)</c> holds neither: <see cref="FindingRecurred"/> is
/// reached only through <see cref="CardStore.RecordSectionVerdictUnderExistingLock"/>, and refused
/// at parse the same "one door" way <c>approve</c>/<c>fix-before-land</c>/<c>land</c> already are
/// (<see cref="Callboard.Cli.CommandParser.ParseBlockTransition"/>): a supervisor drives it
/// directly against a **remediation card**, never against a block that implements tasks (checked
/// independently of this table — see <see cref="CardStore.
/// RecordSectionVerdictUnderExistingLock"/>'s own "targets a task-implementing block" refusal,
/// which reads <see cref="BlockCardFields.Tasks"/>, not this edge's existence). A task-implementing
/// block reviewed and found wanting is not reopened: the fix becomes a new remediation block the
/// section carries, and the original stays exactly as approved. This table used to carry a second
/// edge here, <c>amendment-requested</c> — the architect deliberately reopening an approved block
/// for a fresh review — but it depended on closing comparing each block's certified state against
/// the repository at close time, a check that turned out to have no satisfiable remedy of its own
/// (see <see cref="CardStore.ValidateBlockForLanding"/>'s doc comment) and was cut along with it.
/// There is no route from <c>approved</c> back to <c>briefed</c> for a task-implementing block at
/// all — only for a remediation card, and only through <see cref="FindingRecurred"/>.
/// </para>
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
    ///
    /// <para>
    /// <b>Bounded to the block-level review loop only (§8a block A, work-lifecycle: "Reviewer
    /// remediation is the same card at a higher round" — "This governs the block-level review loop
    /// only").</b> A reviewer's <c>changes-requested</c> never creates a card — it returns the same
    /// card that was already under review, on this one edge, and nothing else on this type's surface
    /// mints a new card from it. Section-level remediation — a supervisor's verdict against a whole
    /// section, routed by whether a card already owns the finding — is a distinct concern (work-
    /// lifecycle: "Section remediation follows the finding, not the verdict") and is not this edge's
    /// job: it is raised through <c>finding-recurred</c> or a fresh <c>finding record</c>, never
    /// through this transition.
    /// </para>
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

    /// <summary>The one way <see cref="Land"/> is ever reached — not through <see cref="
    /// GenericallyInvocableFrom"/> (it never lists it for <c>approved</c>, though <see cref="
    /// AvailableFrom"/> does), but by <see cref="CardStore.CloseSectionUnderExistingLock"/> naming
    /// this property directly, the same way a section close is the one act permitted to move a
    /// block onto <see cref="BlockFlowState.Landed"/>.</summary>
    internal static BlockFlowTransition LandTransition => Land;

    private static readonly BlockFlowTransition Close = new("close", BlockFlowState.Landed, BlockFlowState.Closed);

    /// <summary>
    /// work-lifecycle's own name for the one edge <c>approved</c> carries (§8a block B, "Section
    /// remediation follows the finding, not the verdict" — "A finding the supervisor reports as
    /// still unresolved SHALL return the card that owns it to <c>briefed</c> with <c>round</c>
    /// incremented, by the <c>finding-recurred</c> transition"). Reached only through
    /// <see cref="CardStore.RecordSectionVerdictUnderExistingLock"/>, which applies <see
    /// cref="Match(BlockFlowState)"/>-style round incrementing itself (work-lifecycle: "round += 1
    /// on all three") rather than through <see cref="CardStore.ApplyBlockTransitionUnderExistingLock"/>
    /// — the same "one door, its own dedicated write" shape <see cref="Land"/> and <c>approve</c>
    /// already established, not the generic <c>block transition</c> path. <c>block transition …
    /// finding-recurred</c> is refused outright at parse (<see cref="Callboard.Cli.
    /// CommandParser.ParseBlockTransition"/>), naming <c>section verdict --finding-recurred</c> as
    /// the door.
    /// </summary>
    private static readonly BlockFlowTransition FindingRecurred = new("finding-recurred", BlockFlowState.Approved, BlockFlowState.Briefed);

    /// <summary>The one way <see cref="FindingRecurred"/> is ever reached — see this field's own
    /// doc comment.</summary>
    internal static BlockFlowTransition FindingRecurredTransition => FindingRecurred;

    /// <summary>
    /// Every transition legally available from <paramref name="state"/> — empty for <c>closed</c>,
    /// the flow's one terminal state, and exactly <see cref="Land"/> and <see cref="FindingRecurred"/>
    /// for <c>approved</c> (§8a block B, widened further by the §8a remediation to include
    /// <see cref="Land"/> — see this type's own doc comment): a task-implementing block that reaches
    /// <c>approved</c> has no caller-facing edge back to work, but the edge to <c>landed</c> is real
    /// and this query says so, the way it says so for every other state. <c>in-review</c> is the one
    /// state with three: <c>approve</c>, <c>changes-requested</c> and <c>fix-before-land</c> (§8
    /// block B), the latter two landing on the same <c>briefed</c> destination as distinct named
    /// edges.
    ///
    /// <b>This is the raw edge table, not the invocation surface (§8a remediation).</b> A caller
    /// deciding what a bare <c>block transition</c> call may itself drive reads
    /// <see cref="GenericallyInvocableFrom"/> instead — this query includes one-door edges
    /// (<c>approve</c>, <c>fix-before-land</c>, <c>finding-recurred</c>, <c>land</c>) that exist and
    /// are legal but are reached only through their own dedicated write, never through the generic
    /// applier.
    /// </summary>
    internal static IReadOnlyList<BlockFlowTransition> AvailableFrom(BlockFlowState state) => state.Match(
        onDrafting: static () => (IReadOnlyList<BlockFlowTransition>)[Brief],
        onBriefed: static () => [Claim],
        onBuilding: static () => [SubmitForReview],
        onInReview: static () => [Approve, ChangesRequested, FixBeforeLand],
        onApproved: static () => [Land, FindingRecurred],
        onLanded: static () => [Close],
        onClosed: static () => []);

    /// <summary>
    /// The subset of <see cref="AvailableFrom"/>'s edges a bare <c>block transition</c> call may
    /// itself drive (§8a remediation, settling the contract collision the supervisor's §8a section
    /// review found: <see cref="AvailableFrom"/> was being read both as "the invocation surface" and
    /// as "the legal-edge table", and widening it for the second reading broke the first). Every
    /// one-door edge — <c>approve</c> (<see cref="CardStore.RecordApprovalUnderExistingLock"/>'s own
    /// door), <c>fix-before-land</c> (<see cref="CardStore.DispositionNitUnderLocks"/>'s),
    /// <c>finding-recurred</c> (<see cref="CardStore.RecordSectionVerdictUnderExistingLock"/>'s) and
    /// <c>land</c> (<see cref="CardStore.CloseSectionUnderExistingLock"/>'s) — is legal (it is still
    /// on <see cref="AvailableFrom"/>) but excluded here, because each already has its own dedicated
    /// write that applies its own certification/disposition/verdict/section-close side effects a
    /// bare transition could never supply. <see cref="CardStore.
    /// ApplyBlockTransitionUnderExistingLock"/> resolves a caller-supplied transition name against
    /// this query, not <see cref="AvailableFrom"/>, and its <c>UndefinedTransition</c> refusal
    /// reports this query's result — so the refusal never advertises a door it would then itself
    /// refuse (the second consequence the supervisor's finding named).
    /// </summary>
    internal static IReadOnlyList<BlockFlowTransition> GenericallyInvocableFrom(BlockFlowState state) => state.Match(
        onDrafting: static () => (IReadOnlyList<BlockFlowTransition>)[Brief],
        onBriefed: static () => [Claim],
        onBuilding: static () => [SubmitForReview],
        onInReview: static () => [ChangesRequested],
        onApproved: static () => [],
        onLanded: static () => [Close],
        onClosed: static () => []);

    /// <summary>
    /// The transition names work-lifecycle's "Stored round agrees with the transition history"
    /// (8a.17) counts as round-incrementing — <see cref="ChangesRequested"/>,
    /// <see cref="FixBeforeLand"/> and <see cref="FindingRecurred"/>, the three edges this type's
    /// own doc comment diagrams as <c>round += 1</c>. Derived here, from this table's own three
    /// named fields, rather than restated as a second hand-maintained list in the checker
    /// (<see cref="CardStore.CountRoundIncrementingTransitions"/>) — a fourth back-edge added later
    /// to this table without updating <em>this</em> property would still count correctly, closing
    /// the exact two-sources-of-truth failure 8a.17 exists to catch. <see cref="Land"/>,
    /// <see cref="Brief"/>, <see cref="Claim"/>, <see cref="SubmitForReview"/>, <see cref="Approve"/>
    /// and <see cref="Close"/> never increment <c>round</c> — work-lifecycle only ever names the
    /// three edges here.
    /// </summary>
    internal static IReadOnlyList<string> RoundIncrementingTransitionNames { get; } =
        [ChangesRequested.Name, FixBeforeLand.Name, FindingRecurred.Name];
}
