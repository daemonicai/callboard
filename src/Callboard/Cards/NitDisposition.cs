namespace Callboard.Cards;

/// <summary>
/// The three dispositions a nit can receive (review-certification: "Nits carry a disposition", §8
/// block B): <c>fix-before-land</c>, <c>defer</c> or <c>decline</c>. Modelled as a closed union for
/// the same reason as <see cref="CardKind"/>/<see cref="BlockFlowState"/> — see <see cref="CardKind"/>'s
/// own doc comment: an enum switch is never exhaustively checked, so a fourth disposition added
/// later would silently pass through a default arm instead of failing to build.
///
/// <para>
/// This is <b>not</b> a fourth <see cref="BlockFlowTransition"/> shared by all three — only
/// <see cref="FixBeforeLand"/> ever moves a block card at all (work-lifecycle: "<c>fix-before-land</c>
/// … <c>in-review → briefed</c>"); <see cref="Defer"/> and <see cref="Decline"/> promote the nit to a
/// second card and never touch the block's own flow state. The disposition value is recorded on the
/// appended disposition <see cref="CardComment"/> regardless of which of the three it is — see that
/// type's own doc comment for why a later comment, never a mutation of the nit.
/// </para>
/// </summary>
internal abstract record NitDisposition
{
    private NitDisposition()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<TResult> onFixBeforeLand,
        Func<TResult> onDefer,
        Func<TResult> onDecline);

    internal static readonly NitDisposition FixBeforeLand = new FixBeforeLandCase();
    internal static readonly NitDisposition Defer = new DeferCase();
    internal static readonly NitDisposition Decline = new DeclineCase();

    private sealed record FixBeforeLandCase : NitDisposition
    {
        internal override TResult Match<TResult>(Func<TResult> onFixBeforeLand, Func<TResult> onDefer, Func<TResult> onDecline) => onFixBeforeLand();
    }

    private sealed record DeferCase : NitDisposition
    {
        internal override TResult Match<TResult>(Func<TResult> onFixBeforeLand, Func<TResult> onDefer, Func<TResult> onDecline) => onDefer();
    }

    private sealed record DeclineCase : NitDisposition
    {
        internal override TResult Match<TResult>(Func<TResult> onFixBeforeLand, Func<TResult> onDefer, Func<TResult> onDecline) => onDecline();
    }
}

/// <summary>
/// Wire form of <see cref="NitDisposition"/> and the parse path back, matched with explicit
/// <see cref="StringComparer.Ordinal"/> — see <see cref="CardKindWireFormat"/> for why.
/// </summary>
internal static class NitDispositionWireFormat
{
    private static readonly IReadOnlyDictionary<string, NitDisposition> ByWireValue =
        new Dictionary<string, NitDisposition>(StringComparer.Ordinal)
        {
            ["fix-before-land"] = NitDisposition.FixBeforeLand,
            ["defer"] = NitDisposition.Defer,
            ["decline"] = NitDisposition.Decline,
        };

    internal static string ToWireString(this NitDisposition disposition) => disposition.Match(
        onFixBeforeLand: static () => "fix-before-land",
        onDefer: static () => "defer",
        onDecline: static () => "decline");

    internal static string RecognisedValues => string.Join(", ", ByWireValue.Keys);

    internal static bool TryParse(string value, out NitDisposition disposition)
    {
        var found = ByWireValue.TryGetValue(value, out var match);
        // Every value stored in ByWireValue is a non-null NitDisposition singleton, so `match` is
        // non-null whenever `found` is true; the fallback on failure is always discarded.
        disposition = found ? match! : NitDisposition.FixBeforeLand;
        return found;
    }
}
