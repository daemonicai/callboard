namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.DeclineObligation"/> can end (§9 block F, register:
/// "An obligation that will not be met SHALL be closable by declining it with a recorded reason, and
/// the record SHALL distinguish that from an obligation that was discharged"). Same split-by-
/// disposition reasoning as <see cref="CardRegisterDischargeOutcome"/>, which this parallels closely
/// — declining reuses the same lifecycle transition (<c>open</c> → <c>discharged</c>) and refuses on
/// the same two register-wide preconditions (register kind, non-flow-state status), plus one
/// disposition-specific refusal <see cref="CardRegisterDischargeOutcome"/> has no counterpart for:
/// <see cref="ReasonRequired"/>.
///
/// <para>
/// <b>No <c>InvalidStatus</c> case (§12 block A).</b> See <see cref="CardRegisterDischargeOutcome"/>'s
/// own doc comment: <see cref="CardFileParser"/> now validates a register card's own <c>status</c>
/// at the parse door, so <see cref="CardCorrupt"/> carries that refusal's reason instead.
/// </para>
/// </summary>
internal abstract record CardObligationDeclineOutcome
{
    private CardObligationDeclineOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Declined, TResult> onDeclined,
        Func<ReasonRequired, TResult> onReasonRequired,
        Func<AlreadyDischarged, TResult> onAlreadyDischarged,
        Func<NotAnObligationCard, TResult> onNotAnObligationCard,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState);

    /// <param name="Card">The card exactly as written after declining — <c>status</c> is now
    /// <c>discharged</c>, <see cref="RegisterCardFields.DischargedBy"/>/<see cref="RegisterCardFields.
    /// DischargedAt"/> record who and when the same as any discharge, and <see cref="RegisterCardFields.
    /// DeclinedReason"/> carries the reason — the one field that tells a later reader this was a
    /// decline, not a discharge asserting the work was met.</param>
    internal sealed record Declined(CardFile Card) : CardObligationDeclineOutcome
    {
        internal override TResult Match<TResult>(Func<Declined, TResult> onDeclined, Func<ReasonRequired, TResult> onReasonRequired, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onDeclined(this);
    }

    /// <summary>No reason was supplied — register: "Scenario: Declining requires a reason" ("the
    /// system refuses and states that a reason is required"). <see cref="Callboard.Cli.
    /// CommandParser"/>'s own <c>obligation decline</c> door already requires <c>--reason</c>
    /// unconditionally (the same "required at the door a real caller uses" discipline block A2 drew
    /// for <c>rule promote</c>'s <c>--change</c>); this case exists so <see cref="CardStore.
    /// DeclineObligation"/> defends the same requirement on its own terms, for a caller reaching it
    /// directly rather than through the CLI. Refusal-shaped.</summary>
    internal sealed record ReasonRequired(string FilePath) : CardObligationDeclineOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Declined, TResult> onDeclined, Func<ReasonRequired, TResult> onReasonRequired, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onReasonRequired(this);

        public string RefusingRule => "register: declining an obligation requires a recorded reason";

        public string Remedy => $"'{FilePath}' was not declined; retry with a non-empty reason.";
    }

    /// <summary>The obligation is already discharged — met, promoted-away-and-reopened, or declined
    /// previously; declining again would either silently overwrite the first disposition's reason or
    /// silently no-op, neither of which this build accepts. Refusal-shaped.</summary>
    internal sealed record AlreadyDischarged(string FilePath) : CardObligationDeclineOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Declined, TResult> onDeclined, Func<ReasonRequired, TResult> onReasonRequired, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onAlreadyDischarged(this);

        public string RefusingRule => "register: a discharged register card cannot be discharged (or declined) again";

        public string Remedy => $"'{FilePath}' is already discharged; there is nothing further to decline.";
    }

    /// <summary>The resolved card is not an <c>obligation</c> — register's decline scenario is
    /// obligation-specific ("an obligation that will not be met"), unlike the generic four-kind
    /// discharge. Refusal-shaped.</summary>
    internal sealed record NotAnObligationCard(CardKind Kind) : CardObligationDeclineOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Declined, TResult> onDeclined, Func<ReasonRequired, TResult> onReasonRequired, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onNotAnObligationCard(this);

        public string RefusingRule => "register: declining applies to obligation cards";

        public string Remedy => "target a card whose kind is 'obligation'.";
    }

    /// <summary>No card file exists at the resolved path (a race between resolution and locking).
    /// Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardObligationDeclineOutcome
    {
        internal override TResult Match<TResult>(Func<Declined, TResult> onDeclined, Func<ReasonRequired, TResult> onReasonRequired, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardNotFound(this);
    }

    /// <summary>The card's directory does not anchor under its own recorded scope — unreachable in
    /// practice, kept for the same reason every other <c>CardStore</c> write surface carries this
    /// case. Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardObligationDeclineOutcome
    {
        internal override TResult Match<TResult>(Func<Declined, TResult> onDeclined, Func<ReasonRequired, TResult> onReasonRequired, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but could not be parsed. Neither refusal nor tool-failure.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardObligationDeclineOutcome
    {
        internal override TResult Match<TResult>(Func<Declined, TResult> onDeclined, Func<ReasonRequired, TResult> onReasonRequired, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardCorrupt(this);
    }

    /// <summary>working-context: "No figure SHALL be hand-entered anywhere in the system" (§10
    /// block C) — <paramref name="Key"/> names a reserved derived-state field (<see
    /// cref="DerivedStateFieldKeys.All"/>) present on the target card's <see cref="CardFile.
    /// UnknownFrontmatterFields"/>, the door a hand-edited card's frontmatter uses to reach this far
    /// at all (nothing this build's own CLI ever writes one). Refusal-shaped, card-addressed (§9
    /// block A3): checked immediately once the card is read, before declining the obligation is allowed to
    /// proceed, so this write never re-emits (and never launders forward) a hand-entered count or
    /// next-step pin it did not itself write. See <see cref="CardWriteResult.HandEnteredDerivedState"/>
    /// for the sibling case on the generic comment/handover surface.</summary>
    internal sealed record HandEnteredDerivedState(string Key) : CardObligationDeclineOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Declined, TResult> onDeclined, Func<ReasonRequired, TResult> onReasonRequired, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onHandEnteredDerivedState(this);

        public string RefusingRule => "working-context: no figure shall be hand-entered";

        public string Remedy =>
            $"'{Key}' is a reserved derived-state field name; remove it from this card's frontmatter — " +
            "this state is derived at request time, never stored, and is available from 'callboard state'.";
    }

    /// <summary>Enforcement itself is unavailable — the lock could not be acquired, or the write
    /// failed after every check passed. Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardObligationDeclineOutcome
    {
        internal override TResult Match<TResult>(Func<Declined, TResult> onDeclined, Func<ReasonRequired, TResult> onReasonRequired, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onToolFailure(this);
    }
}
