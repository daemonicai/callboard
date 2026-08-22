namespace Callboard.Cards;

/// <summary>
/// The single-letter kind prefix card-model's spec text writes identities with (<c>B-0042</c>,
/// <c>Q-0007</c>, <c>F-0031</c>, <c>D-0019</c>) — the letter alone is what lets a reader identify a
/// card's kind from its identity without looking anything up. One letter per kind, all distinct:
/// <c>B</c>lock, <c>Q</c>uestion, <c>F</c>inding, <c>O</c>bligation, <c>R</c>ule, <c>H</c>azard,
/// <c>D</c>ecision.
/// </summary>
internal static class CardIdentityPrefix
{
    internal static string PrefixFor(this CardKind kind) => kind.Match(
        onBlock: static () => "B",
        onQuestion: static () => "Q",
        onFinding: static () => "F",
        onObligation: static () => "O",
        onRule: static () => "R",
        onHazard: static () => "H",
        onDecision: static () => "D");
}
