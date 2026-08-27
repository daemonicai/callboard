namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.PromoteRule"/> can end (§7 block E, register:
/// "Promoting a change-scoped rule to repository scope SHALL move the same card, retaining its
/// identity, text and thread"). Same split-by-disposition reasoning as <see cref="CardCreateOutcome"/>/
/// <see cref="CardRegisterDischargeOutcome"/>.
///
/// <para>
/// <b>No <c>InvalidStatus</c> case (§12 block A).</b> See <see cref="CardRegisterDischargeOutcome"/>'s
/// own doc comment: <see cref="CardFileParser"/> now validates a register card's own <c>status</c>
/// at the parse door, so <see cref="CardCorrupt"/> carries that refusal's reason instead.
/// </para>
/// </summary>
internal abstract record CardRulePromoteOutcome
{
    private CardRulePromoteOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Promoted, TResult> onPromoted,
        Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped,
        Func<NotChangeScoped, TResult> onNotChangeScoped,
        Func<NotARuleCard, TResult> onNotARuleCard,
        Func<TargetAlreadyExists, TResult> onTargetAlreadyExists,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState);

    /// <param name="Card">The card exactly as written after promotion — same id, same body, same
    /// comments (<see cref="CardFile.Comments"/> is untouched by this method entirely), only
    /// <c>scope</c> and <c>updated</c> differ from what was read at the start of the call.</param>
    /// <param name="OldFilePath">Where the card lived before promotion.</param>
    /// <param name="NewFilePath">Where the card lives now — inside <see cref="CardLayout.
    /// RegisterDirectory"/>.</param>
    internal sealed record Promoted(CardFile Card, string OldFilePath, string NewFilePath) : CardRulePromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onPromoted(this);
    }

    /// <summary>The rule is already <see cref="CardScope.Repository"/>-scoped — "promoting an
    /// already-repository-scoped rule is a refusal too" (§7 block E brief item 3). Refusal-shaped.
    /// </summary>
    internal sealed record AlreadyRepositoryScoped(string FilePath) : CardRulePromoteOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onAlreadyRepositoryScoped(this);

        public string RefusingRule => "register: promoting an already-repository-scoped rule is a refusal too";

        public string Remedy => $"'{FilePath}' is already repository-scoped; there is nothing to promote.";
    }

    /// <summary>The rule carries a <see cref="CardScope"/> other than <see cref="CardScope.Change"/>
    /// or <see cref="CardScope.Repository"/> — unreachable through this codebase's own writers
    /// (<see cref="CardScopeRules.Validate"/> never lets a rule take <see cref="CardScope.Section"/>
    /// or <see cref="CardScope.Capability"/>), but a hand-edited file can still say it, and this
    /// method never treats a case <see cref="CardScopeRules"/> forbids as if it were the one
    /// scope promotion actually knows how to move from. Refusal-shaped.</summary>
    internal sealed record NotChangeScoped(CardScope Scope, string FilePath) : CardRulePromoteOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onNotChangeScoped(this);

        public string RefusingRule => "register: promotion moves a change-scoped rule to repository scope, nothing else";

        public string Remedy => $"'{FilePath}' is '{Scope.ToWireString()}'-scoped; only a 'change'-scoped rule can be promoted.";
    }

    /// <summary>The resolved card is not a <c>rule</c> — "promotion applies to rules" (§7 block E
    /// brief item 3): every other register kind has exactly one legal scope, so promoting it would
    /// be meaningless, not merely unsupported. Refusal-shaped.</summary>
    internal sealed record NotARuleCard(CardKind Kind) : CardRulePromoteOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onNotARuleCard(this);

        public string RefusingRule => "register: promotion applies to rule cards";

        public string Remedy => "target a card whose kind is 'rule'.";
    }

    /// <summary>A file already occupies the exact path this rule would move to inside
    /// <see cref="CardLayout.RegisterDirectory"/> (its own basename, unchanged by promotion, already
    /// claimed by an unrelated card). Refusal-shaped — checked before <see cref="File.Move(string,
    /// string)"/> is attempted, so a collision never partially moves anything.</summary>
    internal sealed record TargetAlreadyExists(string FilePath) : CardRulePromoteOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onTargetAlreadyExists(this);

        public string RefusingRule => "card-model: identities are never recycled, and a promotion must not overwrite an unrelated card";

        public string Remedy => $"'{FilePath}' already exists at the promotion target; resolve the collision before retrying.";
    }

    /// <summary>No card file exists at the resolved path (a race between resolution and locking).
    /// Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardRulePromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardNotFound(this);
    }

    /// <summary>The computed target path does not resolve under <see cref="CardLayout.
    /// RegisterDirectory"/> for the given root (<see cref="AnchoredCardPath.TryCreate"/>) —
    /// unreachable in practice since this method computes the target itself, kept for the same
    /// "the anchoring check can refuse, so the outcome type must be able to say so" reason every
    /// other <c>CardStore</c> write surface carries this case. Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardRulePromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but could not be parsed. Neither refusal nor tool-failure.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardRulePromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardCorrupt(this);
    }

    /// <summary>working-context: "No figure SHALL be hand-entered anywhere in the system" (§10
    /// block C) — <paramref name="Key"/> names a reserved derived-state field (<see
    /// cref="DerivedStateFieldKeys.All"/>) present on the target card's <see cref="CardFile.
    /// UnknownFrontmatterFields"/>, the door a hand-edited card's frontmatter uses to reach this far
    /// at all (nothing this build's own CLI ever writes one). Refusal-shaped, card-addressed (§9
    /// block A3): checked immediately once the card is read, before promoting the rule is allowed to
    /// proceed, so this write never re-emits (and never launders forward) a hand-entered count or
    /// next-step pin it did not itself write. See <see cref="CardWriteResult.HandEnteredDerivedState"/>
    /// for the sibling case on the generic comment/handover surface.</summary>
    internal sealed record HandEnteredDerivedState(string Key) : CardRulePromoteOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onHandEnteredDerivedState(this);

        public string RefusingRule => "working-context: no figure shall be hand-entered";

        public string Remedy =>
            $"'{Key}' is a reserved derived-state field name; remove it from this card's frontmatter — " +
            "this state is derived at request time, never stored, and is available from 'callboard state'.";
    }

    /// <summary>
    /// Enforcement itself is unavailable, or the two-step move-then-edit did not finish — the lock
    /// could not be acquired within its timeout, <see cref="File.Move(string, string)"/> threw (phase
    /// one: nothing moved, the rule is still live at its old path, old scope, unmodified), or the
    /// frontmatter rewrite at the new location threw after the move already landed (phase two: the
    /// rule now physically lives under <see cref="CardLayout.RegisterDirectory"/> but its own
    /// <c>scope</c> field still reads <c>change</c> until this same call is retried — see
    /// <see cref="CardStore.PromoteRule"/>'s own doc comment for why a retry against the same id
    /// self-heals this exact state rather than getting stuck on it). Tool-failure-shaped, the same
    /// "do not claim atomicity this does not have" discipline <see cref="ChangeArchiveOutcome"/>'s
    /// own two-phase doc comment states for archive's directory move.
    /// </summary>
    internal sealed record ToolFailure(string Reason) : CardRulePromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onToolFailure(this);
    }
}
