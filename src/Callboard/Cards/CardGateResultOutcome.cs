namespace Callboard.Cards;

/// <summary>
/// Closed union over how recording a gate result (§5 block D, <see cref="CardStore.
/// RecordGateResult"/>) can end. Same shape and same reasoning as
/// <see cref="CardBlockTransitionOutcome"/> — a caller-correctable refusal
/// (<see cref="NotABlockCard"/>, <see cref="CardNotFound"/>, <see cref="LayoutMismatch"/>) is kept
/// structurally apart from a reported problem with the record's own content
/// (<see cref="CardCorrupt"/>) and from enforcement itself being unavailable
/// (<see cref="ToolFailure"/>), so a caller wired over this type cannot fold "the tool broke" into
/// "you are wrong" the way §5 block C's first remediation round found <c>CardWriteResult</c> had
/// let happen.
/// </summary>
internal abstract record CardGateResultOutcome
{
    private CardGateResultOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Recorded, TResult> onRecorded,
        Func<NotABlockCard, TResult> onNotABlockCard,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="Card">The card as written, carrying the newly recorded (or replaced) gate
    /// result.</param>
    /// <param name="Result">The gate result actually recorded.</param>
    /// <param name="ActingRole">The role that recorded this gate result (§5 remediation, DEVLOG §5
    /// finding B1) — not persisted on <see cref="Result"/> itself (work-lifecycle only requires a
    /// gate result to carry a label and an exit code), but required here so a caller mapping this
    /// outcome to a CLI result can surface who recorded it without falling back to re-reading the
    /// parsed command, the same way <see cref="CardSectionVerdictOutcome.Recorded.Entry"/> already
    /// carries <c>By</c>.</param>
    internal sealed record Recorded(CardFile Card, GateResult Result, CardOwner ActingRole) : CardGateResultOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onRecorded(this);
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>block</c> — gate
    /// results are only recorded on a block card. Refusal-shaped.</summary>
    internal sealed record NotABlockCard(CardKind Kind) : CardGateResultOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotABlockCard(this);
    }

    /// <summary>No card exists at the target path. Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardGateResultOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardNotFound(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>). Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardGateResultOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but could not be parsed. Neither refusal nor tool-failure — a
    /// reported problem with the record's own content. A caller wired over this type must not
    /// route it to a refusal exit.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardGateResultOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardCorrupt(this);
    }

    /// <summary>Enforcement itself is unavailable: the card's lock could not be acquired within
    /// its timeout, or an I/O error occurred while writing. Tool-failure-shaped. A caller wired
    /// over this type must let it reach a tool-failure exit (ADR-0001).</summary>
    internal sealed record ToolFailure(string Reason) : CardGateResultOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
