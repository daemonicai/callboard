namespace Callboard.Cards;

/// <summary>
/// Closed union over the shapes a write to (or targeted read-modify-write of) the primary record
/// can end in. Split into distinct cases, not one <c>Failure(string)</c>, because refusal,
/// tool-failure and reported-failure carry <b>opposite instructions to the caller</b> (§3's
/// standing rule, record-retrieval's degraded-mode requirement): a refusal means stop, the caller
/// is wrong and can correct it; a tool-failure means enforcement itself is unavailable and the
/// loop must proceed unenforced; a corrupt card is neither — a reported problem with the record's
/// content, not with what the caller asked for. §5 block C shipped a version of this idea
/// (<c>CardBlockTransitionOutcome</c>) split correctly at its own domain-specific layer but left
/// this shared type flat, so its one caller (<c>block transition</c>) mapped every disposition to
/// a refusal regardless of which it actually was. Fixed here, at the type, rather than at a CLI
/// mapping — a mapping fix is a convention a future caller (§8's verbs over
/// <see cref="CardStore.AppendComment"/>/<see cref="CardStore.TransferOwnership"/>) could as easily
/// get wrong again; the compiler forcing every case to be handled is what a convention cannot do.
/// </summary>
internal abstract record CardWriteResult
{
    private CardWriteResult()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Success, TResult> onSuccess,
        Func<NotFound, TResult> onNotFound,
        Func<AlreadyExists, TResult> onAlreadyExists,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<Corrupt, TResult> onCorrupt,
        Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory);

    internal sealed record Success : CardWriteResult
    {
        internal override TResult Match<TResult>(Func<Success, TResult> onSuccess, Func<NotFound, TResult> onNotFound, Func<AlreadyExists, TResult> onAlreadyExists, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<Corrupt, TResult> onCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onSuccess(this);
    }

    /// <summary>No card exists at the target path for an operation that requires one to already be
    /// there (append, transfer). Refusal-shaped: caller-correctable, same class as
    /// <c>repo-root-not-found</c>.</summary>
    internal sealed record NotFound(string FilePath) : CardWriteResult
    {
        internal override TResult Match<TResult>(Func<Success, TResult> onSuccess, Func<NotFound, TResult> onNotFound, Func<AlreadyExists, TResult> onAlreadyExists, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<Corrupt, TResult> onCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onNotFound(this);
    }

    /// <summary>A card already exists at the target path for a create-only write. Refusal-shaped:
    /// caller-correctable — use <see cref="CardStore.AppendComment"/> or
    /// <see cref="CardStore.TransferOwnership"/> to update an existing card instead.</summary>
    internal sealed record AlreadyExists(string FilePath) : CardWriteResult
    {
        internal override TResult Match<TResult>(Func<Success, TResult> onSuccess, Func<NotFound, TResult> onNotFound, Func<AlreadyExists, TResult> onAlreadyExists, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<Corrupt, TResult> onCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onAlreadyExists(this);
    }

    /// <summary>The target path does not resolve under the given repository root and scope
    /// (<see cref="AnchoredCardPath.TryCreate"/>), or a required change name was missing or
    /// invalid for a change-/section-scoped card. Refusal-shaped: caller-correctable.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardWriteResult
    {
        internal override TResult Match<TResult>(Func<Success, TResult> onSuccess, Func<NotFound, TResult> onNotFound, Func<AlreadyExists, TResult> onAlreadyExists, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<Corrupt, TResult> onCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but could not be parsed. Neither refusal nor tool-failure — a
    /// reported problem with the record's own content (record-retrieval's degraded-mode
    /// requirement: a corrupt card must not be mistaken for the caller being wrong, or for
    /// enforcement being unavailable).</summary>
    internal sealed record Corrupt(string FilePath, string Reason) : CardWriteResult
    {
        internal override TResult Match<TResult>(Func<Success, TResult> onSuccess, Func<NotFound, TResult> onNotFound, Func<AlreadyExists, TResult> onAlreadyExists, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<Corrupt, TResult> onCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onCorrupt(this);
    }

    /// <summary>work-lifecycle: "Stored round agrees with the transition history" (8a.17) — the
    /// block card's stored <c>round</c> does not equal one plus the number of round-incrementing
    /// transitions (<see cref="BlockFlowTransitions.RoundIncrementingTransitionNames"/>) in its own
    /// <see cref="CardFile.Transitions"/> history. Refusal-shaped: neither figure is privileged and
    /// neither is altered — a stored count ahead of the history and a history ahead of the count are
    /// different failures, and guessing which is right would silently destroy the evidence of
    /// whichever was correct.</summary>
    internal sealed record RoundDisagreesWithHistory(int StoredRound, int ExpectedRound) : CardWriteResult
    {
        internal override TResult Match<TResult>(Func<Success, TResult> onSuccess, Func<NotFound, TResult> onNotFound, Func<AlreadyExists, TResult> onAlreadyExists, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<Corrupt, TResult> onCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onRoundDisagreesWithHistory(this);
    }

    /// <summary>Enforcement itself is unavailable: the card's lock could not be acquired within
    /// its timeout, or an I/O error occurred while writing. Tool-failure-shaped: the board is not
    /// refusing anything, the record is merely temporarily unwritable — a caller wired over this
    /// must let it propagate to a tool-failure exit (ADR-0001), never fold it into a refusal.
    /// </summary>
    internal sealed record ToolFailure(string Reason) : CardWriteResult
    {
        internal override TResult Match<TResult>(Func<Success, TResult> onSuccess, Func<NotFound, TResult> onNotFound, Func<AlreadyExists, TResult> onAlreadyExists, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<Corrupt, TResult> onCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onToolFailure(this);
    }
}
