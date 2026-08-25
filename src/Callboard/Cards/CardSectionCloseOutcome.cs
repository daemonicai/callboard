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
/// <see cref="BlockGateAbsent"/>. These are still about what a section is permitted to close
/// <em>over</em> (its own blocks), not about the section entity in isolation — see the next
/// paragraph for the boundary this stays inside of.
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
/// <b>What this type still does not decide (§5 block E brief — "the closing conditions belong to
/// §9, not to you").</b> <see cref="CardStore.CloseSectionUnderExistingLock"/> never checks open
/// obligations, undeferred questions, or unresolved threads before closing — those refusals are
/// 9.6/9.7/9.8's, layered by a caller of this method, not built into it.
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
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure);

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
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onClosed(this);
    }

    /// <summary>The target section is already closed. Refusal-shaped — closing does not
    /// re-record a new acting role/time over the one already recorded.</summary>
    internal sealed record AlreadyClosed(string FilePath) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onAlreadyClosed(this);
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>section</c>.
    /// Refusal-shaped.</summary>
    internal sealed record NotASectionCard(CardKind Kind) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotASectionCard(this);
    }

    /// <summary>work-lifecycle: "Closing a section SHALL refuse where any block in it is not
    /// `approved`" (§8a block A) — <paramref name="BlockId"/> names the offending block,
    /// <paramref name="BlockFilePath"/> its file, <paramref name="ActualState"/> the state it is
    /// actually in. A block already <see cref="BlockFlowState.Landed"/> never reaches this case — it
    /// is skipped, not refused (see <see cref="Closed.LandedBlocks"/>'s own doc comment).
    /// Refusal-shaped.</summary>
    internal sealed record BlockNotApproved(string BlockId, string BlockFilePath, BlockFlowState ActualState) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onBlockNotApproved(this);
    }

    /// <summary>work-lifecycle: "Closing a section SHALL refuse where … any block carries an
    /// expected gate whose recorded exit code is non-zero" (§8a block A) — an expected gate is one
    /// this block has recorded evidence for at all (any <see cref="GateResult.Label"/> present on
    /// its <see cref="BlockCardFields.GateResults"/>, regardless of round); <paramref
    /// name="ExitCode"/> is what the current round actually recorded for it. Refusal-shaped.
    /// </summary>
    internal sealed record BlockGateFailed(string BlockId, string BlockFilePath, string GateLabel, int ExitCode) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onBlockGateFailed(this);
    }

    /// <summary>work-lifecycle: "Closing a section SHALL refuse where … any block carries an
    /// expected gate whose recorded exit code is … absent" (§8a block A) — kept distinct from
    /// <see cref="BlockGateFailed"/> the same way <see cref="GateStatus"/> keeps "never ran" apart
    /// from "ran and failed": absent is a refusal in its own right, not a pass by default. Fires
    /// when the current round has no recorded result at all for a label this block has recorded
    /// evidence for in an earlier round (<see cref="BlockCardFields.GateStatusOf"/>). Refusal-shaped.
    /// </summary>
    internal sealed record BlockGateAbsent(string BlockId, string BlockFilePath, string GateLabel) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onBlockGateAbsent(this);
    }

    /// <summary>No card exists at the target path — the section's own path, or (§8a block A) a
    /// block's path that vanished between the scan and the write. Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardNotFound(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>) — the section's own path, or (§8a block A) one of
    /// its blocks'. Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>A card exists but could not be parsed — the section's own file, or (§8a block A) any
    /// <c>*.md</c> file in the section's directory that could not be read as a card at all, which
    /// this close conservatively refuses over rather than silently skip (the same "an unreadable
    /// card blocks the whole operation" discipline <see cref="CardStore.ArchiveChange"/> already
    /// applies for the same reason: a card this method cannot parse is a card whose <c>section</c>
    /// field it cannot check, so it cannot be ruled out as one of this section's own blocks).
    /// Neither refusal nor tool-failure.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardCorrupt(this);
    }

    /// <summary>Enforcement itself is unavailable: a card's lock could not be acquired within its
    /// timeout — the section's own, or (§8a block A) one of its blocks' — or an I/O error occurred
    /// while writing. Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(
            Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard,
            Func<BlockNotApproved, TResult> onBlockNotApproved, Func<BlockGateFailed, TResult> onBlockGateFailed, Func<BlockGateAbsent, TResult> onBlockGateAbsent,
            Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
