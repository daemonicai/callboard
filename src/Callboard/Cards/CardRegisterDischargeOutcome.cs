namespace Callboard.Cards;

/// <summary>
/// Closed union over how discharging a register card (§7 block A, <see cref="CardStore.
/// DischargeRegisterCard"/>) can end. Same shape and reasoning as <see cref="CardSectionCloseOutcome"/>
/// — <see cref="AlreadyDischarged"/> for the same reason <see cref="CardSectionCloseOutcome.
/// AlreadyClosed"/> exists (discharging records the acting role and the time exactly once), plus
/// <see cref="InvalidStatus"/>, which that type has no counterpart for: register's "SHALL NOT occupy
/// flow states" means a register card's <c>status</c> can be found holding a value
/// <see cref="RegisterLifecycleStateWireFormat.TryParse"/> does not recognise (a hand-edited flow-
/// state value, e.g. <c>briefed</c>) in a way <see cref="SectionFlowStateWireFormat"/> — which never
/// has to reject a value belonging to a different vocabulary sharing its own field — does not.
/// </summary>
internal abstract record CardRegisterDischargeOutcome
{
    private CardRegisterDischargeOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Discharged, TResult> onDischarged,
        Func<AlreadyDischarged, TResult> onAlreadyDischarged,
        Func<InvalidStatus, TResult> onInvalidStatus,
        Func<NotARegisterCard, TResult> onNotARegisterCard,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState);

    /// <param name="Card">The card as written, now carrying <c>status: discharged</c> and its
    /// <c>discharged_by</c>/<c>discharged_at</c> fields.</param>
    internal sealed record Discharged(CardFile Card) : CardRegisterDischargeOutcome
    {
        internal override TResult Match<TResult>(Func<Discharged, TResult> onDischarged, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARegisterCard, TResult> onNotARegisterCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onDischarged(this);
    }

    /// <summary>The target register card is already discharged. Refusal-shaped — discharging does
    /// not re-record a new acting role/time over the one already recorded.</summary>
    internal sealed record AlreadyDischarged(string FilePath) : CardRegisterDischargeOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Discharged, TResult> onDischarged, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARegisterCard, TResult> onNotARegisterCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onAlreadyDischarged(this);

        public string RefusingRule => "register: discharging a card records the acting role and the time exactly once";

        public string Remedy => $"'{FilePath}' is already discharged; there is nothing left to discharge.";
    }

    /// <summary>The target card's own <c>status</c> does not parse as <see cref="RegisterLifecycleState"/>
    /// — register: "SHALL NOT occupy flow states", enforced here as a real, exercised refusal rather
    /// than a documented intention. Refusal-shaped.</summary>
    internal sealed record InvalidStatus(string FilePath, string Status) : CardRegisterDischargeOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Discharged, TResult> onDischarged, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARegisterCard, TResult> onNotARegisterCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onInvalidStatus(this);

        public string RefusingRule => "register: register cards SHALL NOT occupy flow states";

        public string Remedy =>
            $"'{FilePath}' has status '{Status}', which is not a recognised register lifecycle state " +
            $"({RegisterLifecycleStateWireFormat.RecognisedValues}); correct the card's own 'status' field before discharging it.";
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not one of the four
    /// register kinds. Refusal-shaped.</summary>
    internal sealed record NotARegisterCard(CardKind Kind) : CardRegisterDischargeOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Discharged, TResult> onDischarged, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARegisterCard, TResult> onNotARegisterCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onNotARegisterCard(this);

        public string RefusingRule => "register: discharge applies only to a register card";

        public string Remedy => "target a card whose kind is one of the four register kinds.";
    }

    /// <summary>No card exists at the target path. Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardRegisterDischargeOutcome
    {
        internal override TResult Match<TResult>(Func<Discharged, TResult> onDischarged, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARegisterCard, TResult> onNotARegisterCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardNotFound(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>). Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardRegisterDischargeOutcome
    {
        internal override TResult Match<TResult>(Func<Discharged, TResult> onDischarged, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARegisterCard, TResult> onNotARegisterCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but could not be parsed. Neither refusal nor tool-failure.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardRegisterDischargeOutcome
    {
        internal override TResult Match<TResult>(Func<Discharged, TResult> onDischarged, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARegisterCard, TResult> onNotARegisterCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardCorrupt(this);
    }

    /// <summary>working-context: "No figure SHALL be hand-entered anywhere in the system" (§10
    /// block C) — <paramref name="Key"/> names a reserved derived-state field (<see
    /// cref="DerivedStateFieldKeys.All"/>) present on the target card's <see cref="CardFile.
    /// UnknownFrontmatterFields"/>, the door a hand-edited card's frontmatter uses to reach this far
    /// at all (nothing this build's own CLI ever writes one). Refusal-shaped, card-addressed (§9
    /// block A3): checked immediately once the card is read, before discharging the card is allowed to
    /// proceed, so this write never re-emits (and never launders forward) a hand-entered count or
    /// next-step pin it did not itself write. See <see cref="CardWriteResult.HandEnteredDerivedState"/>
    /// for the sibling case on the generic comment/handover surface.</summary>
    internal sealed record HandEnteredDerivedState(string Key) : CardRegisterDischargeOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Discharged, TResult> onDischarged, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARegisterCard, TResult> onNotARegisterCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onHandEnteredDerivedState(this);

        public string RefusingRule => "working-context: no figure shall be hand-entered";

        public string Remedy =>
            $"'{Key}' is a reserved derived-state field name; remove it from this card's frontmatter — " +
            "this state is derived at request time, never stored, and is available from 'callboard state'.";
    }

    /// <summary>Enforcement itself is unavailable: the card's lock could not be acquired within
    /// its timeout, or an I/O error occurred while writing. Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardRegisterDischargeOutcome
    {
        internal override TResult Match<TResult>(Func<Discharged, TResult> onDischarged, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARegisterCard, TResult> onNotARegisterCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onToolFailure(this);
    }
}
