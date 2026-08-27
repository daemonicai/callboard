namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.AnswerQuestion"/> can end (§9 block D,
/// process-enforcement: "An answer must be written down"). Built in the refusal-recording format
/// from the start (the carve's standing rule for any outcome union §9 mints) — <see cref="NotAQuestionCard"/>
/// and <see cref="NotOpen"/> both implement <see cref="ICardRefusalReason"/> and record.
///
/// <para>
/// <b>Whether an answer was actually supplied is not this union's job (Architect ruling, §9 block
/// D).</b> Naming a <c>decision</c> card or recording an inline answer is decidable from argv alone
/// — the same "argv-decidable" shape <see cref="Callboard.Cli.CommandParser.ParseObligationCreate"/>
/// already established for a missing <c>--section</c> — so <see cref="Callboard.Cli.CommandParser.
/// ParseQuestionAnswer"/> refuses a call naming neither before this method, or any card, is ever
/// reached. That refusal never resolves a card, so per the base ruling (§9 architect ruling: "only a
/// card-addressed refusal records") it is a plain <c>CommandOutcome.Refusal</c>, not a case here —
/// there is nothing of a question's own state for it to depend on.
/// </para>
/// </summary>
internal abstract record CardQuestionAnswerOutcome
{
    private CardQuestionAnswerOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Answered, TResult> onAnswered,
        Func<NotAQuestionCard, TResult> onNotAQuestionCard,
        Func<NotOpen, TResult> onNotOpen,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState);

    /// <param name="Card">The card as written: <c>status: answered</c> and whichever of
    /// <see cref="QuestionCardFields.AnswerDecisionId"/>/<see cref="QuestionCardFields.AnswerInline"/>
    /// was supplied, alongside <see cref="QuestionCardFields.AnsweredBy"/>/<see cref="
    /// QuestionCardFields.AnsweredAt"/>.</param>
    internal sealed record Answered(CardFile Card) : CardQuestionAnswerOutcome
    {
        internal override TResult Match<TResult>(Func<Answered, TResult> onAnswered, Func<NotAQuestionCard, TResult> onNotAQuestionCard, Func<NotOpen, TResult> onNotOpen, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onAnswered(this);
    }

    /// <summary>The target resolved, under lock, to a card whose kind is not <c>question</c>.
    /// Refusal-shaped: caller-correctable, and card-addressed (the read that discovered the wrong
    /// kind is the same read this refusal records against) — recorded.</summary>
    internal sealed record NotAQuestionCard(CardKind Kind) : CardQuestionAnswerOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Answered, TResult> onAnswered, Func<NotAQuestionCard, TResult> onNotAQuestionCard, Func<NotOpen, TResult> onNotOpen, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onNotAQuestionCard(this);

        public string RefusingRule => "process-enforcement: an answer targets a question card";

        public string Remedy => "target a card whose kind is 'question'.";
    }

    /// <summary>The target question's <c>status</c> is not <see cref="QuestionStatus.Open"/> —
    /// already answered, or deferred. Refusal-shaped and card-addressed — recorded.</summary>
    internal sealed record NotOpen(QuestionStatus CurrentStatus) : CardQuestionAnswerOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Answered, TResult> onAnswered, Func<NotAQuestionCard, TResult> onNotAQuestionCard, Func<NotOpen, TResult> onNotOpen, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onNotOpen(this);

        public string RefusingRule => "process-enforcement: a question is answered only from 'open'";

        public string Remedy => $"this question is already '{CurrentStatus.ToWireString()}' — an answer is recorded once.";
    }

    /// <summary>No card exists at the target path. Refusal-shaped: caller-correctable, but never
    /// card-addressed (there is nothing to read) — never recorded, per the base ruling.</summary>
    internal sealed record CardNotFound(string FilePath) : CardQuestionAnswerOutcome
    {
        internal override TResult Match<TResult>(Func<Answered, TResult> onAnswered, Func<NotAQuestionCard, TResult> onNotAQuestionCard, Func<NotOpen, TResult> onNotOpen, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardNotFound(this);
    }

    /// <summary>The target's path does not anchor under the record's own scope-shaped layout.
    /// Categorical, never recorded — see the base ruling.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardQuestionAnswerOutcome
    {
        internal override TResult Match<TResult>(Func<Answered, TResult> onAnswered, Func<NotAQuestionCard, TResult> onNotAQuestionCard, Func<NotOpen, TResult> onNotOpen, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onLayoutMismatch(this);
    }

    /// <summary>The target file exists but does not parse. Never recorded — there is nothing
    /// readable to record against.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardQuestionAnswerOutcome
    {
        internal override TResult Match<TResult>(Func<Answered, TResult> onAnswered, Func<NotAQuestionCard, TResult> onNotAQuestionCard, Func<NotOpen, TResult> onNotOpen, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardCorrupt(this);
    }

    /// <summary>working-context: "No figure SHALL be hand-entered anywhere in the system" (§10
    /// block C) — <paramref name="Key"/> names a reserved derived-state field (<see
    /// cref="DerivedStateFieldKeys.All"/>) present on the target card's <see cref="CardFile.
    /// UnknownFrontmatterFields"/>, the door a hand-edited card's frontmatter uses to reach this far
    /// at all (nothing this build's own CLI ever writes one). Refusal-shaped, card-addressed (§9
    /// block A3): checked immediately once the card is read, before answering the question is allowed to
    /// proceed, so this write never re-emits (and never launders forward) a hand-entered count or
    /// next-step pin it did not itself write. See <see cref="CardWriteResult.HandEnteredDerivedState"/>
    /// for the sibling case on the generic comment/handover surface.</summary>
    internal sealed record HandEnteredDerivedState(string Key) : CardQuestionAnswerOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Answered, TResult> onAnswered, Func<NotAQuestionCard, TResult> onNotAQuestionCard, Func<NotOpen, TResult> onNotOpen, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onHandEnteredDerivedState(this);

        public string RefusingRule => "working-context: no figure shall be hand-entered";

        public string Remedy =>
            $"'{Key}' is a reserved derived-state field name; remove it from this card's frontmatter — " +
            "this state is derived at request time, never stored, and is available from 'callboard state'.";
    }

    /// <summary>Enforcement was unavailable (ADR-0001) — a lock timeout, or a refusal's own record
    /// write failing. Never a refusal: the board never got to say no.</summary>
    internal sealed record ToolFailure(string Reason) : CardQuestionAnswerOutcome
    {
        internal override TResult Match<TResult>(Func<Answered, TResult> onAnswered, Func<NotAQuestionCard, TResult> onNotAQuestionCard, Func<NotOpen, TResult> onNotOpen, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onToolFailure(this);
    }
}
