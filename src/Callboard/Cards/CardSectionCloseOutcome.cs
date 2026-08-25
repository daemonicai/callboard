namespace Callboard.Cards;

/// <summary>
/// Closed union over how closing a section (§5 block E, <see cref="CardStore.CloseSection"/>) can
/// end. Same shape and reasoning as <see cref="CardSectionVerdictOutcome"/>, plus
/// <see cref="AlreadyClosed"/> — closing records the acting role and the time exactly once
/// (work-lifecycle: "closing it SHALL record the acting role and the time"), so a second close
/// attempt is refused rather than silently overwriting who closed it first.
///
/// <para>
/// <b>§8a block A adds the section's own landing conditions</b> (work-lifecycle: "Approval is
/// provisional until the section closes" — "Closing a section SHALL refuse where any block in it is
/// not `approved`, or where any block carries an expected gate whose recorded exit code is non-zero
/// or absent"): <see cref="BlockNotApproved"/>, <see cref="BlockGateFailed"/> and
/// <see cref="BlockGateAbsent"/>.
/// </para>
///
/// <para>
/// <b>No `reviewed_state` comparison (§8a block A revision, Product Owner ruling: "`approved` is
/// terminal").</b> An earlier version of this union carried a fourth landing case comparing each
/// block's `reviewed_state` against a caller-supplied "current state". work-lifecycle now says
/// explicitly that closing a section SHALL NOT compare `reviewed_state` against the repository —
/// see <see cref="CardStore.ValidateBlockForLanding"/>'s own doc comment for why that check had no
/// satisfiable remedy once `amendment-requested` was cut.
/// </para>
///
/// <para>
/// <b>§9 block E adds the closing conditions themselves</b> (process-enforcement: "Section close
/// settles its obligations/questions/addressed threads", "Work cannot proceed past a stop-and-ask"):
/// <see cref="OpenObligations"/>, <see cref="OpenUndeferredQuestion"/>,
/// <see cref="UnresolvedAddressedThread"/> and <see cref="BlockedByOpenProductOwnerQuestion"/> — the
/// last one is 9.8's carried arm, the section-driven half of "work cannot proceed past a
/// stop-and-ask" that landing is the only remaining unguarded door for once §8a made landing
/// section-driven (§9 block D's own DEVLOG note). See <see cref="CardStore.
/// ValidateBlockForLanding"/> and <see cref="CardStore.CloseSectionUnderExistingLock"/> for where
/// each is decided.
/// </para>
///
/// <para>
/// <b>Every case in this union is now in the refusal reporting format (§9 block E, the standing
/// carve rule: "each of B–E retrofits its own outcome union entire").</b> A case that resolved a
/// real, parsed card implements <see cref="ICardRefusalReason"/> and records against that card —
/// see each case's own doc comment for which card that is and why. <see cref="CardNotFound"/>,
/// <see cref="LayoutMismatch"/> and <see cref="CardCorrupt"/> never do (§9 architect ruling: "only a
/// card-addressed refusal records"), and <see cref="ToolFailure"/> is never refusal-shaped at all
/// (ADR-0001).
/// </para>
/// </summary>
internal abstract record CardSectionCloseOutcome
{
    private CardSectionCloseOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Closed, TResult> onClosed,
        Func<AlreadyClosed, TResult> onAlreadyClosed,
        Func<NotASectionCard, TResult> onNotASectionCard,
        Func<BlockNotApproved, TResult> onBlockNotApproved,
        Func<BlockGateFailed, TResult> onBlockGateFailed,
        Func<BlockGateAbsent, TResult> onBlockGateAbsent,
        Func<OpenObligations, TResult> onOpenObligations,
        Func<OpenUndeferredQuestion, TResult> onOpenUndeferredQuestion,
        Func<UnresolvedAddressedThread, TResult> onUnresolvedAddressedThread,
        Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory);

    /// <param name="Card">The section card as written, now carrying <c>status: closed</c> and its
    /// <c>closed_by</c>/<c>closed_at</c> fields.</param>
    /// <param name="LandedBlocks">Every block card the close moved onto <c>landed</c> in this same
    /// operation, plus every block that was already <c>landed</c> when the close began (§8a block A:
    /// "a block already landed is skipped rather than refused" — a retried close is idempotent, and
    /// a caller reading this list should not have to tell "just landed" from "landed already" apart
    /// to know the section's blocks are all accounted for). Excludes only the section card itself.
    /// </param>
    internal sealed record Closed(CardFile Card, IReadOnlyList<CardFile> LandedBlocks) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<OpenObligations, TResult> onOpenObligations, Func<OpenUndeferredQuestion, TResult> onOpenUndeferredQuestion,
            Func<UnresolvedAddressedThread, TResult> onUnresolvedAddressedThread, Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onClosed(this);
    }

    /// <summary>The target section is already closed. Card-addressed (post-read: the section card
    /// resolved and parsed before this fires) — records against the section card. Refusal-shaped —
    /// closing does not re-record a new acting role/time over the one already recorded.</summary>
    internal sealed record AlreadyClosed(string FilePath) : CardSectionCloseOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<OpenObligations, TResult> onOpenObligations, Func<OpenUndeferredQuestion, TResult> onOpenUndeferredQuestion,
            Func<UnresolvedAddressedThread, TResult> onUnresolvedAddressedThread, Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onAlreadyClosed(this);

        public string RefusingRule => "work-lifecycle: closing a section records the acting role and the time exactly once";

        public string Remedy => $"'{FilePath}' is already closed; a second close is refused rather than overwriting who closed it first.";
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>section</c>.
    /// Card-addressed — records against the resolved card. Refusal-shaped.</summary>
    internal sealed record NotASectionCard(CardKind Kind) : CardSectionCloseOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<OpenObligations, TResult> onOpenObligations, Func<OpenUndeferredQuestion, TResult> onOpenUndeferredQuestion,
            Func<UnresolvedAddressedThread, TResult> onUnresolvedAddressedThread, Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onNotASectionCard(this);

        public string RefusingRule => "work-lifecycle: only a section card can be closed by this verb";

        public string Remedy => "target a card whose kind is 'section'.";
    }

    /// <summary>work-lifecycle: "Closing a section SHALL refuse where any block in it is not
    /// `approved`" (§8a block A) — <paramref name="BlockId"/> names the offending block,
    /// <paramref name="BlockFilePath"/> its file, <paramref name="ActualState"/> the state it is
    /// actually in. A block already <see cref="BlockFlowState.Landed"/> never reaches this case — it
    /// is skipped, not refused (see <see cref="Closed.LandedBlocks"/>'s own doc comment). Card-
    /// addressed against the offending <em>block</em> (§9 block E ruling: "ask what the refusal
    /// asserts" — this is a fact about that block, not about the section attempting to close it),
    /// under the block's own lock, already held by the time <see cref="CardStore.
    /// ValidateBlockForLanding"/> runs. Refusal-shaped.</summary>
    internal sealed record BlockNotApproved(string BlockId, string BlockFilePath, BlockFlowState ActualState) : CardSectionCloseOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<OpenObligations, TResult> onOpenObligations, Func<OpenUndeferredQuestion, TResult> onOpenUndeferredQuestion,
            Func<UnresolvedAddressedThread, TResult> onUnresolvedAddressedThread, Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onBlockNotApproved(this);

        public string RefusingRule => "work-lifecycle: every block in a section must be approved before the section can close";

        public string Remedy => $"get block '{BlockId}' to 'approved' (it is currently '{ActualState.ToWireString()}') before closing this section.";
    }

    /// <summary>work-lifecycle: "Closing a section SHALL refuse where … any block carries an
    /// expected gate whose recorded exit code is non-zero" (§8a block A) — an expected gate is one
    /// this block has recorded evidence for at all (any <see cref="GateResult.Label"/> present on
    /// its <see cref="BlockCardFields.GateResults"/>, regardless of round); <paramref
    /// name="ExitCode"/> is what the current round actually recorded for it. Card-addressed against
    /// the offending block, same reasoning as <see cref="BlockNotApproved"/>. Refusal-shaped.
    /// </summary>
    internal sealed record BlockGateFailed(string BlockId, string BlockFilePath, string GateLabel, int ExitCode) : CardSectionCloseOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<OpenObligations, TResult> onOpenObligations, Func<OpenUndeferredQuestion, TResult> onOpenUndeferredQuestion,
            Func<UnresolvedAddressedThread, TResult> onUnresolvedAddressedThread, Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onBlockGateFailed(this);

        public string RefusingRule => "work-lifecycle: every gate a block carries must have passed before the section can close";

        public string Remedy => $"gate '{GateLabel}' on block '{BlockId}' recorded exit code {ExitCode}; get it to 0 (with 'block gate') before closing this section.";
    }

    /// <summary>work-lifecycle: "Closing a section SHALL refuse where … any block carries an
    /// expected gate whose recorded exit code is … absent" (§8a block A) — kept distinct from
    /// <see cref="BlockGateFailed"/> the same way <see cref="GateStatus"/> keeps "never ran" apart
    /// from "ran and failed": absent is a refusal in its own right, not a pass by default. Fires
    /// when the current round has no recorded result at all for a label this block has recorded
    /// evidence for in an earlier round (<see cref="BlockCardFields.GateStatusOf"/>). Card-addressed
    /// against the offending block, same reasoning as <see cref="BlockNotApproved"/>. Refusal-
    /// shaped.</summary>
    internal sealed record BlockGateAbsent(string BlockId, string BlockFilePath, string GateLabel) : CardSectionCloseOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<OpenObligations, TResult> onOpenObligations, Func<OpenUndeferredQuestion, TResult> onOpenUndeferredQuestion,
            Func<UnresolvedAddressedThread, TResult> onUnresolvedAddressedThread, Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onBlockGateAbsent(this);

        public string RefusingRule => "work-lifecycle: every gate a block carries must have passed before the section can close";

        public string Remedy => $"record gate '{GateLabel}' for block '{BlockId}' with 'block gate' before closing this section — an absent gate is not a pass by default.";
    }

    /// <summary>process-enforcement: "Section close settles its obligations" (§9 block E) — at
    /// least one <c>obligation</c> card, owed by this section (<see cref="RegisterCardFields.
    /// OwedBy"/> naming the section's own id), is still <see cref="RegisterLifecycleState.Open"/>.
    /// Card-addressed against the <em>section</em> card — the fact this asserts is "this section may
    /// not close yet", not a fact about any one obligation. <paramref name="Obligations"/> names
    /// every open obligation found (spec: "the system refuses and lists the obligations and the
    /// dispositions available"), each obligations owed by <see cref="CardStore.
    /// CloseSectionUnderExistingLock"/>'s target read as one fresh pass over the section's own
    /// directory at decision time (obligations are <see cref="CardScope.Change"/>-scoped and live in
    /// that same directory) — conservative the same way the block scan already is: an unreadable
    /// card anywhere in that directory refuses the whole close via <see cref="CardCorrupt"/> rather
    /// than being silently skipped, since its own <c>owed_by</c> cannot be checked either.
    /// Refusal-shaped.</summary>
    internal sealed record OpenObligations(string SectionId, IReadOnlyList<(string Id, string Title)> Obligations) : CardSectionCloseOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<OpenObligations, TResult> onOpenObligations, Func<OpenUndeferredQuestion, TResult> onOpenUndeferredQuestion,
            Func<UnresolvedAddressedThread, TResult> onUnresolvedAddressedThread, Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onOpenObligations(this);

        public string RefusingRule => "process-enforcement: section close settles its obligations";

        public string Remedy =>
            $"discharge each obligation owed by '{SectionId}' (with 'obligation discharge'), promote it to a wider scope, " +
            "or decline it with a recorded reason, before this section can close: " +
            string.Join(", ", Obligations.Select(static o => $"{o.Id} (\"{o.Title}\")")) + ".";
    }

    /// <summary>process-enforcement: "Section close settles its questions" (§9 block E) — a
    /// <c>question</c> raised in this section (<see cref="CardFrontmatter.Section"/> naming it) is
    /// still <see cref="QuestionStatus.Open"/> — not <c>answered</c>, and not <c>deferred</c> to a
    /// named target (deferred: "the close proceeds and the question remains open against its
    /// target"). Card-addressed against the <em>section</em> card, same reasoning as <see
    /// cref="OpenObligations"/>. Names one offending question at a time (spec scenario: "the system
    /// refuses and names the question"), the first found scanning the register — questions are
    /// <see cref="CardScope.Repository"/>-scoped and live outside the section's own directory, so
    /// this reads <see cref="CardLayout.ResolveLiveRecordDirectories"/> the same way <see
    /// cref="RuleCitations.UncitedOpenRules"/> already does, and — unlike <see
    /// cref="OpenObligations"/>'s own directory — an unreadable card anywhere in that wider scan is
    /// silently skipped rather than refusing every section's close (the same "resolution failures
    /// are conservative by omission" precedent <see cref="CardStore.
    /// FindBlockingOpenProductOwnerQuestion"/> already established for a question lookup outside a
    /// card's own directory). Refusal-shaped.</summary>
    internal sealed record OpenUndeferredQuestion(string SectionId, string QuestionId, string QuestionTitle) : CardSectionCloseOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<OpenObligations, TResult> onOpenObligations, Func<OpenUndeferredQuestion, TResult> onOpenUndeferredQuestion,
            Func<UnresolvedAddressedThread, TResult> onUnresolvedAddressedThread, Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onOpenUndeferredQuestion(this);

        public string RefusingRule => "process-enforcement: section close settles its questions";

        public string Remedy =>
            $"question '{QuestionId}' (\"{QuestionTitle}\") is open and raised in this section; answer it (with 'question answer'), " +
            "defer it to a named later section or change (with 'question defer'), before this section can close.";
    }

    /// <summary>process-enforcement: "Section close settles its addressed threads" (§9 block E) —
    /// at least one comment addressed to a role, on the section card itself or on one of its blocks,
    /// remains unresolved (<see cref="CardCommentRouting.LiveAddressedThreadIds"/>, role-agnostic —
    /// this close is not acting as any one role). Card-addressed against whichever card carries the
    /// thread (<paramref name="CardId"/>/<paramref name="CardFilePath"/>) — the fact this asserts is
    /// about that specific thread, the same "ask what the refusal asserts" reasoning <see
    /// cref="BlockNotApproved"/>'s own doc comment gives. Names the first card found carrying an
    /// unresolved addressed thread (section checked first, then each block in the order <see
    /// cref="CardStore.ReadAllCards"/> returns them) and every thread id on that card (spec: "the
    /// system refuses and lists the dispositions available for it"). No new lock is taken to record
    /// this: the section's own card and every block card are already held under lock by the time
    /// this check runs.
    /// <b>Absolute — no age qualifier (§9 block E, architect ruling).</b> Every live addressed
    /// thread refuses, whether or not it has also survived a round boundary on its own block (<see
    /// cref="AgeingThread"/>, computed separately by <see cref="CardStore.
    /// FindAgeingAddressedThreads"/> for <c>section status</c>). The two are not mutually
    /// exclusive: a thread named here can also be named by <see cref="AgeingThread"/> — the
    /// ageing sweep is a distinct, earlier-in-the-section's-life surfacing that refuses nothing of
    /// its own, not an exemption carved out of this refusal. Exempting an aged thread would let a
    /// section close over the very threads neglected longest, rewarding the neglect the
    /// requirement's own purpose clause ("to keep this gate from becoming a formality discharged in
    /// bulk at the moment of closing") exists to prevent. Refusal-shaped.</summary>
    internal sealed record UnresolvedAddressedThread(string CardId, string CardFilePath, IReadOnlyList<string> ThreadIds) : CardSectionCloseOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<OpenObligations, TResult> onOpenObligations, Func<OpenUndeferredQuestion, TResult> onOpenUndeferredQuestion,
            Func<UnresolvedAddressedThread, TResult> onUnresolvedAddressedThread, Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onUnresolvedAddressedThread(this);

        public string RefusingRule => "process-enforcement: section close settles its addressed threads";

        public string Remedy =>
            $"card '{CardId}' carries unresolved addressed thread(s) {string.Join(", ", ThreadIds)}; resolve, promote to a 'question', " +
            "promote to a 'decision', or decline with a recorded reason, before this section can close.";
    }

    /// <summary>process-enforcement: "Work cannot proceed past a stop-and-ask" (§9 block D's
    /// guard on the generic transitions and <c>approve</c>; §9 block E's carried arm, 9.8) —
    /// <paramref name="BlockId"/> names an <c>approved</c> block this close would otherwise land,
    /// but its own <see cref="BlockCardFields.BlockedBy"/> names an open Product Owner question
    /// (<see cref="CardStore.FindBlockingOpenProductOwnerQuestion"/>). Section-driven landing (§8a)
    /// is the one remaining forward motion this guard did not already reach when block D shipped it
    /// for the generic transitions and <c>approve</c> — see <see cref="CardStore.
    /// ValidateBlockForLanding"/>, where this is checked. Card-addressed against the <em>block</em>,
    /// exactly the same disposition as <see cref="CardApprovalOutcome.
    /// BlockedByOpenProductOwnerQuestion"/> and <see cref="CardBlockTransitionOutcome.
    /// BlockedByOpenProductOwnerQuestion"/> — "this block may not advance" is a fact about the
    /// block, regardless of which verb discovered it. Refusal-shaped.</summary>
    internal sealed record BlockedByOpenProductOwnerQuestion(string BlockId, string BlockFilePath, string QuestionId, string QuestionTitle) : CardSectionCloseOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<OpenObligations, TResult> onOpenObligations, Func<OpenUndeferredQuestion, TResult> onOpenUndeferredQuestion,
            Func<UnresolvedAddressedThread, TResult> onUnresolvedAddressedThread, Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onBlockedByOpenProductOwnerQuestion(this);

        public string RefusingRule => "process-enforcement: work cannot proceed past a stop-and-ask";

        public string Remedy => $"question '{QuestionId}' (\"{QuestionTitle}\") is open and owned by the product owner; get it answered or deferred before block '{BlockId}' can land.";
    }

    /// <summary>No card exists at the target path — the section's own path, or (§8a block A) a
    /// block's path that vanished between the scan and the write. Never card-addressed (§9
    /// architect ruling: nothing resolved to record against). Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<OpenObligations, TResult> onOpenObligations, Func<OpenUndeferredQuestion, TResult> onOpenUndeferredQuestion,
            Func<UnresolvedAddressedThread, TResult> onUnresolvedAddressedThread, Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onCardNotFound(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>) — the section's own path, or (§8a block A) one of
    /// its blocks'. Never card-addressed (categorical, §9 architect ruling). Refusal-shaped.
    /// </summary>
    internal sealed record LayoutMismatch(string Reason) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<OpenObligations, TResult> onOpenObligations, Func<OpenUndeferredQuestion, TResult> onOpenUndeferredQuestion,
            Func<UnresolvedAddressedThread, TResult> onUnresolvedAddressedThread, Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onLayoutMismatch(this);
    }

    /// <summary>A card exists but could not be parsed — the section's own file, or (§8a block A) any
    /// <c>*.md</c> file in the section's directory that could not be read as a card at all, which
    /// this close conservatively refuses over rather than silently skip (the same "an unreadable
    /// card blocks the whole operation" discipline <see cref="CardStore.ArchiveChange"/> already
    /// applies for the same reason: a card this method cannot parse is a card whose <c>section</c>
    /// field it cannot check, so it cannot be ruled out as one of this section's own blocks or
    /// obligations). Never card-addressed (nothing parsed to record against). Neither refusal nor
    /// tool-failure.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<OpenObligations, TResult> onOpenObligations, Func<OpenUndeferredQuestion, TResult> onOpenUndeferredQuestion,
            Func<UnresolvedAddressedThread, TResult> onUnresolvedAddressedThread, Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onCardCorrupt(this);
    }

    /// <summary>work-lifecycle: "Stored round agrees with the transition history" (8a.17) — the
    /// <paramref name="BlockFilePath"/> block's stored <c>round</c> does not equal one plus the
    /// number of round-incrementing transitions (<see cref="BlockFlowTransitions.
    /// RoundIncrementingTransitionNames"/>) in its own <see cref="CardFile.Transitions"/> history.
    /// Card-addressed against the offending block, same reasoning as <see cref="BlockNotApproved"/>.
    /// Carries its own <paramref name="BlockFilePath"/> for the same reason <see
    /// cref="BlockNotApproved"/> does — this verb's own target is the section, so the refusal has
    /// to say which of the blocks it is closing over disagrees. Refusal-shaped: neither figure is
    /// privileged and neither is altered — a stored count ahead of the history and a history ahead
    /// of the count are different failures, and guessing which is right would silently destroy the
    /// evidence of whichever was correct.</summary>
    internal sealed record RoundDisagreesWithHistory(string BlockFilePath, int StoredRound, int ExpectedRound) : CardSectionCloseOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard, Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent, Func<OpenObligations, TResult> onOpenObligations, Func<OpenUndeferredQuestion, TResult> onOpenUndeferredQuestion, Func<UnresolvedAddressedThread, TResult> onUnresolvedAddressedThread, Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onRoundDisagreesWithHistory(this);

        public string RefusingRule => "work-lifecycle: stored round agrees with the transition history";

        public string Remedy =>
            $"the recorded round ({StoredRound}) disagrees with the transition history ({ExpectedRound}); " +
            "correct whichever was altered outside the tool before this section can close.";
    }

    /// <summary>Enforcement itself is unavailable: a card's lock could not be acquired within its
    /// timeout — the section's own, or (§8a block A) one of its blocks' — or an I/O error occurred
    /// while writing, including while recording a refusal against a card this close resolved.
    /// Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<OpenObligations, TResult> onOpenObligations, Func<OpenUndeferredQuestion, TResult> onOpenUndeferredQuestion,
            Func<UnresolvedAddressedThread, TResult> onUnresolvedAddressedThread, Func<BlockedByOpenProductOwnerQuestion, TResult> onBlockedByOpenProductOwnerQuestion,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onToolFailure(this);
    }
}

/// <summary>One live addressed thread found by <see cref="CardStore.FindAgeingAddressedThreads"/>
/// — process-enforcement's ageing-thread prompt (§9 block E, architect ruling): "to keep this gate
/// from becoming a formality discharged in bulk at the moment of closing", the requirement's own
/// purpose clause names an <em>earlier</em> surfacing during the section's life, not a close-time
/// one — so this is read from <c>section status</c>, never from <see cref="CardSectionCloseOutcome.
/// Closed"/> (by the time a close succeeds, <see cref="UnresolvedAddressedThread"/> has already
/// refused over every live addressed thread, aged or not, so nothing would ever be left to report
/// there). Never a refusal in its own right.</summary>
/// <param name="CardId">The id of the block card carrying the thread.</param>
/// <param name="CardFilePath">That block's own file path.</param>
/// <param name="ThreadId">The id of the ageing comment itself.</param>
/// <param name="AddressedTo">The role the comment is addressed to — who this prompt is
/// for.</param>
internal sealed record AgeingThread(string CardId, string CardFilePath, string ThreadId, CardOwner AddressedTo);
