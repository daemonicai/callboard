namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.PromoteComment"/> can end (§9 remediation, round two —
/// S4: give <c>9.6</c>'s "promote to a 'question'" / "promote to a 'decision'" a verb a caller can
/// actually run). Two-card write — a new <c>question</c>/<c>decision</c> card, plus the resolving
/// comment on the existing card — reusing <see cref="CardStore.RecordFinding"/>'s own two-card,
/// two-lock discipline (§8a supervisor: do not add a fourth divergent multi-card write shape to
/// <c>CardStore</c>). <see cref="RaisedCardAlreadyExists"/>/<see cref="RaisedCardLayoutMismatch"/>
/// mirror <see cref="CardFindingRecordOutcome"/>'s own <c>BlindSpotCardAlreadyExists</c>/
/// <c>BlindSpotLayoutMismatch</c> exactly: neither ever resolves an existing card at the raised
/// path, so neither is card-addressed (§9 block A3 ruling).
///
/// <para>
/// <b><see cref="RoleNotPermitted"/> is refusal-shaped and records</b> — same precedent, and the
/// same Product Owner ruling, as <see cref="CardCommentResolveOutcome.RoleNotPermitted"/>'s own
/// doc comment records; the two verbs share one policy.
/// </para>
/// </summary>
internal abstract record CardCommentPromoteOutcome
{
    private CardCommentPromoteOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Promoted, TResult> onPromoted,
        Func<CommentNotFound, TResult> onCommentNotFound,
        Func<RoleNotPermitted, TResult> onRoleNotPermitted,
        Func<AlreadyResolved, TResult> onAlreadyResolved,
        Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists,
        Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState);

    /// <param name="OriginalCard">The original card exactly as written after the resolving comment
    /// landed.</param>
    /// <param name="RaisedCard">The new <c>question</c>/<c>decision</c> card, written first (this
    /// method's own "second write" is the original card's resolving comment — same ordering
    /// <see cref="CardStore.RecordFinding"/> uses, and for the same reason: it is what makes a
    /// genuine "first write succeeded, second failed" rollback case reachable at all).</param>
    /// <param name="RaisedCardFilePath">Where <paramref name="RaisedCard"/> landed — <see
    /// cref="CardLayout.FileNameFor"/>'s result under its fixed scope's own directory
    /// (14.5-remediation, §14 supervisor finding, second round: the caller no longer names
    /// this).</param>
    internal sealed record Promoted(CardFile OriginalCard, CardFile RaisedCard, string RaisedCardFilePath) : CardCommentPromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<CommentNotFound, TResult> onCommentNotFound, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onPromoted(this);
    }

    /// <summary>No comment on the resolved original card carries this id. Card-addressed. Refusal-shaped.</summary>
    internal sealed record CommentNotFound(string CommentId) : CardCommentPromoteOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<CommentNotFound, TResult> onCommentNotFound, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCommentNotFound(this);

        public string RefusingRule => "card-model: a comment can only be resolved by its own id";

        public string Remedy => $"comment '{CommentId}' does not exist on the resolved card; check the id and retry.";
    }

    /// <summary>The acting role is neither the thread's addressee nor the card's owner. Same policy
    /// as <see cref="CardCommentResolveOutcome.RoleNotPermitted"/> — see its doc comment for the
    /// Product Owner ruling and the deliberate consequences it names. Card-addressed. Refusal-shaped.</summary>
    internal sealed record RoleNotPermitted(CardOwner AttemptedRole, CardOwner CardOwnerRole, CardOwner? AddressedTo) : CardCommentPromoteOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<CommentNotFound, TResult> onCommentNotFound, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onRoleNotPermitted(this);

        public string RefusingRule => "process-enforcement: a thread is disposed of only by its addressee or the card's owner";

        public string Remedy => AddressedTo is null
            ? $"only the card's owner ('{CardOwnerRole.ToWireString()}') may dispose of this thread; '{AttemptedRole.ToWireString()}' attempted it."
            : $"only '{AddressedTo.ToWireString()}' (the thread's addressee) or '{CardOwnerRole.ToWireString()}' (the card's owner) may dispose of this thread; '{AttemptedRole.ToWireString()}' attempted it.";
    }

    /// <summary>The named comment already has a later comment resolving it. Refusal-shaped.</summary>
    internal sealed record AlreadyResolved(string CommentId) : CardCommentPromoteOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<CommentNotFound, TResult> onCommentNotFound, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onAlreadyResolved(this);

        public string RefusingRule => "card-model: a comment resolved once cannot be resolved again";

        public string Remedy => $"comment '{CommentId}' is already resolved; nothing further to do.";
    }

    /// <summary>A card already exists at the raised card's own target path. Never card-addressed —
    /// no existing card was resolved there to record against (§9 block A3 ruling, mirroring <see
    /// cref="CardFindingRecordOutcome.BlindSpotCardAlreadyExists"/> exactly).</summary>
    internal sealed record RaisedCardAlreadyExists(string FilePath) : CardCommentPromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<CommentNotFound, TResult> onCommentNotFound, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onRaisedCardAlreadyExists(this);
    }

    /// <summary>The raised card's own target path does not anchor under its scope. Never
    /// card-addressed — no card was resolved there either (mirrors <see cref="CardFindingRecordOutcome.
    /// BlindSpotLayoutMismatch"/>).</summary>
    internal sealed record RaisedCardLayoutMismatch(string Reason) : CardCommentPromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<CommentNotFound, TResult> onCommentNotFound, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onRaisedCardLayoutMismatch(this);
    }

    /// <summary>No card file exists at the original card's resolved path (a race between resolution
    /// and locking). Refusal-shaped.</summary>
    internal sealed record CardNotFound(string FilePath) : CardCommentPromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<CommentNotFound, TResult> onCommentNotFound, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardNotFound(this);
    }

    /// <summary>The original card's directory does not anchor under its own recorded scope —
    /// unreachable in practice, kept for the same reason every other <c>CardStore</c> write surface
    /// carries this case. Refusal-shaped.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardCommentPromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<CommentNotFound, TResult> onCommentNotFound, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onLayoutMismatch(this);
    }

    /// <summary>The original card exists but could not be parsed. Neither refusal nor tool-failure.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardCommentPromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<CommentNotFound, TResult> onCommentNotFound, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onCardCorrupt(this);
    }

    /// <summary>working-context: "No figure SHALL be hand-entered anywhere in the system" (§10
    /// block C) — <paramref name="Key"/> names a reserved derived-state field (<see
    /// cref="DerivedStateFieldKeys.All"/>) present on the target card's <see cref="CardFile.
    /// UnknownFrontmatterFields"/>, the door a hand-edited card's frontmatter uses to reach this far
    /// at all (nothing this build's own CLI ever writes one). Refusal-shaped, card-addressed (§9
    /// block A3): checked immediately once the card is read, before promoting the comment is allowed to
    /// proceed, so this write never re-emits (and never launders forward) a hand-entered count or
    /// next-step pin it did not itself write. See <see cref="CardWriteResult.HandEnteredDerivedState"/>
    /// for the sibling case on the generic comment/handover surface.</summary>
    internal sealed record HandEnteredDerivedState(string Key) : CardCommentPromoteOutcome, ICardRefusalReason
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<CommentNotFound, TResult> onCommentNotFound, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onHandEnteredDerivedState(this);

        public string RefusingRule => "working-context: no figure shall be hand-entered";

        public string Remedy =>
            $"'{Key}' is a reserved derived-state field name; remove it from this card's frontmatter — " +
            "this state is derived at request time, never stored, and is available from 'callboard state'.";
    }

    /// <summary>Enforcement itself is unavailable — a lock could not be acquired, or a write failed
    /// after every check passed. Tool-failure-shaped.</summary>
    internal sealed record ToolFailure(string Reason) : CardCommentPromoteOutcome
    {
        internal override TResult Match<TResult>(Func<Promoted, TResult> onPromoted, Func<CommentNotFound, TResult> onCommentNotFound, Func<RoleNotPermitted, TResult> onRoleNotPermitted, Func<AlreadyResolved, TResult> onAlreadyResolved, Func<RaisedCardAlreadyExists, TResult> onRaisedCardAlreadyExists, Func<RaisedCardLayoutMismatch, TResult> onRaisedCardLayoutMismatch, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<HandEnteredDerivedState, TResult> onHandEnteredDerivedState) =>
            onToolFailure(this);
    }
}
