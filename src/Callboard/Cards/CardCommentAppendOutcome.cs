namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.AddComment"/> can end (§13, card-model: "Append-only
/// addressed comment threads" — "The verbs that dispose of a thread SHALL NOT be the only ones
/// that can start one"). <c>comment add</c>'s own outcome, the same shape every other
/// <c>comment</c> sub-verb already carries (<see cref="CardCommentResolveOutcome"/>, <see
/// cref="CardCommentPromoteOutcome"/>) rather than exposing the lower-level, generic <see
/// cref="CardWriteResult"/> at the CLI boundary — that type is shared by roughly twenty other
/// writers across this codebase (<see cref="CardStore.WriteCard"/>, every <c>*UnderExistingLock</c>
/// method that maps through <see cref="CardStore.AtomicWrite"/>), so a case specific to this one
/// verb (<see cref="ReplyToNotFound"/>) cannot be added there without forcing every one of those
/// unrelated call sites to grow an arm for a check that can never fire through them.
///
/// <para>
/// <b><see cref="ReplyToNotFound"/> is the reason this union exists rather than a bare call to <see
/// cref="CardStore.AppendComment"/> (Architect ruling, §13 block brief item 4).</b> <see
/// cref="CardStore.AddCommentUnderExistingLock"/> is its own read-decide-write — the same shape
/// every other <c>comment</c> sub-verb's own <c>CardStore</c> method already uses (<see
/// cref="CardStore.ResolveCommentUnderExistingLock"/>, <see cref="CardStore.
/// PromoteCommentUnderLocks"/>), not a wrapper around <see cref="CardStore.AppendComment"/> — a
/// verb-specific refusal cannot be added to <see cref="CardStore.AppendComment"/>'s own return type
/// without the blast radius this type's own header paragraph names, and re-reading the card a
/// second time just to delegate to it would only replay checks this method already reused
/// directly. <see cref="RoundDisagreesWithHistory"/> and <see cref="HandEnteredDerivedState"/> are
/// <see cref="CardStore.AppendCommentUnderExistingLock"/>'s own two guards (<see cref="CardStore.
/// ReservedDerivedStateFieldKeyIn"/>, <see cref="CardStore.RoundAgreesWithHistory"/>), reused by
/// direct call rather than reimplemented, so the two verbs cannot silently drift on what either
/// means.
/// </para>
/// </summary>
internal abstract record CardCommentAppendOutcome
{
    private CardCommentAppendOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Added, TResult> onAdded,
        Func<ReplyToNotFound, TResult> onReplyToNotFound,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory,
        Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState,
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="Card">The card exactly as read, before the comment landed — frontmatter and
    /// prior thread only; <see cref="Comment"/> carries the new entry itself, so a caller does not
    /// need to search the card's own <see cref="CardFile.Comments"/> for what it just sent.</param>
    /// <param name="Comment">The comment as appended, carrying its own minted <see cref="Cards.
    /// CardComment.Id"/> — the only handle a caller has to resolve this thread later (§11 ruling
    /// 2: a comment id is document-local, so withholding it withholds the only handle).</param>
    internal sealed record Added(CardFile Card, CardComment Comment) : CardCommentAppendOutcome
    {
        internal override TResult Match<TResult>(Func<Added, TResult> onAdded, Func<ReplyToNotFound, TResult> onReplyToNotFound, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState, Func<ToolFailure, TResult> onToolFailure) =>
            onAdded(this);
    }

    /// <summary><c>--reply-to</c> named a comment id that is not in this card's own thread
    /// (Architect ruling, §13 block brief item 4: "a refusal, not a silently-dropped field").
    /// Card-addressed — the card itself was resolved and read, only the named comment does not
    /// exist on it. Refusal-shaped.</summary>
    internal sealed record ReplyToNotFound(string ReplyToId) : CardCommentAppendOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Added, TResult> onAdded, Func<ReplyToNotFound, TResult> onReplyToNotFound, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState, Func<ToolFailure, TResult> onToolFailure) =>
            onReplyToNotFound(this);

        public string RefusingRule => "card-model: '--reply-to' must name a comment already in this card's own thread";

        public string Remedy => $"comment '{ReplyToId}' does not exist on this card; read the thread with 'card show --id' to find the real comment id.";
    }

    /// <summary>No card file exists at the resolved path (a race between resolution and locking).
    /// Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardCommentAppendOutcome
    {
        internal override TResult Match<TResult>(Func<Added, TResult> onAdded, Func<ReplyToNotFound, TResult> onReplyToNotFound, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState, Func<ToolFailure, TResult> onToolFailure) =>
            onCardNotFound(this);
    }

    /// <summary>The card's directory does not anchor under its own recorded scope — unreachable in
    /// practice, kept for the same reason every other <c>CardStore</c> write surface carries this
    /// case. Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardCommentAppendOutcome
    {
        internal override TResult Match<TResult>(Func<Added, TResult> onAdded, Func<ReplyToNotFound, TResult> onReplyToNotFound, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but could not be parsed. Neither refusal nor tool-failure.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardCommentAppendOutcome
    {
        internal override TResult Match<TResult>(Func<Added, TResult> onAdded, Func<ReplyToNotFound, TResult> onReplyToNotFound, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState, Func<ToolFailure, TResult> onToolFailure) =>
            onCardCorrupt(this);
    }

    /// <summary>work-lifecycle: "act on that card" covers every writer that mutates a block card
    /// (§8a block D) — <see cref="CardStore.AppendCommentUnderExistingLock"/>'s own guard, reported
    /// here as this union's own case rather than the generic <see cref="CardWriteResult"/>'s.
    /// Already recorded by the time this case is constructed; see this type's own doc
    /// comment.</summary>
    internal sealed record RoundDisagreesWithHistory(int StoredRound, int ExpectedRound) : CardCommentAppendOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Added, TResult> onAdded, Func<ReplyToNotFound, TResult> onReplyToNotFound, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState, Func<ToolFailure, TResult> onToolFailure) =>
            onRoundDisagreesWithHistory(this);

        public string RefusingRule => "work-lifecycle: stored round agrees with the transition history";

        public string Remedy =>
            $"the recorded round ({StoredRound}) disagrees with the transition history ({ExpectedRound}); " +
            "correct whichever was altered outside the tool before this write can proceed.";
    }

    /// <summary>working-context: "No figure SHALL be hand-entered anywhere in the system" (§10
    /// block C) — <see cref="CardStore.AppendCommentUnderExistingLock"/>'s own guard, reported here
    /// as this union's own case. Already recorded by the time this case is constructed; see this
    /// type's own doc comment.</summary>
    internal sealed record HandEnteredDerivedState(string Key) : CardCommentAppendOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Added, TResult> onAdded, Func<ReplyToNotFound, TResult> onReplyToNotFound, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState, Func<ToolFailure, TResult> onToolFailure) =>
            onHandEnteredDerivedState(this);

        public string RefusingRule => "working-context: no figure shall be hand-entered";

        public string Remedy =>
            $"'{Key}' is a reserved derived-state field name; remove it from this card's frontmatter — " +
            "this state is derived at request time, never stored, and is available from 'callboard state'.";
    }

    /// <summary>Enforcement itself is unavailable — the lock could not be acquired, or the write
    /// failed after every check passed. Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardCommentAppendOutcome
    {
        internal override TResult Match<TResult>(Func<Added, TResult> onAdded, Func<ReplyToNotFound, TResult> onReplyToNotFound, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
