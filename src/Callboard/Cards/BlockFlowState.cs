namespace Callboard.Cards;

/// <summary>
/// The seven states a <c>block</c> card's flow occupies (work-lifecycle: "Block cards move through
/// a defined flow"): <c>drafting</c>, <c>briefed</c>, <c>building</c>, <c>in-review</c>,
/// <c>approved</c>, <c>landed</c>, <c>closed</c>. Modelled the same way as
/// <see cref="Callboard.Cards.CardKind"/> and <see cref="Callboard.Cli.CommandOutcome"/> — a
/// private constructor and seven sealed nested cases close the hierarchy to this file, and
/// <see cref="Match{TResult}"/> is the only way to consume a value. See <see cref="CardKind"/>'s
/// doc comment for why this is a closed union and not a C# <c>enum</c>: an enum switch is never
/// exhaustively checked by the compiler, so an eighth case (or a renamed one) would silently pass
/// through a default/discard arm instead of failing to build.
///
/// <para>
/// <c>changes-requested</c> is <b>not</b> an eighth state — work-lifecycle names it a transition
/// that lands back in <c>briefed</c> with <c>round</c> incremented. It has no case here; it is a
/// <see cref="BlockFlowTransition"/> in <see cref="BlockFlowTransitions"/> instead.
/// </para>
/// </summary>
internal abstract record BlockFlowState
{
    private BlockFlowState()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<TResult> onDrafting,
        Func<TResult> onBriefed,
        Func<TResult> onBuilding,
        Func<TResult> onInReview,
        Func<TResult> onApproved,
        Func<TResult> onLanded,
        Func<TResult> onClosed);

    internal static readonly BlockFlowState Drafting = new DraftingCase();
    internal static readonly BlockFlowState Briefed = new BriefedCase();
    internal static readonly BlockFlowState Building = new BuildingCase();
    internal static readonly BlockFlowState InReview = new InReviewCase();
    internal static readonly BlockFlowState Approved = new ApprovedCase();
    internal static readonly BlockFlowState Landed = new LandedCase();
    internal static readonly BlockFlowState Closed = new ClosedCase();

    private sealed record DraftingCase : BlockFlowState
    {
        internal override TResult Match<TResult>(Func<TResult> onDrafting, Func<TResult> onBriefed, Func<TResult> onBuilding, Func<TResult> onInReview, Func<TResult> onApproved, Func<TResult> onLanded, Func<TResult> onClosed) => onDrafting();
    }

    private sealed record BriefedCase : BlockFlowState
    {
        internal override TResult Match<TResult>(Func<TResult> onDrafting, Func<TResult> onBriefed, Func<TResult> onBuilding, Func<TResult> onInReview, Func<TResult> onApproved, Func<TResult> onLanded, Func<TResult> onClosed) => onBriefed();
    }

    private sealed record BuildingCase : BlockFlowState
    {
        internal override TResult Match<TResult>(Func<TResult> onDrafting, Func<TResult> onBriefed, Func<TResult> onBuilding, Func<TResult> onInReview, Func<TResult> onApproved, Func<TResult> onLanded, Func<TResult> onClosed) => onBuilding();
    }

    private sealed record InReviewCase : BlockFlowState
    {
        internal override TResult Match<TResult>(Func<TResult> onDrafting, Func<TResult> onBriefed, Func<TResult> onBuilding, Func<TResult> onInReview, Func<TResult> onApproved, Func<TResult> onLanded, Func<TResult> onClosed) => onInReview();
    }

    private sealed record ApprovedCase : BlockFlowState
    {
        internal override TResult Match<TResult>(Func<TResult> onDrafting, Func<TResult> onBriefed, Func<TResult> onBuilding, Func<TResult> onInReview, Func<TResult> onApproved, Func<TResult> onLanded, Func<TResult> onClosed) => onApproved();
    }

    private sealed record LandedCase : BlockFlowState
    {
        internal override TResult Match<TResult>(Func<TResult> onDrafting, Func<TResult> onBriefed, Func<TResult> onBuilding, Func<TResult> onInReview, Func<TResult> onApproved, Func<TResult> onLanded, Func<TResult> onClosed) => onLanded();
    }

    private sealed record ClosedCase : BlockFlowState
    {
        internal override TResult Match<TResult>(Func<TResult> onDrafting, Func<TResult> onBriefed, Func<TResult> onBuilding, Func<TResult> onInReview, Func<TResult> onApproved, Func<TResult> onLanded, Func<TResult> onClosed) => onClosed();
    }
}

/// <summary>
/// The wire form of <see cref="BlockFlowState"/> — the text a block card's <c>status</c> field
/// carries — and the parse path back. Ordinal comparison throughout, same reason as
/// <see cref="CardKindWireFormat"/>.
/// </summary>
internal static class BlockFlowStateWireFormat
{
    private static readonly IReadOnlyDictionary<string, BlockFlowState> ByWireValue =
        new Dictionary<string, BlockFlowState>(StringComparer.Ordinal)
        {
            ["drafting"] = BlockFlowState.Drafting,
            ["briefed"] = BlockFlowState.Briefed,
            ["building"] = BlockFlowState.Building,
            ["in-review"] = BlockFlowState.InReview,
            ["approved"] = BlockFlowState.Approved,
            ["landed"] = BlockFlowState.Landed,
            ["closed"] = BlockFlowState.Closed,
        };

    internal static string ToWireString(this BlockFlowState state) => state.Match(
        onDrafting: static () => "drafting",
        onBriefed: static () => "briefed",
        onBuilding: static () => "building",
        onInReview: static () => "in-review",
        onApproved: static () => "approved",
        onLanded: static () => "landed",
        onClosed: static () => "closed");

    /// <summary>The recognised wire values, in the order work-lifecycle's spec text lists them.</summary>
    internal static string RecognisedValues => string.Join(", ", ByWireValue.Keys);

    internal static bool TryParse(string value, out BlockFlowState state)
    {
        var found = ByWireValue.TryGetValue(value, out var match);
        // Every value stored in ByWireValue is a non-null BlockFlowState singleton, so `match` is
        // non-null whenever `found` is true; the fallback to Drafting on failure is discarded by
        // every caller, which always checks the returned bool first.
        state = found ? match! : BlockFlowState.Drafting;
        return found;
    }
}
