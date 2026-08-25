namespace Callboard.Cards;

/// <summary>
/// Closed union over how <see cref="CardStore.RaiseNit"/> can end (review-certification: "A nit
/// SHALL be raised only against a block that is under review" — §8 remediation, Product Owner
/// ruling of 2026-08-24). Its own type rather than a reuse of the general-purpose
/// <see cref="CardWriteResult"/> <see cref="CardStore.AppendComment"/> exposes: every other caller
/// of that shared surface (comments that are not nits, ownership handovers) has no notion of
/// "under review" at all, so folding <see cref="NotUnderReview"/> into the shared union would force
/// every unrelated caller's exhaustive switch to carry a case that can never apply to it — the same
/// reasoning that already split <see cref="CardApprovalOutcome"/> out of the shared surface for
/// its own verb-specific facts. <see cref="NotABlockCard"/>, <see cref="CardNotFound"/>,
/// <see cref="NotUnderReview"/> and <see cref="LayoutMismatch"/> are refusal-shaped
/// (caller-correctable); <see cref="CardCorrupt"/> and <see cref="ToolFailure"/> are not — a
/// caller wired over this type (see
/// <see cref="Callboard.Cli.CommandDispatcher.RunNitRaise"/>) must route those two to a
/// tool-failure exit, never a refusal.
/// </summary>
internal abstract record CardNitRaiseOutcome
{
    private CardNitRaiseOutcome()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<Raised, TResult> onRaised,
        Func<NotABlockCard, TResult> onNotABlockCard,
        Func<CardNotFound, TResult> onCardNotFound,
        Func<NotUnderReview, TResult> onNotUnderReview,
        Func<LayoutMismatch, TResult> onLayoutMismatch,
        Func<CardCorrupt, TResult> onCardCorrupt,
        Func<ToolFailure, TResult> onToolFailure);

    /// <param name="Card">The card as written: the nit comment appended, nothing else changed —
    /// raising a nit never itself moves the block's <c>status</c>.</param>
    internal sealed record Raised(CardFile Card) : CardNitRaiseOutcome
    {
        internal override TResult Match<TResult>(Func<Raised, TResult> onRaised, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotUnderReview, TResult> onNotUnderReview, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onRaised(this);
    }

    /// <summary>The target card exists and parses, but its <c>kind</c> is not <c>block</c>. Nits
    /// only ever apply to a block card — checked again here even though
    /// <see cref="Callboard.Cli.CommandDispatcher.RunNitRaise"/> already resolves the reference
    /// against <see cref="CardStore.IsBlockCard"/>, because that resolution happens before the
    /// lock is taken and so is not itself race-proof (the same reasoning <see cref="
    /// CardApprovalOutcome.NotABlockCard"/> already applies). Refusal-shaped.</summary>
    internal sealed record NotABlockCard(CardKind Kind) : CardNitRaiseOutcome
    {
        internal override TResult Match<TResult>(Func<Raised, TResult> onRaised, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotUnderReview, TResult> onNotUnderReview, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotABlockCard(this);
    }

    /// <summary>No card exists at the target path. Refusal-shaped: caller-correctable.</summary>
    internal sealed record CardNotFound(string FilePath) : CardNitRaiseOutcome
    {
        internal override TResult Match<TResult>(Func<Raised, TResult> onRaised, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotUnderReview, TResult> onNotUnderReview, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardNotFound(this);
    }

    /// <summary>The card's current <see cref="BlockFlowState"/> is not
    /// <see cref="BlockFlowState.InReview"/> (review-certification: "A nit SHALL be raised only
    /// against a block that is under review. Raising one against a block in any other state SHALL
    /// be refused, naming the state the block is in and the obligation route below."). The bound is
    /// what makes "a nit SHALL cease to be live only through one of these three dispositions"
    /// enforceable: with raising confined to <c>in-review</c>, and the transition guard already
    /// refusing to leave <c>in-review</c> while a nit is undispositioned, no <c>approved</c>,
    /// <c>landed</c> or <c>closed</c> card can ever come to hold a live nit. Refusal-shaped —
    /// the observation is not lost, it becomes an <c>obligation</c> where the architect or the
    /// Product Owner judges it needs fixing, a judgement this type deliberately does not
    /// automate.</summary>
    internal sealed record NotUnderReview(BlockFlowState CurrentState) : CardNitRaiseOutcome
    {
        internal override TResult Match<TResult>(Func<Raised, TResult> onRaised, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotUnderReview, TResult> onNotUnderReview, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onNotUnderReview(this);
    }

    /// <summary>The target path does not resolve under the given root/scope/change name
    /// (<see cref="AnchoredCardPath.TryCreate"/>). Refusal-shaped: caller-correctable.</summary>
    internal sealed record LayoutMismatch(string Reason) : CardNitRaiseOutcome
    {
        internal override TResult Match<TResult>(Func<Raised, TResult> onRaised, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotUnderReview, TResult> onNotUnderReview, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onLayoutMismatch(this);
    }

    /// <summary>The card exists but its content could not be parsed, or carries a <c>status</c>
    /// this build does not recognise as a <see cref="BlockFlowState"/>. Neither refusal nor
    /// tool-failure — a reported problem with the record's own content. A caller wired over this
    /// type must not route it to a refusal exit.</summary>
    internal sealed record CardCorrupt(string FilePath, string Reason) : CardNitRaiseOutcome
    {
        internal override TResult Match<TResult>(Func<Raised, TResult> onRaised, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotUnderReview, TResult> onNotUnderReview, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onCardCorrupt(this);
    }

    /// <summary>Enforcement itself is unavailable: the card's lock could not be acquired within its
    /// timeout, or an I/O error occurred while writing. Tool-failure-shaped — the board is not
    /// refusing anything. A caller wired over this type must let it reach a tool-failure exit
    /// (ADR-0001).</summary>
    internal sealed record ToolFailure(string Reason) : CardNitRaiseOutcome
    {
        internal override TResult Match<TResult>(Func<Raised, TResult> onRaised, Func<NotABlockCard, TResult> onNotABlockCard, Func<CardNotFound, TResult> onCardNotFound, Func<NotUnderReview, TResult> onNotUnderReview, Func<LayoutMismatch, TResult> onLayoutMismatch, Func<CardCorrupt, TResult> onCardCorrupt, Func<ToolFailure, TResult> onToolFailure) =>
            onToolFailure(this);
    }
}
