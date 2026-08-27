namespace Callboard.Cards;

/// <summary>
/// The eight recognised card kinds (card-model: "Single card entity with a kind discriminator" —
/// amended to eight, §5 block E: <c>section</c> was not one of the original seven kinds card-model
/// shipped with §4's supervisor approval; work-lifecycle's "Sections are entities" requires a
/// section to be a card, and the Architect ruled the union grows a case rather than a section being
/// represented some other way — see the §5 DEVLOG thread). Modelled the same way as
/// <see cref="Callboard.Cli.CommandOutcome"/>: a private constructor and eight sealed nested cases
/// close the hierarchy to this file, and <see cref="Match{TResult}"/> is the only way to consume a
/// value — abstract on the base, so a call site missing an argument for a case is a compile error
/// (CS7036), and a ninth case added later is a compile error everywhere <see cref="Match{TResult}"/>
/// is implemented until it is handled. A plain <c>enum</c> cannot give this: C# treats every enum
/// switch as potentially incomplete (an enum can hold any underlying integer value), so a switch
/// over it needs a default/discard arm just to compile — which then silently swallows a future case
/// instead of failing to build, exactly what "an unhandled case is a compile error" forbids.
/// </summary>
internal abstract record CardKind
{
    private CardKind()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<TResult> onBlock,
        Func<TResult> onQuestion,
        Func<TResult> onFinding,
        Func<TResult> onObligation,
        Func<TResult> onRule,
        Func<TResult> onHazard,
        Func<TResult> onDecision,
        Func<TResult> onSection);

    internal static readonly CardKind Block = new BlockCase();
    internal static readonly CardKind Question = new QuestionCase();
    internal static readonly CardKind Finding = new FindingCase();
    internal static readonly CardKind Obligation = new ObligationCase();
    internal static readonly CardKind Rule = new RuleCase();
    internal static readonly CardKind Hazard = new HazardCase();
    internal static readonly CardKind Decision = new DecisionCase();
    internal static readonly CardKind Section = new SectionCase();

    private sealed record BlockCase : CardKind
    {
        internal override TResult Match<TResult>(Func<TResult> onBlock, Func<TResult> onQuestion, Func<TResult> onFinding, Func<TResult> onObligation, Func<TResult> onRule, Func<TResult> onHazard, Func<TResult> onDecision, Func<TResult> onSection) => onBlock();
    }

    private sealed record QuestionCase : CardKind
    {
        internal override TResult Match<TResult>(Func<TResult> onBlock, Func<TResult> onQuestion, Func<TResult> onFinding, Func<TResult> onObligation, Func<TResult> onRule, Func<TResult> onHazard, Func<TResult> onDecision, Func<TResult> onSection) => onQuestion();
    }

    private sealed record FindingCase : CardKind
    {
        internal override TResult Match<TResult>(Func<TResult> onBlock, Func<TResult> onQuestion, Func<TResult> onFinding, Func<TResult> onObligation, Func<TResult> onRule, Func<TResult> onHazard, Func<TResult> onDecision, Func<TResult> onSection) => onFinding();
    }

    private sealed record ObligationCase : CardKind
    {
        internal override TResult Match<TResult>(Func<TResult> onBlock, Func<TResult> onQuestion, Func<TResult> onFinding, Func<TResult> onObligation, Func<TResult> onRule, Func<TResult> onHazard, Func<TResult> onDecision, Func<TResult> onSection) => onObligation();
    }

    private sealed record RuleCase : CardKind
    {
        internal override TResult Match<TResult>(Func<TResult> onBlock, Func<TResult> onQuestion, Func<TResult> onFinding, Func<TResult> onObligation, Func<TResult> onRule, Func<TResult> onHazard, Func<TResult> onDecision, Func<TResult> onSection) => onRule();
    }

    private sealed record HazardCase : CardKind
    {
        internal override TResult Match<TResult>(Func<TResult> onBlock, Func<TResult> onQuestion, Func<TResult> onFinding, Func<TResult> onObligation, Func<TResult> onRule, Func<TResult> onHazard, Func<TResult> onDecision, Func<TResult> onSection) => onHazard();
    }

    private sealed record DecisionCase : CardKind
    {
        internal override TResult Match<TResult>(Func<TResult> onBlock, Func<TResult> onQuestion, Func<TResult> onFinding, Func<TResult> onObligation, Func<TResult> onRule, Func<TResult> onHazard, Func<TResult> onDecision, Func<TResult> onSection) => onDecision();
    }

    private sealed record SectionCase : CardKind
    {
        internal override TResult Match<TResult>(Func<TResult> onBlock, Func<TResult> onQuestion, Func<TResult> onFinding, Func<TResult> onObligation, Func<TResult> onRule, Func<TResult> onHazard, Func<TResult> onDecision, Func<TResult> onSection) => onSection();
    }
}

