namespace Callboard.Cards;

/// <summary>
/// Closed union over how applying a block flow transition (5.2, 5.3, 5.5) can end. Distinct from
/// <see cref="CardWriteResult"/> — that type only distinguishes success from an opaque string
/// failure, which is enough for <see cref="CardStore.AppendComment"/> and
/// <see cref="CardStore.TransferOwnership"/> because nothing downstream of them needs to tell
/// their failure reasons apart. A transition's caller does: the CLI boundary (5.2) has to mint a
/// distinct refusal code for an undefined transition versus a missing <c>base</c> versus an
/// attempt to change an already-recorded one, and modelling those as one more string would put the
/// classification back in prose a caller has to parse instead of a case a compiler can force
/// exhaustive handling of (ADR-0001: a refusal is a returned result).
///
/// <para>
/// <b>Refusal, tool-failure and reported-failure stay distinct cases (reviewer finding, first
/// remediation round):</b> the first shape shipped folded "no card at that path", "layout
/// mismatch", "corrupt card" and "lock timeout / I/O failure while writing" into one
/// <c>WriteFailed(string Reason)</c> case, which the CLI boundary then mapped, wholesale, to one
/// refusal code (<c>card-write-failed</c>). That inverted §3's own standing rule: refusal means
/// "stop, you are wrong"; tool-failure means "enforcement is unavailable, proceed unenforced"; a
/// corrupt card is neither. Telling a caller to stop when the truth is "the tool broke" is the
/// exact failure class this codebase exists to prevent. <see cref="CardNotFound"/> and
/// <see cref="LayoutMismatch"/> are refusal-shaped (caller-correctable); <see cref="CardCorrupt"/>
/// and <see cref="ToolFailure"/> are not — a caller wired over this type (see
/// <see cref="Callboard.Cli.CommandDispatcher.RunBlockTransition"/>) must route those two to a
/// tool-failure exit, never a refusal, or the same defect recurs one layer up.
/// </para>
/// </summary>
internal abstract record CardBlockTransitionOutcome
{
    private CardBlockTransitionOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Applied, TResult> onApplied,
        Func<UndefinedTransition, TResult> onUndefinedTransition,
        Func<BaseNotRecorded, TResult> onBaseNotRecorded,
        Func<BaseImmutable, TResult> onBaseImmutable,
        Func<UndispositionedNits, TResult> onUndispositionedNits,
        Func<NotABlockCard, TResult> onNotABlockCard,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory);

    /// <param name="Card">The card as written: new status, updated <c>base</c>/<c>round</c>, and
    /// the appended <see cref="CardBlockTransitionEntry"/>.</param>
    /// <param name="Transition">The edge that was applied.</param>
    internal sealed record Applied(CardFile Card, BlockFlowTransition Transition) : CardBlockTransitionOutcome
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onApplied(this);
    }

    /// <param name="CurrentState">The state the card was actually in when the transition was
    /// attempted.</param>
    /// <param name="Available">The transitions a bare <c>block transition</c> call may itself drive
    /// from <paramref name="CurrentState"/> — read from <see cref="BlockFlowTransitions.
    /// GenericallyInvocableFrom"/>, not <see cref="BlockFlowTransitions.AvailableFrom"/> (§8a
    /// remediation): the wider table can carry an edge (e.g. <c>finding-recurred</c>) that is legal
    /// from this state but reached only through its own dedicated door, and naming it here would
    /// advertise a door this same refusal would then itself refuse.</param>
    internal sealed record UndefinedTransition(BlockFlowState CurrentState, IReadOnlyList<BlockFlowTransition> Available) : CardBlockTransitionOutcome
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onUndefinedTransition(this);
    }

    /// <summary>work-lifecycle: "base SHALL be recorded before the block is briefed" — neither
    /// already recorded on the card nor supplied with this call.</summary>
    internal sealed record BaseNotRecorded : CardBlockTransitionOutcome
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onBaseNotRecorded(this);
    }

    /// <summary>work-lifecycle: "SHALL NOT change across remediation rounds" — a caller supplied a
    /// <c>base</c> that disagrees with the one already recorded on the card.</summary>
    internal sealed record BaseImmutable(string Recorded, string Attempted) : CardBlockTransitionOutcome
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onBaseImmutable(this);
    }

    /// <summary>review-certification: "Undispositioned nits block the verdict" (§8 block B) —
    /// <c>changes-requested</c> is one of the transitions "moved out of in-review" that requirement
    /// binds; <c>approve</c> is <see cref="CardApprovalOutcome.UndispositionedNits"/>'s own case,
    /// and <c>fix-before-land</c> is refused at parse before it ever reaches this table
    /// (<see cref="Callboard.Cli.CommandParser.ParseBlockTransition"/>).</summary>
    internal sealed record UndispositionedNits(IReadOnlyList<string> NitIds) : CardBlockTransitionOutcome
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onUndispositionedNits(this);
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>block</c> — flow
    /// transitions are only defined for a block card. Refusal-shaped: caller pointed the verb at
    /// the wrong card.</summary>
    internal sealed record NotABlockCard(CardKind Kind) : CardBlockTransitionOutcome
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onNotABlockCard(this);
    }

    /// <summary>No card exists at the target path. Refusal-shaped: caller-correctable, same class
    /// as <c>repo-root-not-found</c>.</summary>
    internal sealed record CardNotFound(string FilePath) : CardBlockTransitionOutcome
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onCardNotFound(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>). Refusal-shaped: caller-correctable.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardBlockTransitionOutcome
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but its content could not be parsed, or (specifically for a block
    /// card) carries a <c>status</c> this build does not recognise as a <see cref="BlockFlowState"/>.
    /// Neither refusal nor tool-failure — a reported problem with the record's own content
    /// (record-retrieval's degraded-mode requirement). A caller wired over this type must not
    /// route it to a refusal exit.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardBlockTransitionOutcome
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onCardCorrupt(this);
    }

    /// <summary>work-lifecycle: "Stored round agrees with the transition history" (8a.17) — the
    /// block card's stored <c>round</c> does not equal one plus the number of round-incrementing
    /// transitions (<see cref="BlockFlowTransitions.RoundIncrementingTransitionNames"/>) in its own
    /// <see cref="CardFile.Transitions"/> history. Refusal-shaped: neither figure is privileged and
    /// neither is altered — a stored count ahead of the history and a history ahead of the count are
    /// different failures, and guessing which is right would silently destroy the evidence of
    /// whichever was correct.</summary>
    internal sealed record RoundDisagreesWithHistory(int StoredRound, int ExpectedRound) : CardBlockTransitionOutcome
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure, Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onRoundDisagreesWithHistory(this);
    }

    /// <summary>Enforcement itself is unavailable: the card's lock could not be acquired within its
    /// timeout, or an I/O error occurred while writing. Tool-failure-shaped — the board is not
    /// refusing anything. A caller wired over this type must let it reach a tool-failure exit
    /// (ADR-0001), the same route <c>index rebuild</c>'s SQLite I/O failures already take by
    /// letting the exception escape to <see cref="Callboard.Cli.CommandDispatcher.Run"/>'s own
    /// catch, never fold it into a refusal.</summary>
    internal sealed record ToolFailure(string Reason) : CardBlockTransitionOutcome
    {
        internal override TResult Match<TResult>(Func<Applied, TResult> onApplied, Func<UndefinedTransition, TResult> onUndefinedTransition, Func<BaseNotRecorded, TResult> onBaseNotRecorded, Func<BaseImmutable, TResult> onBaseImmutable, Func<UndispositionedNits, TResult> onUndispositionedNits, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure,
        Func<RoundDisagreesWithHistory, TResult> onRoundDisagreesWithHistory) =>
            onToolFailure(this);
    }
}
