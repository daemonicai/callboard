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
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="Card">The card as written, now carrying <c>status: discharged</c> and its
    /// <c>discharged_by</c>/<c>discharged_at</c> fields.</param>
    internal sealed record Discharged(CardFile Card) : CardRegisterDischargeOutcome
    {
        internal override TResult Match<TResult>(Func<Discharged, TResult> onDischarged, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARegisterCard, TResult> onNotARegisterCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onDischarged(this);
    }

    /// <summary>The target register card is already discharged. Refusal-shaped — discharging does
    /// not re-record a new acting role/time over the one already recorded.</summary>
    internal sealed record AlreadyDischarged(string FilePath) : CardRegisterDischargeOutcome
    {
        internal override TResult Match<TResult>(Func<Discharged, TResult> onDischarged, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARegisterCard, TResult> onNotARegisterCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onAlreadyDischarged(this);
    }

    /// <summary>The target card's own <c>status</c> does not parse as <see cref="RegisterLifecycleState"/>
    /// — register: "SHALL NOT occupy flow states", enforced here as a real, exercised refusal rather
    /// than a documented intention. Refusal-shaped.</summary>
    internal sealed record InvalidStatus(string FilePath, string Status) : CardRegisterDischargeOutcome
    {
        internal override TResult Match<TResult>(Func<Discharged, TResult> onDischarged, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARegisterCard, TResult> onNotARegisterCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onInvalidStatus(this);
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not one of the four
    /// register kinds. Refusal-shaped.</summary>
    internal sealed record NotARegisterCard(CardKind Kind) : CardRegisterDischargeOutcome
    {
        internal override TResult Match<TResult>(Func<Discharged, TResult> onDischarged, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARegisterCard, TResult> onNotARegisterCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotARegisterCard(this);
    }

    /// <summary>No card exists at the target path. Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardRegisterDischargeOutcome
    {
        internal override TResult Match<TResult>(Func<Discharged, TResult> onDischarged, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARegisterCard, TResult> onNotARegisterCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardNotFound(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>). Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardRegisterDischargeOutcome
    {
        internal override TResult Match<TResult>(Func<Discharged, TResult> onDischarged, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARegisterCard, TResult> onNotARegisterCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but could not be parsed. Neither refusal nor tool-failure.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardRegisterDischargeOutcome
    {
        internal override TResult Match<TResult>(Func<Discharged, TResult> onDischarged, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARegisterCard, TResult> onNotARegisterCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardCorrupt(this);
    }

    /// <summary>Enforcement itself is unavailable: the card's lock could not be acquired within
    /// its timeout, or an I/O error occurred while writing. Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardRegisterDischargeOutcome
    {
        internal override TResult Match<TResult>(Func<Discharged, TResult> onDischarged, Func<AlreadyDischarged, TResult> onAlreadyDischarged, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARegisterCard, TResult> onNotARegisterCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
