namespace Callboard.Cards;

/// <summary>
/// Closed union over how recording a section verdict (§5 block E, <see cref="CardStore.
/// RecordSectionVerdict"/>) can end. Same shape and same reasoning as
/// <see cref="CardGateResultOutcome"/> — a caller-correctable refusal
/// (<see cref="NotASectionCard"/>, <see cref="CardNotFound"/>, <see cref="LayoutMismatch"/>) is kept
/// structurally apart from a reported problem with the record's own content
/// (<see cref="CardCorrupt"/>) and from enforcement itself being unavailable
/// (<see cref="ToolFailure"/>).
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
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="Card">The card as written, carrying the newly appended verdict entry.</param>
    /// <param name="Entry">The verdict entry actually recorded.</param>
    internal sealed record Recorded(CardFile Card, SectionVerdictEntry Entry) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onRecorded(this);
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>section</c> —
    /// verdicts are only recorded on a section card. Refusal-shaped.</summary>
    internal sealed record NotASectionCard(CardKind Kind) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotASectionCard(this);
    }

    /// <summary>No card exists at the target path. Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardNotFound(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>). Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but could not be parsed. Neither refusal nor tool-failure — a
    /// reported problem with the record's own content.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardCorrupt(this);
    }

    /// <summary>Enforcement itself is unavailable: the card's lock could not be acquired within
    /// its timeout, or an I/O error occurred while writing. Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardSectionVerdictOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotASectionCard, TResult> onNotASectionCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
