namespace Callboard.Cards;

/// <summary>
/// Closed union over how recording a section verdict (§5 block E / §8a block B, <see cref="CardStore.
/// RecordSectionVerdict"/>) can end. Same shape and same reasoning as
/// <see cref="CardGateResultOutcome"/> — a caller-correctable refusal
/// (<see cref="NotASectionCard"/>, <see cref="CardNotFound"/>, <see cref="RecurringTargetNotFound"/>,
/// <see cref="LayoutMismatch"/>, <see cref="RecurringFindingNotApproved"/>,
/// <see cref="RecurringFindingTargetsTaskImplementingBlock"/>, <see cref="FindingAlreadyOwned"/>,
/// <see cref="NewFindingCardAlreadyExists"/>, <see cref="RemediationBoundExceeded"/> and
/// <see cref="RoundDisagreesWithHistory"/>) is kept
/// structurally apart from a reported problem with the record's own content
/// (<see cref="CardCorrupt"/>) and from enforcement itself being unavailable
/// (<see cref="ToolFailure"/>).
///
/// <para>
/// <b>§9 block B retrofit onto the refusal reporting format.</b> Every card-addressed case above
/// implements <see cref="ICardRefusalReason"/> and records against the already-resolved section
/// card, except <see cref="CardNotFound"/> and <see cref="LayoutMismatch"/> (never card-addressed,
/// per the §9 base ruling) — <see cref="CardNotFound"/> is split from <see cref="
/// RecurringTargetNotFound"/> for exactly this reason: the same "no card at the target path" fact is
/// pre-lock for the section's own path and post-lock, card-addressed, for a vanished
/// <c>--finding-recurred</c> target (§9 block B, standing instruction 2).
/// </para>
///
/// <para>
/// <b>§8a block B's four additions are validated before anything is written (work-lifecycle:
/// "Section remediation follows the finding, not the verdict").</b> <see cref="CardStore.
/// RecordSectionVerdictUnderExistingLock"/> checks every <c>--finding-recurred</c> target and every
/// <c>--finding-new</c> request before it writes any of them, the same "validate the whole set,
/// then write" discipline <see cref="CardStore.CloseSectionUnderExistingLock"/> already established
/// for landing a section's blocks — a refusal here leaves the section card, every targeted
/// remediation card, and the filesystem at the intended new-card path exactly as it found them.
/// </para>
/// </summary>
internal abstract record CardSectionVerdictOutcome
{
    private CardSectionVerdictOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Recorded, TResult> onRecorded,
        Func<NotASectionCard, TResult> onNotASectionCard,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<RecurringTargetNotFound, TResult> onRecurringTargetNotFound,
        Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved,
        Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock,
        Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned,
        Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists,
        Func<RemediationBoundExceeded, TResult> onRemediationBoundExceeded,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory);

    /// <param name="Card">The section card as written, carrying the newly appended verdict entry.</param>
    /// <param name="Entry">The verdict entry actually recorded.</param>
    /// <param name="RecurredCards">Every card returned to <c>briefed</c> by <c>finding-recurred</c>
    /// this call, in the order <c>--finding-recurred</c> named them (§8a block B, work-lifecycle:
    /// "A single verdict MAY do both").</param>
    /// <param name="NewCards">The cards created for a first-time finding this call, in
    /// <c>--finding-new</c> argv order (empty when no <c>--finding-new</c> occurrence was
    /// given).</param>
    internal sealed record Recorded(
        CardFile Card, SectionVerdictEntry Entry, IReadOnlyList<CardFile> RecurredCards, IReadOnlyList<CardFile> NewCards) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringTargetNotFound, TResult> onRecurringTargetNotFound, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<RemediationBoundExceeded, TResult> onRemediationBoundExceeded, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onRecorded(this);
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>section</c> —
    /// verdicts are only recorded on a section card. Refusal-shaped.</summary>
    internal sealed record NotASectionCard(CardKind Kind) : CardSectionVerdictOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringTargetNotFound, TResult> onRecurringTargetNotFound, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<RemediationBoundExceeded, TResult> onRemediationBoundExceeded, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onNotASectionCard(this);

        public string RefusingRule => "work-lifecycle: verdicts only apply to a section card";

        public string Remedy => "target a card whose kind is 'section'.";
    }

    /// <summary>No card exists at the target path — the section itself. Refusal-shaped, but never
    /// card-addressed: this is the pre-lock <see cref="File.Exists(string)"/> check on the section
    /// card's own path, before anything is ever read. <see cref="RecurringTargetNotFound"/> is this
    /// same "no card exists" fact for a <c>--finding-recurred</c> target, split out (§9 block B,
    /// standing instruction 2) because that check runs after the section card is already read,
    /// anchored and held — the two occurrences of "not found" here are not interchangeable for
    /// recording purposes.</summary>
    internal sealed record CardNotFound(string FilePath) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringTargetNotFound, TResult> onRecurringTargetNotFound, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<RemediationBoundExceeded, TResult> onRemediationBoundExceeded, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onCardNotFound(this);
    }

    /// <summary>A <c>--finding-recurred</c> target vanished between resolution and this call's own
    /// fresh read under lock (§8a block B). Refusal-shaped and, unlike its pre-lock sibling
    /// <see cref="CardNotFound"/>, card-addressed: the section card this verdict targets is already
    /// read, anchored and locked by the time this fires, so it is recorded against the section, the
    /// same "record against the already-resolved card" reasoning <see cref="CardNitDispositionOutcome.
    /// RaisedCardAlreadyExists"/> already established for a collision on a different, unparsed
    /// path.</summary>
    internal sealed record RecurringTargetNotFound(string FilePath) : CardSectionVerdictOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringTargetNotFound, TResult> onRecurringTargetNotFound, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<RemediationBoundExceeded, TResult> onRemediationBoundExceeded, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onRecurringTargetNotFound(this);

        public string RefusingRule => "card-model: a --finding-recurred target must exist";

        public string Remedy => $"'{FilePath}' no longer exists; recheck the id, or drop it from --finding-recurred and raise it as new with --finding-new instead.";
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>) — the section itself, a recurring target, or the
    /// new card's own path. Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringTargetNotFound, TResult> onRecurringTargetNotFound, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<RemediationBoundExceeded, TResult> onRemediationBoundExceeded, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onLayoutMismatch(this);
    }

    /// <summary>A <c>--finding-recurred</c> target is not currently <c>approved</c> — the
    /// <c>finding-recurred</c> edge is not available from its current state (work-lifecycle:
    /// "<c>finding-recurred</c> leaves <c>approved</c>"). Refusal-shaped.</summary>
    internal sealed record RecurringFindingNotApproved(string CardId, string FilePath, BlockFlowState CurrentState) : CardSectionVerdictOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringTargetNotFound, TResult> onRecurringTargetNotFound, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<RemediationBoundExceeded, TResult> onRemediationBoundExceeded, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onRecurringFindingNotApproved(this);

        public string RefusingRule => "work-lifecycle: finding-recurred leaves approved";

        public string Remedy =>
            $"'{CardId}' ('{FilePath}') is not 'approved' (it is '{CurrentState.ToWireString()}') — " +
            "'finding-recurred' only returns a remediation card that is currently approved.";
    }

    /// <summary>A <c>--finding-recurred</c> target carries one or more <see cref="BlockCardFields.
    /// Tasks"/> — work-lifecycle's own definition of task-implementing ("A block card carrying
    /// tasks is task-implementing; a remediation card carries none") — so
    /// <c>finding-recurred</c> SHALL NOT target it (work-lifecycle: "it never targets a
    /// task-implementing block"). Refusal-shaped.</summary>
    internal sealed record RecurringFindingTargetsTaskImplementingBlock(string CardId, string FilePath) : CardSectionVerdictOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringTargetNotFound, TResult> onRecurringTargetNotFound, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<RemediationBoundExceeded, TResult> onRemediationBoundExceeded, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onRecurringFindingTargetsTaskImplementingBlock(this);

        public string RefusingRule => "work-lifecycle: finding-recurred never targets a task-implementing block";

        public string Remedy =>
            $"'{CardId}' ('{FilePath}') carries tasks — it is a task-implementing block, not a remediation card. " +
            "Raise the finding as new instead, with '--finding-new'.";
    }

    /// <summary>A <c>--finding-new</c> manifest's <c>key</c> already names an owner of that finding
    /// in this section (work-lifecycle: "A recurrence SHALL NOT create a second card for the same
    /// finding, so that one card's thread is the complete history of one finding across every
    /// round it took to close"). The owner is either an existing on-disk card, or — §8a block B
    /// revision's own addition, once a single verdict could name more than one new finding — an
    /// <em>earlier</em> <c>--finding-new</c> occurrence in this same call, in which case
    /// <see cref="OwningCardId"/> is the literal <c>"&lt;pending: this verdict&gt;"</c> sentinel and
    /// <see cref="OwningCardFilePath"/> is that earlier manifest's own <c>new-card-file</c>, since
    /// there is no on-disk owner yet to name honestly. Names the remedy either way:
    /// <c>--finding-recurred</c> for a real owner, a different key for an in-batch collision.
    /// Refusal-shaped.</summary>
    internal sealed record FindingAlreadyOwned(string Key, string OwningCardId, string OwningCardFilePath) : CardSectionVerdictOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringTargetNotFound, TResult> onRecurringTargetNotFound, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<RemediationBoundExceeded, TResult> onRemediationBoundExceeded, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onFindingAlreadyOwned(this);

        public string RefusingRule => "work-lifecycle: a recurrence SHALL NOT create a second card for the same finding";

        public string Remedy =>
            $"finding '{Key}' is already owned by '{OwningCardId}' ('{OwningCardFilePath}'). " +
            $"Use '--finding-recurred {OwningCardId}' instead, or give the new finding a different '--finding-new' key.";
    }

    /// <summary>A <c>--finding-new-file</c> path already has a file on disk. Refusal-shaped.</summary>
    internal sealed record NewFindingCardAlreadyExists(string FilePath) : CardSectionVerdictOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringTargetNotFound, TResult> onRecurringTargetNotFound, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<RemediationBoundExceeded, TResult> onRemediationBoundExceeded, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onNewFindingCardAlreadyExists(this);

        public string RefusingRule => "card-model: identities are never recycled, and a new finding must not overwrite an existing file";

        public string Remedy => $"'{FilePath}' already exists; choose a different '--finding-new-file' path.";
    }

    /// <summary>work-lifecycle: "Remediation beyond the second round requires recorded
    /// authorisation" (§8a block C) — this <c>request-changes</c> verdict would be the section's
    /// third or later, and no unspent Product Owner authorisation covers it. <see
    /// cref="VerdictNumber"/> is the position this verdict would occupy among the section's own
    /// <c>request-changes</c> verdicts (3, 4, ...) had it been recorded; <see cref="
    /// AuthorisationsRecorded"/> is the section's total recorded-authorisation count and <see
    /// cref="UnspentAuthorisations"/> the derived count still available at the moment of the
    /// attempt (always &lt;= 0 here — that is what makes this the refused case) — both reported so
    /// the refusal states the fact, not just the rule, and so the message never has to assert
    /// "none unspent" as text that could go stale against a future revoking path (reviewer nit,
    /// §8a block C remediation, Architect ruling).</summary>
    internal sealed record RemediationBoundExceeded(int VerdictNumber, int AuthorisationsRecorded, int UnspentAuthorisations) : CardSectionVerdictOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringTargetNotFound, TResult> onRecurringTargetNotFound, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<RemediationBoundExceeded, TResult> onRemediationBoundExceeded, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onRemediationBoundExceeded(this);

        public string RefusingRule => "work-lifecycle: remediation beyond the second round requires recorded authorisation";

        public string Remedy =>
            $"the section already carries {VerdictNumber - 1} 'request-changes' verdicts (a section admits two " +
            $"without ceremony) and this would be number {VerdictNumber} — {AuthorisationsRecorded} authorisation" +
            $"{(AuthorisationsRecorded == 1 ? "" : "s")} recorded, {Math.Max(UnspentAuthorisations, 0)} unspent. " +
            "A recorded Product Owner authorisation ('section authorise --role product-owner --reason <text>') would satisfy it.";
    }

    /// <summary>The card exists but could not be parsed. Neither refusal nor tool-failure — a
    /// reported problem with the record's own content.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringTargetNotFound, TResult> onRecurringTargetNotFound, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<RemediationBoundExceeded, TResult> onRemediationBoundExceeded, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onCardCorrupt(this);
    }

    /// <summary>work-lifecycle: "Stored round agrees with the transition history" (8a.17) — the
    /// <paramref name="FilePath"/> block card's stored <c>round</c> does not equal one plus the
    /// number of round-incrementing transitions (<see cref="BlockFlowTransitions.
    /// RoundIncrementingTransitionNames"/>) in its own <see cref="CardFile.Transitions"/> history.
    /// Carries its own <paramref name="FilePath"/>, unlike this type's other cases, because this
    /// verdict's own target is the <em>section</em> card — the block that disagrees is one of the
    /// <c>--finding-recurred</c> ids named alongside it, so the refusal has to say which one.
    /// Refusal-shaped: neither figure is privileged and neither is altered — a stored count ahead of
    /// the history and a history ahead of the count are different failures, and guessing which is
    /// right would silently destroy the evidence of whichever was correct.</summary>
    internal sealed record RoundDisagreesWithHistory(string FilePath, int StoredRound, int ExpectedRound) : CardSectionVerdictOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringTargetNotFound, TResult> onRecurringTargetNotFound, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<RemediationBoundExceeded, TResult> onRemediationBoundExceeded, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onRoundDisagreesWithHistory(this);

        public string RefusingRule => "work-lifecycle: stored round agrees with the transition history";

        public string Remedy =>
            $"'{FilePath}' has stored round {StoredRound}, but its own transition history implies round " +
            $"{ExpectedRound}; correct whichever was altered outside the tool before this verdict can proceed.";
    }

    /// <summary>Enforcement itself is unavailable: a card's lock could not be acquired within its
    /// timeout, or an I/O error occurred while writing. Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringTargetNotFound, TResult> onRecurringTargetNotFound, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<RemediationBoundExceeded, TResult> onRemediationBoundExceeded, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onToolFailure(this);
    }
}
