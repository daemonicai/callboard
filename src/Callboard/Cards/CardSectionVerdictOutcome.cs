namespace Callboard.Cards;

/// <summary>
/// Closed union over how recording a section verdict (§5 block E / §8a block B, <see cref="CardStore.
/// RecordSectionVerdict"/>) can end. Same shape and same reasoning as
/// <see cref="CardGateResultOutcome"/> — a caller-correctable refusal
/// (<see cref="NotASectionCard"/>, <see cref="CardNotFound"/>, <see cref="LayoutMismatch"/>,
/// <see cref="RecurringFindingNotApproved"/>, <see cref="RecurringFindingTargetsTaskImplementingBlock"/>,
/// <see cref="FindingAlreadyOwned"/>, <see cref="NewFindingCardAlreadyExists"/>) is kept
/// structurally apart from a reported problem with the record's own content
/// (<see cref="CardCorrupt"/>) and from enforcement itself being unavailable
/// (<see cref="ToolFailure"/>).
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
        Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved,
        Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock,
        Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned,
        Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure);

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
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onRecorded(this);
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>section</c> —
    /// verdicts are only recorded on a section card. Refusal-shaped.</summary>
    internal sealed record NotASectionCard(CardKind Kind) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotASectionCard(this);
    }

    /// <summary>No card exists at the target path — the section itself, or (§8a block B) a
    /// <c>--finding-recurred</c> target that vanished between resolution and this call's own fresh
    /// read under lock. Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardNotFound(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>) — the section itself, a recurring target, or the
    /// new card's own path. Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>A <c>--finding-recurred</c> target is not currently <c>approved</c> — the
    /// <c>finding-recurred</c> edge is not available from its current state (work-lifecycle:
    /// "<c>finding-recurred</c> leaves <c>approved</c>"). Refusal-shaped.</summary>
    internal sealed record RecurringFindingNotApproved(string CardId, string FilePath, BlockFlowState CurrentState) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onRecurringFindingNotApproved(this);
    }

    /// <summary>A <c>--finding-recurred</c> target carries one or more <see cref="BlockCardFields.
    /// Tasks"/> — work-lifecycle's own definition of task-implementing ("A block card carrying
    /// tasks is task-implementing; a remediation card carries none") — so
    /// <c>finding-recurred</c> SHALL NOT target it (work-lifecycle: "it never targets a
    /// task-implementing block"). Refusal-shaped.</summary>
    internal sealed record RecurringFindingTargetsTaskImplementingBlock(string CardId, string FilePath) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onRecurringFindingTargetsTaskImplementingBlock(this);
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
    internal sealed record FindingAlreadyOwned(string Key, string OwningCardId, string OwningCardFilePath) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onFindingAlreadyOwned(this);
    }

    /// <summary>A <c>--finding-new-file</c> path already has a file on disk. Refusal-shaped.</summary>
    internal sealed record NewFindingCardAlreadyExists(string FilePath) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNewFindingCardAlreadyExists(this);
    }

    /// <summary>The card exists but could not be parsed. Neither refusal nor tool-failure — a
    /// reported problem with the record's own content.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardCorrupt(this);
    }

    /// <summary>Enforcement itself is unavailable: a card's lock could not be acquired within its
    /// timeout, or an I/O error occurred while writing. Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<RecurringFindingNotApproved, TResult> onRecurringFindingNotApproved, Func<RecurringFindingTargetsTaskImplementingBlock, TResult> onRecurringFindingTargetsTaskImplementingBlock, Func<FindingAlreadyOwned, TResult> onFindingAlreadyOwned, Func<NewFindingCardAlreadyExists, TResult> onNewFindingCardAlreadyExists, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
