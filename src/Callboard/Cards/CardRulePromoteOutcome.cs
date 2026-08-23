namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.PromoteRule"/> can end (§7 block E, register:
/// "Promoting a change-scoped rule to repository scope SHALL move the same card, retaining its
/// identity, text and thread"). Same split-by-disposition reasoning as <see cref="CardCreateOutcome"/>/
/// <see cref="CardRegisterDischargeOutcome"/>.
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
        Func<InvalidStatus, TResult> onInvalidStatus,
        Func<NotARuleCard, TResult> onNotARuleCard,
        Func<TargetAlreadyExists, TResult> onTargetAlreadyExists,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="Card">The card exactly as written after promotion — same id, same body, same
    /// comments (<see cref="CardFile.Comments"/> is untouched by this method entirely), only
    /// <c>scope</c> and <c>updated</c> differ from what was read at the start of the call.</param>
    /// <param name="OldFilePath">Where the card lived before promotion.</param>
    /// <param name="NewFilePath">Where the card lives now — inside <see cref="CardLayout.
    /// RegisterDirectory"/>.</param>
    internal sealed record Promoted(CardFile Card, string OldFilePath, string NewFilePath) : CardRulePromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onPromoted(this);
    }

    /// <summary>The rule is already <see cref="CardScope.Repository"/>-scoped — "promoting an
    /// already-repository-scoped rule is a refusal too" (§7 block E brief item 3). Refusal-shaped.
    /// </summary>
    internal sealed record AlreadyRepositoryScoped(string FilePath) : CardRulePromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onAlreadyRepositoryScoped(this);
    }

    /// <summary>The rule carries a <see cref="CardScope"/> other than <see cref="CardScope.Change"/>
    /// or <see cref="CardScope.Repository"/> — unreachable through this codebase's own writers
    /// (<see cref="CardScopeRules.Validate"/> never lets a rule take <see cref="CardScope.Section"/>
    /// or <see cref="CardScope.Capability"/>), but a hand-edited file can still say it, and this
    /// method never treats a case <see cref="CardScopeRules"/> forbids as if it were the one
    /// scope promotion actually knows how to move from. Refusal-shaped.</summary>
    internal sealed record NotChangeScoped(CardScope Scope, string FilePath) : CardRulePromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotChangeScoped(this);
    }

    /// <summary>The rule's own <c>status</c> does not parse as <see cref="RegisterLifecycleState"/>
    /// — register: "SHALL NOT occupy flow states", the same exercised refusal every other register
    /// mutation in this codebase already enforces. Refusal-shaped.</summary>
    internal sealed record InvalidStatus(string FilePath, string Status) : CardRulePromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onInvalidStatus(this);
    }

    /// <summary>The resolved card is not a <c>rule</c> — "promotion applies to rules" (§7 block E
    /// brief item 3): every other register kind has exactly one legal scope, so promoting it would
    /// be meaningless, not merely unsupported. Refusal-shaped.</summary>
    internal sealed record NotARuleCard(CardKind Kind) : CardRulePromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotARuleCard(this);
    }

    /// <summary>A file already occupies the exact path this rule would move to inside
    /// <see cref="CardLayout.RegisterDirectory"/> (its own basename, unchanged by promotion, already
    /// claimed by an unrelated card). Refusal-shaped — checked before <see cref="File.Move(string,
    /// string)"/> is attempted, so a collision never partially moves anything.</summary>
    internal sealed record TargetAlreadyExists(string FilePath) : CardRulePromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onTargetAlreadyExists(this);
    }

    /// <summary>No card file exists at the resolved path (a race between resolution and locking).
    /// Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardRulePromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardNotFound(this);
    }

    /// <summary>The computed target path does not resolve under <see cref="CardLayout.
    /// RegisterDirectory"/> for the given root (<see cref="AnchoredCardPath.TryCreate"/>) —
    /// unreachable in practice since this method computes the target itself, kept for the same
    /// "the anchoring check can refuse, so the outcome type must be able to say so" reason every
    /// other <c>CardStore</c> write surface carries this case. Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardRulePromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but could not be parsed. Neither refusal nor tool-failure.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardRulePromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardCorrupt(this);
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
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<AlreadyRepositoryScoped, TResult> onAlreadyRepositoryScoped, Func<NotChangeScoped, TResult> onNotChangeScoped, Func<InvalidStatus, TResult> onInvalidStatus, Func<NotARuleCard, TResult> onNotARuleCard, Func<TargetAlreadyExists, TResult> onTargetAlreadyExists, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
