namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.ResolveComment"/> can end (§9 remediation, round two —
/// S4: give <c>9.6</c>'s "resolve … or decline with a recorded reason" and <c>9.3</c>'s "resolve the
/// following thread(s)" a verb a caller can actually run). One method, one outcome, backing both
/// <c>comment resolve</c> and <c>comment decline --reason</c> — the two verbs differ only in whether
/// a reason is mandatory, not in the underlying write (an appended comment naming the one it <see
/// cref="CardComment.Resolves"/>, never a mutation — <see cref="CardComment"/>'s own class doc
/// comment). <see cref="ReasonRequired"/> is reachable only when the caller asked for the
/// reason-mandatory disposition; <c>comment resolve</c> never triggers it, the same way <see
/// cref="CardObligationDeclineOutcome.ReasonRequired"/> exists for one verb sharing a union with
/// siblings that never reach it.
/// </summary>
internal abstract record CardCommentResolveOutcome
{
    private CardCommentResolveOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Resolved, TResult> onResolved,
        Func<CommentNotFound, TResult> onCommentNotFound,
        Func<AlreadyResolved, TResult> onAlreadyResolved,
        Func<ReasonRequired, TResult> onReasonRequired,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="Card">The card exactly as written after the resolving comment landed.</param>
    /// <param name="ResolvingComment">The appended comment itself — its own <see cref="CardComment.
    /// Resolves"/> names the comment it closes out.</param>
    internal sealed record Resolved(CardFile Card, CardComment ResolvingComment) : CardCommentResolveOutcome
    {
        internal override TResult Match<TResult>(Func<Resolved, TResult> onResolved, Func<CommentNotFound, TResult> onCommentNotFound, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<ReasonRequired, TResult> onReasonRequired, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onResolved(this);
    }

    /// <summary>No comment on the resolved card carries this id. Card-addressed — the card itself
    /// resolved, only the comment within it did not. Refusal-shaped.</summary>
    internal sealed record CommentNotFound(string CommentId) : CardCommentResolveOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Resolved, TResult> onResolved, Func<CommentNotFound, TResult> onCommentNotFound, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<ReasonRequired, TResult> onReasonRequired, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCommentNotFound(this);

        public string RefusingRule => "card-model: a comment can only be resolved by its own id";

        public string Remedy => $"comment '{CommentId}' does not exist on the resolved card; check the id and retry.";
    }

    /// <summary>The named comment already has a later comment resolving it — card-model's own
    /// append-only discipline (<see cref="CardCommentRouting.IsResolved"/>): resolving it again would
    /// either silently no-op or leave two contradictory resolutions in the thread. Refusal-shaped.</summary>
    internal sealed record AlreadyResolved(string CommentId) : CardCommentResolveOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Resolved, TResult> onResolved, Func<CommentNotFound, TResult> onCommentNotFound, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<ReasonRequired, TResult> onReasonRequired, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onAlreadyResolved(this);

        public string RefusingRule => "card-model: a comment resolved once cannot be resolved again";

        public string Remedy => $"comment '{CommentId}' is already resolved; nothing further to do.";
    }

    /// <summary>No reason was supplied on the reason-mandatory disposition (<c>comment decline</c>).
    /// <see cref="Cli.CommandParser"/>'s own <c>comment decline</c> door already requires
    /// <c>--reason</c> unconditionally (the same "required at the door a real caller uses" discipline
    /// block A2 drew for <c>rule promote</c>'s <c>--change</c>, and block F drew for <c>obligation
    /// decline</c>'s own <c>--reason</c>); this case exists so <see cref="CardStore.ResolveComment"/>
    /// defends the same requirement on its own terms, for a caller reaching it directly. Never
    /// reachable from <c>comment resolve</c>, which never requires a reason. Refusal-shaped.</summary>
    internal sealed record ReasonRequired(string FilePath) : CardCommentResolveOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Resolved, TResult> onResolved, Func<CommentNotFound, TResult> onCommentNotFound, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<ReasonRequired, TResult> onReasonRequired, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onReasonRequired(this);

        public string RefusingRule => "process-enforcement: declining a thread requires a recorded reason";

        public string Remedy => $"'{FilePath}' was not declined; retry with a non-empty reason.";
    }

    /// <summary>No card file exists at the resolved path (a race between resolution and locking).
    /// Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardCommentResolveOutcome
    {
        internal override TResult Match<TResult>(Func<Resolved, TResult> onResolved, Func<CommentNotFound, TResult> onCommentNotFound, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<ReasonRequired, TResult> onReasonRequired, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardNotFound(this);
    }

    /// <summary>The card's directory does not anchor under its own recorded scope — unreachable in
    /// practice, kept for the same reason every other <c>CardStore</c> write surface carries this
    /// case. Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardCommentResolveOutcome
    {
        internal override TResult Match<TResult>(Func<Resolved, TResult> onResolved, Func<CommentNotFound, TResult> onCommentNotFound, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<ReasonRequired, TResult> onReasonRequired, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but could not be parsed. Neither refusal nor tool-failure.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardCommentResolveOutcome
    {
        internal override TResult Match<TResult>(Func<Resolved, TResult> onResolved, Func<CommentNotFound, TResult> onCommentNotFound, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<ReasonRequired, TResult> onReasonRequired, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardCorrupt(this);
    }

    /// <summary>Enforcement itself is unavailable — the lock could not be acquired, or the write
    /// failed after every check passed. Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardCommentResolveOutcome
    {
        internal override TResult Match<TResult>(Func<Resolved, TResult> onResolved, Func<CommentNotFound, TResult> onCommentNotFound, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<ReasonRequired, TResult> onReasonRequired, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
