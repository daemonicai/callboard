namespace Callboard.Cards;

/// <summary>
/// Closed union over how adding to or removing from a block card's <c>blocked_by</c> set (§5
/// block D, <see cref="CardStore.AddBlockedBy"/> / <see cref="CardStore.RemoveBlockedBy"/>) can
/// end. Shared by both operations — each mints only the op-specific case it can actually produce
/// (<see cref="AlreadyBlockedBy"/> for add, <see cref="NotBlockedBy"/> for remove) — for the same
/// reason <see cref="CardBlockTransitionOutcome"/> and <see cref="CardGateResultOutcome"/> keep a
/// caller-correctable refusal structurally apart from a reported content problem
/// (<see cref="CardCorrupt"/>) and from enforcement being unavailable
/// (<see cref="ToolFailure"/>).
///
/// <para>
/// <b>Deriving blocked-ness never restores state (work-lifecycle: "Blocked is derived, not
/// stored").</b> Neither <see cref="CardStore.AddBlockedBy"/> nor <see cref="CardStore.
/// RemoveBlockedBy"/> ever touches <see cref="CardFrontmatter.Status"/> — only
/// <see cref="BlockCardFields.BlockedBy"/> — so a card's <see cref="BlockFlowState"/> is
/// mechanically unable to change as a side effect of blocking or unblocking it: there is no
/// production code path from either method to a status write, not merely a discipline neither
/// method happens to follow.
/// </para>
/// </summary>
internal abstract record CardBlockedByOutcome
{
    private CardBlockedByOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Updated, TResult> onUpdated,
        Func<AlreadyBlockedBy, TResult> onAlreadyBlockedBy,
        Func<NotBlockedBy, TResult> onNotBlockedBy,
        Func<NotABlockCard, TResult> onNotABlockCard,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory);

    /// <param name="Card">The card as written, carrying the updated <c>blocked_by</c> set.</param>
    /// <param name="ActingRole">The role that made this change (§5 remediation, DEVLOG §5 finding
    /// B1) — not persisted on the card (work-lifecycle's "Blocked is derived, not stored" gives
    /// <c>blocked_by</c> no per-item history to attribute against), but required here for the same
    /// reason <see cref="CardGateResultOutcome.Recorded.ActingRole"/> is: so a caller mapping this
    /// outcome to a CLI result can surface who made it.</param>
    internal sealed record Updated(CardFile Card, CardOwner ActingRole) : CardBlockedByOutcome
    {
        internal override TResult Match<TResult>(Func<Updated, TResult> onUpdated, Func<AlreadyBlockedBy, TResult> onAlreadyBlockedBy, Func<NotBlockedBy, TResult> onNotBlockedBy, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onUpdated(this);
    }

    /// <summary><see cref="CardStore.AddBlockedBy"/> only: the card's <c>blocked_by</c> already
    /// names <see cref="BlockingCardId"/> — adding it again would be silently ambiguous (does the
    /// caller mean it is now blocked twice?), so this refuses rather than growing a duplicate
    /// entry <see cref="BlockCardFields"/>'s own three-door validation would then have to
    /// tolerate. Refusal-shaped and card-addressed: fires after the card is read and the
    /// block-card/round checks above have already passed (§9 remediation S1).</summary>
    internal sealed record AlreadyBlockedBy(string BlockingCardId) : CardBlockedByOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Updated, TResult> onUpdated, Func<AlreadyBlockedBy, TResult> onAlreadyBlockedBy, Func<NotBlockedBy, TResult> onNotBlockedBy, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onAlreadyBlockedBy(this);

        public string RefusingRule => "work-lifecycle: blocked-by adds no duplicate entry";

        public string Remedy => $"'{BlockingCardId}' already names this card as a blocker; nothing to add.";
    }

    /// <summary><see cref="CardStore.RemoveBlockedBy"/> only: the card's <c>blocked_by</c> does
    /// not name <see cref="BlockingCardId"/> — nothing to clear. Refusal-shaped and card-addressed:
    /// fires after the card is read and the block-card/round checks above have already passed (§9
    /// remediation S1).</summary>
    internal sealed record NotBlockedBy(string BlockingCardId) : CardBlockedByOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Updated, TResult> onUpdated, Func<AlreadyBlockedBy, TResult> onAlreadyBlockedBy, Func<NotBlockedBy, TResult> onNotBlockedBy, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onNotBlockedBy(this);

        public string RefusingRule => "work-lifecycle: removing a blocker requires it to be present";

        public string Remedy => $"'{BlockingCardId}' does not name a current blocker on this card; nothing to remove.";
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>block</c>.
    /// Refusal-shaped and card-addressed: fires after the card is read (§9 remediation S1).</summary>
    internal sealed record NotABlockCard(CardKind Kind) : CardBlockedByOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Updated, TResult> onUpdated, Func<AlreadyBlockedBy, TResult> onAlreadyBlockedBy, Func<NotBlockedBy, TResult> onNotBlockedBy, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onNotABlockCard(this);

        public string RefusingRule => "work-lifecycle: blocked-by only applies to a block card";

        public string Remedy => "target a card whose kind is 'block'.";
    }

    /// <summary>No card exists at the target path. Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardBlockedByOutcome
    {
        internal override TResult Match<TResult>(Func<Updated, TResult> onUpdated, Func<AlreadyBlockedBy, TResult> onAlreadyBlockedBy, Func<NotBlockedBy, TResult> onNotBlockedBy, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onCardNotFound(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name.
    /// Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardBlockedByOutcome
    {
        internal override TResult Match<TResult>(Func<Updated, TResult> onUpdated, Func<AlreadyBlockedBy, TResult> onAlreadyBlockedBy, Func<NotBlockedBy, TResult> onNotBlockedBy, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but could not be parsed. Neither refusal nor tool-failure.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardBlockedByOutcome
    {
        internal override TResult Match<TResult>(Func<Updated, TResult> onUpdated, Func<AlreadyBlockedBy, TResult> onAlreadyBlockedBy, Func<NotBlockedBy, TResult> onNotBlockedBy, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onCardCorrupt(this);
    }

    /// <summary>work-lifecycle: "Stored round agrees with the transition history" (8a.17) — the
    /// block card's stored <c>round</c> does not equal one plus the number of round-incrementing
    /// transitions (<see cref="BlockFlowTransitions.RoundIncrementingTransitionNames"/>) in its own
    /// <see cref="CardFile.Transitions"/> history. Refusal-shaped: neither figure is privileged and
    /// neither is altered — a stored count ahead of the history and a history ahead of the count are
    /// different failures, and guessing which is right would silently destroy the evidence of
    /// whichever was correct. Card-addressed: fires after the card is read and the block-card check
    /// above has already passed (§9 remediation S1).</summary>
    internal sealed record RoundDisagreesWithHistory(int StoredRound, int ExpectedRound) : CardBlockedByOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Updated, TResult> onUpdated, Func<AlreadyBlockedBy, TResult> onAlreadyBlockedBy, Func<NotBlockedBy, TResult> onNotBlockedBy, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onRoundDisagreesWithHistory(this);

        public string RefusingRule => "work-lifecycle: stored round agrees with the transition history";

        public string Remedy =>
            $"the recorded round ({StoredRound}) disagrees with the transition history ({ExpectedRound}); " +
            "correct whichever was altered outside the tool before this transition can proceed.";
    }

    /// <summary>Enforcement itself is unavailable: the card's lock could not be acquired within
    /// its timeout, or an I/O error occurred while writing. Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardBlockedByOutcome
    {
        internal override TResult Match<TResult>(Func<Updated, TResult> onUpdated, Func<AlreadyBlockedBy, TResult> onAlreadyBlockedBy, Func<NotBlockedBy, TResult> onNotBlockedBy, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onToolFailure(this);
    }
}
