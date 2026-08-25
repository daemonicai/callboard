namespace Callboard.Cards;

/// <summary>
/// The exhaustive, total transition table for <see cref="BlockFlowState"/>
/// (work-lifecycle: "Block cards move through a defined flow"):
///
/// <code>
/// drafting ──▶ briefed ──▶ building ──▶ in-review ──┬──▶ approved ──▶ landed ──▶ closed
///                   ▲                               │        │
///                   ├──── changes-requested ◀───────┤        │
///                   └──── fix-before-land ◀──────────┘        │
///                       (round += 1 on both)
/// </code>
///
/// <see cref="AvailableFrom"/> is the first-class query the brief asks for: what a caller — block
/// B's transition-applying code, or its refusal message — reads instead of restating the table.
/// It is total over every <see cref="BlockFlowState"/> because it is built with
/// <see cref="BlockFlowState.Match{TResult}"/>: every one of the seven cases supplies an arm, so
/// a case with no legal transitions (<c>closed</c>, and — until block B adds `finding-recurred` —
/// <c>approved</c>) has to say so explicitly (an empty list) rather than the query silently falling
/// through. Round application (the <c>round += 1</c> on <c>changes-requested</c>/
/// <c>fix-before-land</c>) is applied by their own callers — this table only says which edges exist
/// and where each one lands.
///
/// <para>
/// <b><c>land</c> is on this diagram but not on <see cref="AvailableFrom"/>'s <c>approved</c>
/// entry (§8a block A, work-lifecycle: "Approval is provisional until the section closes" —
/// "<c>land</c> SHALL NOT be individually invocable"). <see cref="Land"/> still exists as a
/// value</b> — it is what <see cref="CardStore.CloseSectionUnderExistingLock"/> applies directly,
/// via <see cref="LandTransition"/>, to every approved block a section closes over — but no caller
/// can reach it by naming a transition: <c>AvailableFrom</c> never offers it, and
/// <c>block transition … land</c> is refused outright at parse (<see cref="Callboard.Cli.
/// CommandParser.ParseBlockTransition"/>), the same "one door" discipline <c>fix-before-land</c>
/// already established. It is the *invocation* that is gone, not the edge: a block reaches
/// <c>landed</c> only as the consequence of its whole section closing, never as an act performed on
/// that one card alone.
/// </para>
///
/// <para>
/// <b><c>approved</c> is terminal for a task-implementing block (§8a block A revision, Product
/// Owner ruling: "an approved block never goes back to work").</b> `AvailableFrom(Approved)` holds
/// exactly one edge — <see cref="FindingRecurred"/> (§8a block B) — reached only through
/// <see cref="CardStore.RecordSectionVerdictUnderExistingLock"/>, and refused at parse the same
/// "one door" way <c>approve</c>/<c>fix-before-land</c>/<c>land</c> already are
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

    /// <summary>The one way <see cref="Land"/> is ever reached — not through <see cref="AvailableFrom"/>
    /// (it no longer lists it for <c>approved</c>), but by <see cref="CardStore.
    /// CloseSectionUnderExistingLock"/> naming this property directly, the same way a section close
    /// is the one act permitted to move a block onto <see cref="BlockFlowState.Landed"/>.</summary>
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
    /// The transitions legally available from <paramref name="state"/> — empty for <c>closed</c>,
    /// the flow's one terminal state, and exactly <see cref="FindingRecurred"/> for <c>approved</c>
    /// (§8a block B): a task-implementing block that reaches <c>approved</c> has no caller-facing
    /// edge back to work — only a remediation card does, and only through this one edge — see this
    /// type's own doc comment. <c>in-review</c> is the one state with three: <c>approve</c>,
    /// <c>changes-requested</c> and <c>fix-before-land</c> (§8 block B), the latter two landing on
    /// the same <c>briefed</c> destination as distinct named edges.
    /// </summary>
    internal static IReadOnlyList<BlockFlowTransition> AvailableFrom(BlockFlowState state) => state.Match(
        onDrafting: static () => (IReadOnlyList<BlockFlowTransition>)[Brief],
        onBriefed: static () => [Claim],
        onBuilding: static () => [SubmitForReview],
        onInReview: static () => [Approve, ChangesRequested, FixBeforeLand],
        onApproved: static () => [FindingRecurred],
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
