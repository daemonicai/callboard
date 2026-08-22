namespace Callboard.Cards;

/// <summary>
/// Closed union over how closing a section (§5 block E, <see cref="CardStore.CloseSection"/>) can
/// end. Same shape and reasoning as <see cref="CardSectionVerdictOutcome"/>, plus
/// <see cref="AlreadyClosed"/> — closing records the acting role and the time exactly once
/// (work-lifecycle: "closing it SHALL record the acting role and the time"), so a second close
/// attempt is refused rather than silently overwriting who closed it first.
///
/// <para>
/// <b>What this type does not decide (§5 block E brief — "the closing conditions belong to §9,
/// not to you").</b> <see cref="CardStore.CloseSectionUnderExistingLock"/> never checks open
/// obligations, undeferred questions, or unresolved threads before closing — those refusals are
/// 9.6/9.7/9.8's, layered by a caller of this method, not built into it. This union's cases are
/// exhaustively about the section entity's own state (already closed, wrong kind, missing, corrupt,
/// unavailable) — never about what a section is permitted to close over.
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
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="Card">The card as written, now carrying <c>status: closed</c> and its
    /// <c>closed_by</c>/<c>closed_at</c> fields.</param>
    internal sealed record Closed(CardFile Card) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onClosed(this);
    }

    /// <summary>The target section is already closed. Refusal-shaped — closing does not
    /// re-record a new acting role/time over the one already recorded.</summary>
    internal sealed record AlreadyClosed(string FilePath) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onAlreadyClosed(this);
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>section</c>.
    /// Refusal-shaped.</summary>
    internal sealed record NotASectionCard(CardKind Kind) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotASectionCard(this);
    }

    /// <summary>No card exists at the target path. Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardNotFound(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>). Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but could not be parsed. Neither refusal nor tool-failure.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardCorrupt(this);
    }

    /// <summary>Enforcement itself is unavailable: the card's lock could not be acquired within
    /// its timeout, or an I/O error occurred while writing. Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardSectionCloseOutcome
    {
        internal override TResult Match<TResult>(Func<Closed, TResult> onClosed, Func<AlreadyClosed, TResult> onAlreadyClosed, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