/// <summary>
/// The wire form of <see cref="CardKind"/> as card-model's spec text writes it, and the parse
/// path back. Comparison against frontmatter text is explicit <see cref="StringComparer.Ordinal"/>
/// throughout — a frontmatter value is a byte sequence, not a word in a language, and
/// <c>InvariantGlobalization</c> must not be what quietly makes that true by accident.
/// </summary>
internal static class CardKindWireFormat
{
    private static readonly IReadOnlyDictionary<string, CardKind> ByWireValue =
        new Dictionary<string, CardKind>(StringComparer.Ordinal)
        {
            ["block"] = CardKind.Block,
            ["question"] = CardKind.Question,
            ["finding"] = CardKind.Finding,
            ["obligation"] = CardKind.Obligation,
            ["rule"] = CardKind.Rule,
            ["hazard"] = CardKind.Hazard,
            ["decision"] = CardKind.Decision,
            ["section"] = CardKind.Section,
        };

    internal static string ToWireString(this CardKind kind) => kind.Match(
        onBlock: static () => "block",
        onQuestion: static () => "question",
        onFinding: static () => "finding",
        onObligation: static () => "obligation",
        onRule: static () => "rule",
        onHazard: static () => "hazard",
        onDecision: static () => "decision",
        onSection: static () => "section");

    /// <summary>The recognised wire values, in the order card-model's spec text lists them.</summary>
    internal static string RecognisedValues => string.Join(", ", ByWireValue.Keys);

    /// <summary>Every recognised <see cref="CardKind"/>, in the same order as <see
    /// cref="RecognisedValues"/> — §12 block B's board view reads this to name one column per
    /// kind, rather than hand-listing the eight cases a second time. A fixed literal, not
    /// <c>ByWireValue.Values</c>: <see cref="Dictionary{TKey,TValue}"/> enumeration order is an
    /// implementation detail, not a contract, and this order is one a caller renders by.</summary>
    internal static readonly IReadOnlyList<CardKind> AllKinds =
    [
        CardKind.Block,
        CardKind.Question,
        CardKind.Finding,
        CardKind.Obligation,
        CardKind.Rule,
        CardKind.Hazard,
        CardKind.Decision,
        CardKind.Section,
    ];

    /// <summary>The four kinds register: "SHALL NOT occupy flow states" (§7 block A) — obligation,
    /// rule, hazard, decision, in <see cref="AllKinds"/>'s own order. §12 block B's register area
    /// reads this to name one lane per register kind, rather than hand-listing the four a second
    /// time.</summary>
    internal static readonly IReadOnlyList<CardKind> RegisterKinds =
        [.. AllKinds.Where(static kind => ReferenceEquals(kind, CardKind.Obligation)
            || ReferenceEquals(kind, CardKind.Rule)
            || ReferenceEquals(kind, CardKind.Hazard)
            || ReferenceEquals(kind, CardKind.Decision))];

    /// <summary>The column/lane heading for <paramref name="kind"/> — its own wire value
    /// capitalised, since the wire value itself (<c>"block"</c>, <c>"hazard"</c>) is what
    /// card-model's spec text already names each kind by; no separate label vocabulary is
    /// introduced.</summary>
    internal static string DisplayName(this CardKind kind)
    {
        var wire = kind.ToWireString();
        return string.Concat(char.ToUpperInvariant(wire[0]).ToString(), wire.AsSpan(1));
    }

    internal static bool TryParse(string value, out CardKind kind)
    {
        var found = ByWireValue.TryGetValue(value, out var match);
        // Every value stored in ByWireValue is a non-null CardKind singleton, so `match` is
        // non-null whenever `found` is true; the fallback to Block on failure is discarded by
        // every caller, which always checks the returned bool first.
        kind = found ? match! : CardKind.Block;
        return found;
    }
}
