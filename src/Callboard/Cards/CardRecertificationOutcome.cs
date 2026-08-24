namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.RecordRecertification"/> (§8 block C, <c>block
/// recertify</c>) can end. review-certification: "Recertification re-asserts an existing claim
/// set" / "Recertification is bounded". Its own type rather than a reuse of
/// <see cref="CardApprovalOutcome"/>: a recertification never stamps a fresh claim/limit sequence
/// (it re-asserts one already recorded, individually addressed by id — Architect ruling: "claims
/// already have stable identity — use it") and its two live outcomes are shaped differently from
/// approval's binary verdict — <see cref="Recertified"/> leaves the card <c>approved</c>;
/// <see cref="ClaimsRefused"/> is itself a first-class success (the reviewer's judgment that a
/// claim no longer holds is not a caller error), not a refusal, even though it moves the card back
/// to <c>briefed</c>. Same refusal/tool-failure/reported-failure discipline every other outcome
/// type in this codebase establishes: <see cref="RoleNotPermitted"/>, <see cref="NotABlockCard"/>,
/// <see cref="CardNotFound"/>, <see cref="NotApproved"/>, <see cref="AlreadyRecertified"/>,
/// <see cref="UnknownClaimIds"/>, <see cref="MissingClaimOutcomes"/> and
/// <see cref="LayoutMismatch"/> are refusal-shaped (caller-correctable); <see cref="CardCorrupt"/>
/// and <see cref="ToolFailure"/> are not — a caller wired over this type (see
/// <see cref="Callboard.Cli.CommandDispatcher.RunBlockRecertify"/>) must route those two to a
/// tool-failure exit, never a refusal.
///
/// <para>
/// <b>What this type — and this whole verb — cannot enforce.</b> review-certification also says
/// "The reviewer SHALL re-derive each claim against the code; reading the difference between the
/// certified and amended states SHALL NOT be sufficient". That sentence governs a human at a
/// keyboard, not this tool: no case here, and nothing <see cref="CardStore.RecordRecertification"/>
/// does, can distinguish a genuinely re-derived assertion from one rubber-stamped by reading a
/// diff. There is deliberately no flag or field anywhere on this surface implying otherwise — a
/// flag that records a promise the tool cannot verify is the failure mode this project exists to
/// avoid, not a feature to add. The obligation is surfaced in the CLI response text instead (see
/// <see cref="Callboard.Cli.BlockRecertifyResult.Notice"/>), where the reviewer reads it, not
/// enforced.
/// </para>
/// </summary>
internal abstract record CardRecertificationOutcome
{
    private CardRecertificationOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Recertified, TResult> onRecertified,
        Func<ClaimsRefused, TResult> onClaimsRefused,
        Func<RoleNotPermitted, TResult> onRoleNotPermitted,
        Func<NotABlockCard, TResult> onNotABlockCard,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<NotApproved, TResult> onNotApproved,
        Func<AlreadyRecertified, TResult> onAlreadyRecertified,
        Func<UnknownClaimIds, TResult> onUnknownClaimIds,
        Func<MissingClaimOutcomes, TResult> onMissingClaimOutcomes,
        Func<GatesNotGreen, TResult> onGatesNotGreen,
        Func<DifferenceOutsideNitSites, TResult> onDifferenceOutsideNitSites,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure);

    /// <summary>Every named claim was asserted (review-certification: "A successful recertification
    /// SHALL re-stamp reviewed_state to the amended state"). <c>round</c> does not move (Architect
    /// ruling: "A successful recertification does not increment round").</summary>
    /// <param name="Card">The card as written: <c>reviewed_state</c> re-stamped to the amended
    /// state, the appended recertification-record comment, status and round unchanged.</param>
    /// <param name="AssertedClaimIds">Every claim id this call asserted, in the order given —
    /// exactly the current approval's whole claim set, since a recertification naming fewer than
    /// all of them is refused before reaching this case (<see cref="MissingClaimOutcomes"/>).</param>
    internal sealed record Recertified(CardFile Card, IReadOnlyList<string> AssertedClaimIds) : CardRecertificationOutcome
    {
        internal override TResult Match<TResult>(Func<Recertified, TResult> onRecertified, Func<ClaimsRefused, TResult> onClaimsRefused, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotApproved, TResult> onNotApproved, Func<AlreadyRecertified, TResult> onAlreadyRecertified, Func<UnknownClaimIds, TResult> onUnknownClaimIds, Func<MissingClaimOutcomes, TResult> onMissingClaimOutcomes, Func<GatesNotGreen, TResult> onGatesNotGreen, Func<DifferenceOutsideNitSites, TResult> onDifferenceOutsideNitSites, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onRecertified(this);
    }

    /// <summary>At least one named claim was refused — review-certification: "A refusal of any
    /// claim SHALL be a first-class outcome that returns the block to briefed and increments
    /// round." <c>reviewed_state</c> is left exactly as it was (Architect ruling: "Not cleared, not
    /// blanked, not updated"); the caller's candidate <c>--state</c> for this attempt is
    /// discarded.</summary>
    /// <param name="Card">The card as written: status <c>briefed</c>, <c>round</c> incremented by
    /// one, the appended <see cref="CardBlockTransitionEntry"/> for
    /// <c>recertification-refused</c>, the appended recertification-record comment,
    /// <c>reviewed_state</c> untouched.</param>
    /// <param name="AssertedClaimIds">The claims this call asserted.</param>
    /// <param name="RefusedClaimIds">The claims this call refused — together with
    /// <paramref name="AssertedClaimIds"/>, exactly the current approval's whole claim set (Architect
    /// ruling: "every claim must receive an outcome; a recertification that silently omits one is
    /// refused" — see <see cref="MissingClaimOutcomes"/>).</param>
    internal sealed record ClaimsRefused(CardFile Card, IReadOnlyList<string> AssertedClaimIds, IReadOnlyList<string> RefusedClaimIds) : CardRecertificationOutcome
    {
        internal override TResult Match<TResult>(Func<Recertified, TResult> onRecertified, Func<ClaimsRefused, TResult> onClaimsRefused, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotApproved, TResult> onNotApproved, Func<AlreadyRecertified, TResult> onAlreadyRecertified, Func<UnknownClaimIds, TResult> onUnknownClaimIds, Func<MissingClaimOutcomes, TResult> onMissingClaimOutcomes, Func<GatesNotGreen, TResult> onGatesNotGreen, Func<DifferenceOutsideNitSites, TResult> onDifferenceOutsideNitSites, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onClaimsRefused(this);
    }

    /// <summary>review-certification: "Approval is role-bounded" — recertification's own half of
    /// 8.13, reusing <see cref="CardStore.IsApprovingRole"/> (reviewer/supervisor only) rather than
    /// a second predicate.</summary>
    internal sealed record RoleNotPermitted(CardOwner AttemptedRole) : CardRecertificationOutcome
    {
        internal override TResult Match<TResult>(Func<Recertified, TResult> onRecertified, Func<ClaimsRefused, TResult> onClaimsRefused, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotApproved, TResult> onNotApproved, Func<AlreadyRecertified, TResult> onAlreadyRecertified, Func<UnknownClaimIds, TResult> onUnknownClaimIds, Func<MissingClaimOutcomes, TResult> onMissingClaimOutcomes, Func<GatesNotGreen, TResult> onGatesNotGreen, Func<DifferenceOutsideNitSites, TResult> onDifferenceOutsideNitSites, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onRoleNotPermitted(this);
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>block</c>.
    /// Refusal-shaped: caller pointed the verb at the wrong card.</summary>
    internal sealed record NotABlockCard(CardKind Kind) : CardRecertificationOutcome
    {
        internal override TResult Match<TResult>(Func<Recertified, TResult> onRecertified, Func<ClaimsRefused, TResult> onClaimsRefused, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotApproved, TResult> onNotApproved, Func<AlreadyRecertified, TResult> onAlreadyRecertified, Func<UnknownClaimIds, TResult> onUnknownClaimIds, Func<MissingClaimOutcomes, TResult> onMissingClaimOutcomes, Func<GatesNotGreen, TResult> onGatesNotGreen, Func<DifferenceOutsideNitSites, TResult> onDifferenceOutsideNitSites, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotABlockCard(this);
    }

    /// <summary>No card exists at the target path. Refusal-shaped: caller-correctable.</summary>
    internal sealed record CardNotFound(string FilePath) : CardRecertificationOutcome
    {
        internal override TResult Match<TResult>(Func<Recertified, TResult> onRecertified, Func<ClaimsRefused, TResult> onClaimsRefused, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotApproved, TResult> onNotApproved, Func<AlreadyRecertified, TResult> onAlreadyRecertified, Func<UnknownClaimIds, TResult> onUnknownClaimIds, Func<MissingClaimOutcomes, TResult> onMissingClaimOutcomes, Func<GatesNotGreen, TResult> onGatesNotGreen, Func<DifferenceOutsideNitSites, TResult> onDifferenceOutsideNitSites, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardNotFound(this);
    }

    /// <summary>The card's current <see cref="BlockFlowState"/> is not <see cref="BlockFlowState.
    /// Approved"/> — recertification only ever applies to a currently-certified block. Refusal-
    /// shaped: caller pointed the verb at a card that has nothing to recertify.</summary>
    internal sealed record NotApproved(BlockFlowState CurrentState) : CardRecertificationOutcome
    {
        internal override TResult Match<TResult>(Func<Recertified, TResult> onRecertified, Func<ClaimsRefused, TResult> onClaimsRefused, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotApproved, TResult> onNotApproved, Func<AlreadyRecertified, TResult> onAlreadyRecertified, Func<UnknownClaimIds, TResult> onUnknownClaimIds, Func<MissingClaimOutcomes, TResult> onMissingClaimOutcomes, Func<GatesNotGreen, TResult> onGatesNotGreen, Func<DifferenceOutsideNitSites, TResult> onDifferenceOutsideNitSites, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotApproved(this);
    }

    /// <summary>review-certification: "The system SHALL permit at most one recertification per
    /// approval" (8.10) — a recertification (successful or refused) has already been recorded
    /// since the current approval. Attaches to the approval, not the card (Architect ruling): a
    /// block recertified, sent back to <c>briefed</c>, rebuilt and approved again is a new approval
    /// and gets a fresh recertification — see <see cref="CardStore.RecordRecertification"/>'s own
    /// doc comment for how "since the current approval" is derived.</summary>
    internal sealed record AlreadyRecertified : CardRecertificationOutcome
    {
        internal override TResult Match<TResult>(Func<Recertified, TResult> onRecertified, Func<ClaimsRefused, TResult> onClaimsRefused, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotApproved, TResult> onNotApproved, Func<AlreadyRecertified, TResult> onAlreadyRecertified, Func<UnknownClaimIds, TResult> onUnknownClaimIds, Func<MissingClaimOutcomes, TResult> onMissingClaimOutcomes, Func<GatesNotGreen, TResult> onGatesNotGreen, Func<DifferenceOutsideNitSites, TResult> onDifferenceOutsideNitSites, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onAlreadyRecertified(this);
    }

    /// <summary><c>--assert</c>/<c>--refuse</c> named one or more claim ids the current approval
    /// does not carry — a caller-supplied id that is stale, mistyped, or belongs to an earlier,
    /// already-superseded approval's claim set. Refusal-shaped: caller-correctable.</summary>
    internal sealed record UnknownClaimIds(IReadOnlyList<string> ClaimIds) : CardRecertificationOutcome
    {
        internal override TResult Match<TResult>(Func<Recertified, TResult> onRecertified, Func<ClaimsRefused, TResult> onClaimsRefused, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotApproved, TResult> onNotApproved, Func<AlreadyRecertified, TResult> onAlreadyRecertified, Func<UnknownClaimIds, TResult> onUnknownClaimIds, Func<MissingClaimOutcomes, TResult> onMissingClaimOutcomes, Func<GatesNotGreen, TResult> onGatesNotGreen, Func<DifferenceOutsideNitSites, TResult> onDifferenceOutsideNitSites, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onUnknownClaimIds(this);
    }

    /// <summary>One or more of the current approval's claims received no outcome from this call —
    /// named by neither <c>--assert</c> nor <c>--refuse</c> (Architect ruling: "Every claim must
    /// receive an outcome; a recertification that silently omits one is refused, naming the claims
    /// left without one — otherwise the third sits in the same undefined limbo the nit-disposition
    /// rule exists to prevent"). Refusal-shaped: caller-correctable.</summary>
    internal sealed record MissingClaimOutcomes(IReadOnlyList<string> ClaimIds) : CardRecertificationOutcome
    {
        internal override TResult Match<TResult>(Func<Recertified, TResult> onRecertified, Func<ClaimsRefused, TResult> onClaimsRefused, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotApproved, TResult> onNotApproved, Func<AlreadyRecertified, TResult> onAlreadyRecertified, Func<UnknownClaimIds, TResult> onUnknownClaimIds, Func<MissingClaimOutcomes, TResult> onMissingClaimOutcomes, Func<GatesNotGreen, TResult> onGatesNotGreen, Func<DifferenceOutsideNitSites, TResult> onDifferenceOutsideNitSites, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onMissingClaimOutcomes(this);
    }

    /// <summary>
    /// review-certification: "Recertification is bounded" — "every gate on the card SHALL have
    /// been re-run to a passing exit code". Mechanical, refuse-only (§8 block D brief items 2/3/10):
    /// this cannot establish that a gate was re-run <em>after</em> the amendment, only that this
    /// round's gates — every distinct label ever recorded on <see cref="BlockCardFields.
    /// GateResults"/>, checked via <see cref="BlockCardFields.GateStatusOf"/> — currently read as
    /// green. Absent and failed are kept apart in <see cref="AbsentLabels"/>/<see cref="
    /// FailedLabels"/> exactly as <see cref="GateStatus"/> keeps them apart (a label with no
    /// recorded result this round is not the same fact as one recorded non-zero). Refusal-shaped:
    /// caller-correctable — re-run the named gates and record their results.
    /// </summary>
    /// <param name="AbsentLabels">Every distinct gate label the card has ever recorded that has no
    /// result recorded for the card's current round.</param>
    /// <param name="FailedLabels">Every distinct gate label with a result recorded for the current
    /// round whose exit code was non-zero, paired with that exit code.</param>
    internal sealed record GatesNotGreen(IReadOnlyList<string> AbsentLabels, IReadOnlyList<RecertificationGateFailure> FailedLabels) : CardRecertificationOutcome
    {
        internal override TResult Match<TResult>(Func<Recertified, TResult> onRecertified, Func<ClaimsRefused, TResult> onClaimsRefused, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotApproved, TResult> onNotApproved, Func<AlreadyRecertified, TResult> onAlreadyRecertified, Func<UnknownClaimIds, TResult> onUnknownClaimIds, Func<MissingClaimOutcomes, TResult> onMissingClaimOutcomes, Func<GatesNotGreen, TResult> onGatesNotGreen, Func<DifferenceOutsideNitSites, TResult> onDifferenceOutsideNitSites, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onGatesNotGreen(this);
    }

    /// <summary>
    /// review-certification: "the difference between certified and amended states SHALL be
    /// confined to the sites of the dispositioned nits. A difference extending beyond those sites
    /// SHALL send the block to full re-review, by the same <c>amendment-requested</c> route" (§8
    /// block D brief items 4/5/6/7). The bound is the union of <see cref="CardComment.Sites"/> of
    /// every dispositioned nit raised in the current round (the round starting at the most recent
    /// transition landing on <see cref="BlockFlowState.Briefed"/>) — a round with no dispositioned
    /// nit has an empty bound, so every caller-supplied changed path is necessarily out of scope
    /// (Architect ruling: not a vacuous pass). Refusal-shaped: caller-correctable — the caller
    /// mistyped a site, or the amendment genuinely exceeds what was dispositioned, in which case
    /// the architect reopens the block via <c>block amendment-requested</c>.
    /// </summary>
    /// <param name="OffendingPaths">The caller-supplied <c>--changed</c> paths that matched no
    /// in-bounds site.</param>
    /// <param name="InBoundsSites">The dispositioned nit sites that bounded this attempt, so the
    /// caller can tell a genuine out-of-scope edit from a mistyped site.</param>
    internal sealed record DifferenceOutsideNitSites(IReadOnlyList<string> OffendingPaths, IReadOnlyList<string> InBoundsSites) : CardRecertificationOutcome
    {
        internal override TResult Match<TResult>(Func<Recertified, TResult> onRecertified, Func<ClaimsRefused, TResult> onClaimsRefused, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotApproved, TResult> onNotApproved, Func<AlreadyRecertified, TResult> onAlreadyRecertified, Func<UnknownClaimIds, TResult> onUnknownClaimIds, Func<MissingClaimOutcomes, TResult> onMissingClaimOutcomes, Func<GatesNotGreen, TResult> onGatesNotGreen, Func<DifferenceOutsideNitSites, TResult> onDifferenceOutsideNitSites, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onDifferenceOutsideNitSites(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>). Refusal-shaped: caller-correctable.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardRecertificationOutcome
    {
        internal override TResult Match<TResult>(Func<Recertified, TResult> onRecertified, Func<ClaimsRefused, TResult> onClaimsRefused, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotApproved, TResult> onNotApproved, Func<AlreadyRecertified, TResult> onAlreadyRecertified, Func<UnknownClaimIds, TResult> onUnknownClaimIds, Func<MissingClaimOutcomes, TResult> onMissingClaimOutcomes, Func<GatesNotGreen, TResult> onGatesNotGreen, Func<DifferenceOutsideNitSites, TResult> onDifferenceOutsideNitSites, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but its content could not be parsed, or carries a <c>status</c>
    /// this build does not recognise as a <see cref="BlockFlowState"/>. Neither refusal nor
    /// tool-failure — a reported problem with the record's own content. A caller wired over this
    /// type must not route it to a refusal exit.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardRecertificationOutcome
    {
        internal override TResult Match<TResult>(Func<Recertified, TResult> onRecertified, Func<ClaimsRefused, TResult> onClaimsRefused, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotApproved, TResult> onNotApproved, Func<AlreadyRecertified, TResult> onAlreadyRecertified, Func<UnknownClaimIds, TResult> onUnknownClaimIds, Func<MissingClaimOutcomes, TResult> onMissingClaimOutcomes, Func<GatesNotGreen, TResult> onGatesNotGreen, Func<DifferenceOutsideNitSites, TResult> onDifferenceOutsideNitSites, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardCorrupt(this);
    }

    /// <summary>Enforcement itself is unavailable: the card's lock could not be acquired within its
    /// timeout, or an I/O error occurred while writing. Tool-failure-shaped — the board is not
    /// refusing anything. A caller wired over this type must let it reach a tool-failure exit
    /// (ADR-0001).</summary>
    internal sealed record ToolFailure(string Reason) : CardRecertificationOutcome
    {
        internal override TResult Match<TResult>(Func<Recertified, TResult> onRecertified, Func<ClaimsRefused, TResult> onClaimsRefused, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotApproved, TResult> onNotApproved, Func<AlreadyRecertified, TResult> onAlreadyRecertified, Func<UnknownClaimIds, TResult> onUnknownClaimIds, Func<MissingClaimOutcomes, TResult> onMissingClaimOutcomes, Func<GatesNotGreen, TResult> onGatesNotGreen, Func<DifferenceOutsideNitSites, TResult> onDifferenceOutsideNitSites, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
