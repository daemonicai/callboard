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
        Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState);

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
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onRecorded(this);
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>block</c> — gate
    /// results are only recorded on a block card. Refusal-shaped, and card-addressed (§9 block A3):
    /// resolved under lock before this check runs.</summary>
    internal sealed record NotABlockCard(CardKind Kind) : CardGateResultOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onNotABlockCard(this);

        public string RefusingRule => "work-lifecycle: gate results only apply to a block card";

        public string Remedy => "target a card whose kind is 'block'.";
    }

    /// <summary>No card exists at the target path. Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardGateResultOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardNotFound(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>). Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardGateResultOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but could not be parsed. Neither refusal nor tool-failure — a
    /// reported problem with the record's own content. A caller wired over this type must not
    /// route it to a refusal exit.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardGateResultOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardCorrupt(this);
    }

    /// <summary>work-lifecycle: "Stored round agrees with the transition history" (8a.17) — the
    /// block card's stored <c>round</c> does not equal one plus the number of round-incrementing
    /// transitions (<see cref="BlockFlowTransitions.RoundIncrementingTransitionNames"/>) in its own
    /// <see cref="CardFile.Transitions"/> history. Refusal-shaped: neither figure is privileged and
    /// neither is altered — a stored count ahead of the history and a history ahead of the count are
    /// different failures, and guessing which is right would silently destroy the evidence of
    /// whichever was correct.</summary>
    internal sealed record RoundDisagreesWithHistory(int StoredRound, int ExpectedRound) : CardGateResultOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onRoundDisagreesWithHistory(this);

        public string RefusingRule => "work-lifecycle: stored round agrees with the transition history";

        public string Remedy =>
            $"the recorded round ({StoredRound}) disagrees with the transition history ({ExpectedRound}); " +
            "correct whichever was altered outside the tool before this gate result can be recorded.";
    }

    /// <summary>working-context: "No figure SHALL be hand-entered anywhere in the system" (§10
    /// block C) — <paramref name="Key"/> names a reserved derived-state field (<see
    /// cref="DerivedStateFieldKeys.All"/>) present on the target card's <see cref="CardFile.
    /// UnknownFrontmatterFields"/>, the door a hand-edited card's frontmatter uses to reach this far
    /// at all (nothing this build's own CLI ever writes one). Refusal-shaped, card-addressed (§9
    /// block A3): checked immediately once the card is read, before recording the gate result is allowed to
    /// proceed, so this write never re-emits (and never launders forward) a hand-entered count or
    /// next-step pin it did not itself write. See <see cref="CardWriteResult.HandEnteredDerivedState"/>
    /// for the sibling case on the generic comment/handover surface.</summary>
    internal sealed record HandEnteredDerivedState(string Key) : CardGateResultOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onHandEnteredDerivedState(this);

        public string RefusingRule => "working-context: no figure shall be hand-entered";

        public string Remedy =>
            $"'{Key}' is a reserved derived-state field name; remove it from this card's frontmatter — " +
            "this state is derived at request time, never stored, and is available from 'callboard state'.";
    }

    /// <summary>Enforcement itself is unavailable: the card's lock could not be acquired within
    /// its timeout, or an I/O error occurred while writing. Tool-failure-shaped. A caller wired
    /// over this type must let it reach a tool-failure exit (ADR-0001).</summary>
    internal sealed record ToolFailure(string Reason) : CardGateResultOutcome
    {
        internal override TResult Match<TResult>(Func<Recorded, TResult> onRecorded, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onToolFailure(this);
    }
}
