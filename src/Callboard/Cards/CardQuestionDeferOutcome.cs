namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.DeferQuestion"/> can end (§9 block D — the
/// <c>deferred</c> half of the question status vocabulary block E's <c>9.5</c> reads: "Refuse
/// section close over open undeferred questions" has no meaning until a question can actually be
/// deferred). Built in the refusal-recording format from the start, the same carve standing rule
/// <see cref="CardQuestionAnswerOutcome"/> follows — see that type's own doc comment for why a
/// missing <c>--target</c> is refused at parse, never a case here.
/// </summary>
internal abstract record CardQuestionDeferOutcome
{
    private CardQuestionDeferOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Deferred, TResult> onDeferred,
        Func<NotAQuestionCard, TResult> onNotAQuestionCard,
        Func<NotOpen, TResult> onNotOpen,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState);

    /// <param name="Card">The card as written: <c>status: deferred</c>,
    /// <see cref="QuestionCardFields.DeferredTarget"/>, and <see cref="QuestionCardFields.
    /// DeferredBy"/>/<see cref="QuestionCardFields.DeferredAt"/>.</param>
    internal sealed record Deferred(CardFile Card) : CardQuestionDeferOutcome
    {
        internal override TResult Match<TResult>(Func<Deferred, TResult> onDeferred, Func<NotAQuestionCard, TResult> onNotAQuestionCard, Func<NotOpen, TResult> onNotOpen, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onDeferred(this);
    }

    /// <summary>The target resolved, under lock, to a card whose kind is not <c>question</c>.
    /// Refusal-shaped and card-addressed — recorded.</summary>
    internal sealed record NotAQuestionCard(CardKind Kind) : CardQuestionDeferOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Deferred, TResult> onDeferred, Func<NotAQuestionCard, TResult> onNotAQuestionCard, Func<NotOpen, TResult> onNotOpen, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onNotAQuestionCard(this);

        public string RefusingRule => "process-enforcement: a deferral targets a question card";

        public string Remedy => "target a card whose kind is 'question'.";
    }

    /// <summary>The target question's <c>status</c> is not <see cref="QuestionStatus.Open"/> —
    /// already answered, or already deferred. Refusal-shaped and card-addressed — recorded.</summary>
    internal sealed record NotOpen(QuestionStatus CurrentStatus) : CardQuestionDeferOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Deferred, TResult> onDeferred, Func<NotAQuestionCard, TResult> onNotAQuestionCard, Func<NotOpen, TResult> onNotOpen, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onNotOpen(this);

        public string RefusingRule => "process-enforcement: a question is deferred only from 'open'";

        public string Remedy => $"this question is already '{CurrentStatus.ToWireString()}' — a deferral is recorded once.";
    }

    /// <summary>No card exists at the target path. Never recorded — there is nothing to read.</summary>
    internal sealed record CardNotFound(string FilePath) : CardQuestionDeferOutcome
    {
        internal override TResult Match<TResult>(Func<Deferred, TResult> onDeferred, Func<NotAQuestionCard, TResult> onNotAQuestionCard, Func<NotOpen, TResult> onNotOpen, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardNotFound(this);
    }

    /// <summary>The target's path does not anchor under the record's own scope-shaped layout.
    /// Categorical, never recorded.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardQuestionDeferOutcome
    {
        internal override TResult Match<TResult>(Func<Deferred, TResult> onDeferred, Func<NotAQuestionCard, TResult> onNotAQuestionCard, Func<NotOpen, TResult> onNotOpen, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onLayoutMismatch(this);
    }

    /// <summary>The target file exists but does not parse. Never recorded.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardQuestionDeferOutcome
    {
        internal override TResult Match<TResult>(Func<Deferred, TResult> onDeferred, Func<NotAQuestionCard, TResult> onNotAQuestionCard, Func<NotOpen, TResult> onNotOpen, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardCorrupt(this);
    }

    /// <summary>working-context: "No figure SHALL be hand-entered anywhere in the system" (§10
    /// block C) — <paramref name="Key"/> names a reserved derived-state field (<see
    /// cref="DerivedStateFieldKeys.All"/>) present on the target card's <see cref="CardFile.
    /// UnknownFrontmatterFields"/>, the door a hand-edited card's frontmatter uses to reach this far
    /// at all (nothing this build's own CLI ever writes one). Refusal-shaped, card-addressed (§9
    /// block A3): checked immediately once the card is read, before deferring the question is allowed to
    /// proceed, so this write never re-emits (and never launders forward) a hand-entered count or
    /// next-step pin it did not itself write. See <see cref="CardWriteResult.HandEnteredDerivedState"/>
    /// for the sibling case on the generic comment/handover surface.</summary>
    internal sealed record HandEnteredDerivedState(string Key) : CardQuestionDeferOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Deferred, TResult> onDeferred, Func<NotAQuestionCard, TResult> onNotAQuestionCard, Func<NotOpen, TResult> onNotOpen, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onHandEnteredDerivedState(this);

        public string RefusingRule => "working-context: no figure shall be hand-entered";

        public string Remedy =>
            $"'{Key}' is a reserved derived-state field name; remove it from this card's frontmatter — " +
            "this state is derived at request time, never stored, and is available from 'callboard state'.";
    }

    /// <summary>Enforcement was unavailable (ADR-0001). Never a refusal.</summary>
    internal sealed record ToolFailure(string Reason) : CardQuestionDeferOutcome
    {
        internal override TResult Match<TResult>(Func<Deferred, TResult> onDeferred, Func<NotAQuestionCard, TResult> onNotAQuestionCard, Func<NotOpen, TResult> onNotOpen, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onToolFailure(this);
    }
}
