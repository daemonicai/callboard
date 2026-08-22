namespace Callboard.Cards;

/// <summary>
/// The two states a <c>section</c> card's own <c>status</c> field occupies (work-lifecycle:
/// "Sections are entities" — "A section SHALL be a first-class entity carrying its own status"):
/// <c>open</c> or <c>closed</c>. Modelled the same way as <see cref="BlockFlowState"/> — a private
/// constructor and two sealed nested cases close the hierarchy to this file, and
/// <see cref="Match{TResult}"/> is the only way to consume a value. See <see cref="CardKind"/>'s
/// doc comment for why this is a closed union and not a C# <c>enum</c>.
///
/// <para>
/// <b>This is the section's own status field, read the same way <see cref="BlockFlowState"/> reads
/// a block card's <c>status</c> — not derived from the cards it raised.</b> Deriving "is this
/// section closed" from an aggregate over its raised cards (e.g. "every block card referencing it
/// is closed") is exactly the alternative design work-lifecycle's "the system answers from the
/// section entity without requiring its cards to be read" scenario rules out — see
/// <see cref="SectionFields"/>'s own doc comment and the CLI's <c>section status</c> handler, which
/// this type's <see cref="SectionFlowStateWireFormat.TryParse"/> is the only way either ever reads
/// a section's status.
/// </para>
/// </summary>
internal abstract record SectionFlowState
{
    private SectionFlowState()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<TResult> onOpen,
        Func<TResult> onClosed);

    internal static readonly SectionFlowState Open = new OpenCase();
    internal static readonly SectionFlowState Closed = new ClosedCase();

    private sealed record OpenCase : SectionFlowState
    {
        internal override TResult Match<TResult>(Func<TResult> onOpen, Func<TResult> onClosed) => onOpen();
    }

    private sealed record ClosedCase : SectionFlowState
    {
        internal override TResult Match<TResult>(Func<TResult> onOpen, Func<TResult> onClosed) => onClosed();
    }
}

/// <summary>
/// The wire form of <see cref="SectionFlowState"/> — the text a section card's <c>status</c> field
/// carries — and the parse path back. Ordinal comparison throughout, same reason as
/// <see cref="CardKindWireFormat"/>.
/// </summary>
internal static class SectionFlowStateWireFormat
{
    private static readonly IReadOnlyDictionary<string, SectionFlowState> ByWireValue =
        new Dictionary<string, SectionFlowState>(StringComparer.Ordinal)
        {
            ["open"] = SectionFlowState.Open,
            ["closed"] = SectionFlowState.Closed,
        };

    internal static string ToWireString(this SectionFlowState state) => state.Match(
        onOpen: static () => "open",
        onClosed: static () => "closed");

    /// <summary>The recognised wire values, in the order work-lifecycle's spec text lists them.</summary>
    internal static string RecognisedValues => string.Join(", ", ByWireValue.Keys);

    internal static bool TryParse(string value, out SectionFlowState state)
    {
        var found = ByWireValue.TryGetValue(value, out var match);
        // Every value stored in ByWireValue is a non-null SectionFlowState singleton, so `match` is
        // non-null whenever `found` is true; the fallback to Open on failure is discarded by every
        // caller, which always checks the returned bool first.
        state = found ? match! : SectionFlowState.Open;
        return found;
    }
}
