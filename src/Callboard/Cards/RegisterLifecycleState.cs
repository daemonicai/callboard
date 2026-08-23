namespace Callboard.Cards;

/// <summary>
/// The two states a <c>rule</c>, <c>hazard</c>, <c>obligation</c> or <c>decision</c> card's own
/// <c>status</c> field occupies (register: "Register kinds have a two-state lifecycle" — "SHALL be
/// <c>open</c> or <c>discharged</c> and SHALL NOT occupy flow states"): <c>open</c> or
/// <c>discharged</c>. Modelled the same way as <see cref="BlockFlowState"/> and
/// <see cref="SectionFlowState"/> — a private constructor and two sealed nested cases close the
/// hierarchy to this file, and <see cref="Match{TResult}"/> is the only way to consume a value. See
/// <see cref="CardKind"/>'s doc comment for why this is a closed union and not a C# <c>enum</c>.
///
/// <para>
/// <b>Its own type, alongside <see cref="BlockFlowState"/> and <see cref="SectionFlowState"/>, never
/// folded into either (§7 block A brief).</b> A block flows through seven states; a section is
/// open/closed; a register card is open/discharged — three different vocabularies over three
/// different kinds of thing, each with its own wire form. Widening <see cref="BlockFlowState"/> or
/// <see cref="SectionFlowState"/> to also carry <c>discharged</c> would let a register card's status
/// parse successfully against a flow-state reader, which is exactly the "SHALL NOT occupy flow
/// states" the requirement forbids — this type existing separately, and
/// <see cref="RegisterLifecycleStateWireFormat.TryParse"/> being the only reader that ever succeeds
/// on a register card's <c>status</c>, is what makes that a structural fact rather than a documented
/// intention. <see cref="CardStore.DischargeRegisterCardUnderExistingLock"/> is where the refusal
/// half of "SHALL NOT" actually runs: a register card whose recorded <c>status</c> does not parse
/// here (a hand-edited flow-state value, e.g. <c>briefed</c>) is reported as corrupt rather than
/// silently treated as open — a real, exercised code path, not a comment asserting the invariant.
/// </para>
/// </summary>
internal abstract record RegisterLifecycleState
{
    private RegisterLifecycleState()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<TResult> onOpen,
        Func<TResult> onDischarged);

    internal static readonly RegisterLifecycleState Open = new OpenCase();
    internal static readonly RegisterLifecycleState Discharged = new DischargedCase();

    private sealed record OpenCase : RegisterLifecycleState
    {
        internal override TResult Match<TResult>(Func<TResult> onOpen, Func<TResult> onDischarged) => onOpen();
    }

    private sealed record DischargedCase : RegisterLifecycleState
    {
        internal override TResult Match<TResult>(Func<TResult> onOpen, Func<TResult> onDischarged) => onDischarged();
    }
}

/// <summary>
/// The wire form of <see cref="RegisterLifecycleState"/> — the text a register card's <c>status</c>
/// field carries — and the parse path back. Ordinal comparison throughout, same reason as
/// <see cref="CardKindWireFormat"/>.
/// </summary>
internal static class RegisterLifecycleStateWireFormat
{
    private static readonly IReadOnlyDictionary<string, RegisterLifecycleState> ByWireValue =
        new Dictionary<string, RegisterLifecycleState>(StringComparer.Ordinal)
        {
            ["open"] = RegisterLifecycleState.Open,
            ["discharged"] = RegisterLifecycleState.Discharged,
        };

    internal static string ToWireString(this RegisterLifecycleState state) => state.Match(
        onOpen: static () => "open",
        onDischarged: static () => "discharged");

    /// <summary>The recognised wire values, in the order register's spec text lists them.</summary>
    internal static string RecognisedValues => string.Join(", ", ByWireValue.Keys);

    internal static bool TryParse(string value, out RegisterLifecycleState state)
    {
        var found = ByWireValue.TryGetValue(value, out var match);
        // Every value stored in ByWireValue is a non-null RegisterLifecycleState singleton, so
        // `match` is non-null whenever `found` is true; the fallback to Open on failure is
        // discarded by every caller, which always checks the returned bool first.
        state = found ? match! : RegisterLifecycleState.Open;
        return found;
    }
}
