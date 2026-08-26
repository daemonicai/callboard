namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.PromoteObligation"/> can end (§9 block F, register:
/// "Promotion SHALL NOT be limited to rules... An <c>obligation</c> that outlives the change it was
/// raised in SHALL be promotable to a wider scope on the same terms — the same card, retaining its
/// identity, text and thread"). Same split-by-disposition reasoning as <see cref="CardRulePromoteOutcome"/>,
/// which this mirrors case for case — the two verbs share every failure shape because they share the
/// same move-then-rewrite mechanics, differing only in which <see cref="CardKind"/> is legal and the
/// refusal text naming it.
/// </summary>
internal abstract record CardObligationPromoteOutcome
{
    private CardObligationPromoteOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Promoted, TResult> onPromoted,
        Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped,
        Func<NotChangeScoped, TResult> onNotChangeScoped,
        Func<InvalidStatus, TResult> onInvalidStatus,
        Func<NotAnObligationCard, TResult> onNotAnObligationCard,
        Func<TargetAlreadyExists, TResult> onTargetAlreadyExists,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="Card">The card exactly as written after promotion — same id, same body, same
    /// comments, only <c>scope</c> and <c>updated</c> differ from what was read at the start of the
    /// call.</param>
    /// <param name="OldFilePath">Where the card lived before promotion.</param>
    /// <param name="NewFilePath">Where the card lives now — inside <see cref="CardLayout.
    /// RegisterDirectory"/>.</param>
    internal sealed record Promoted(CardFile Card, string OldFilePath, string NewFilePath) : CardObligationPromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onPromoted(this);
    }

    /// <summary>The obligation is already <see cref="CardScope.Repository"/>-scoped. Refusal-shaped.
    /// </summary>
    internal sealed record AlreadyRepositoryScoped(string FilePath) : CardObligationPromoteOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onAlreadyRepositoryScoped(this);

        public string RefusingRule => "register: promoting an already-repository-scoped obligation is a refusal too";

        public string Remedy => $"'{FilePath}' is already repository-scoped; there is nothing to promote.";
    }

    /// <summary>The obligation carries a <see cref="CardScope"/> other than <see cref="CardScope.
    /// Change"/> or <see cref="CardScope.Repository"/> — unreachable through this codebase's own
    /// writers, kept for the same reason <see cref="CardRulePromoteOutcome.NotChangeScoped"/> is.
    /// Refusal-shaped.</summary>
    internal sealed record NotChangeScoped(CardScope Scope, string FilePath) : CardObligationPromoteOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotChangeScoped(this);

        public string RefusingRule => "register: promotion moves a change-scoped obligation to repository scope, nothing else";

        public string Remedy => $"'{FilePath}' is '{Scope.ToWireString()}'-scoped; only a 'change'-scoped obligation can be promoted.";
    }

    /// <summary>The obligation's own <c>status</c> does not parse as <see cref="RegisterLifecycleState"/>
    /// — register: "SHALL NOT occupy flow states". Refusal-shaped.</summary>
    internal sealed record InvalidStatus(string FilePath, string Status) : CardObligationPromoteOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onInvalidStatus(this);

        public string RefusingRule => "register: register cards SHALL NOT occupy flow states";

        public string Remedy =>
            $"'{FilePath}' has status '{Status}', which is not a recognised register lifecycle state " +
            $"({RegisterLifecycleStateWireFormat.RecognisedValues}); correct the card's own 'status' field before promoting it.";
    }

    /// <summary>The resolved card is not an <c>obligation</c>. Refusal-shaped.</summary>
    internal sealed record NotAnObligationCard(CardKind Kind) : CardObligationPromoteOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotAnObligationCard(this);

        public string RefusingRule => "register: promotion applies to obligation cards";

        public string Remedy => "target a card whose kind is 'obligation'.";
    }

    /// <summary>A file already occupies the exact path this obligation would move to inside
    /// <see cref="CardLayout.RegisterDirectory"/>. Refusal-shaped.</summary>
    internal sealed record TargetAlreadyExists(string FilePath) : CardObligationPromoteOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onTargetAlreadyExists(this);

        public string RefusingRule => "card-model: identities are never recycled, and a promotion must not overwrite an unrelated card";

        public string Remedy => $"'{FilePath}' already exists at the promotion target; resolve the collision before retrying.";
    }

    /// <summary>No card file exists at the resolved path (a race between resolution and locking).
    /// Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardObligationPromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardNotFound(this);
    }

    /// <summary>The computed target path does not resolve under <see cref="CardLayout.
    /// RegisterDirectory"/> for the given root — unreachable in practice, kept for the same reason
    /// every other <c>CardStore</c> write surface carries this case. Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardObligationPromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but could not be parsed. Neither refusal nor tool-failure.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardObligationPromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardCorrupt(this);
    }

    /// <summary>
    /// Enforcement itself is unavailable, or the two-step move-then-edit did not finish — see
    /// <see cref="CardStore.PromoteObligation"/>'s own doc comment for the exact phase-one/phase-two
    /// failure shapes this mirrors from <see cref="CardStore.PromoteRule"/>. Tool-failure-shaped.
    /// </summary>
    internal sealed record ToolFailure(string Reason) : CardObligationPromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotAnObligationCard, TResult> onNotAnObligationCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
