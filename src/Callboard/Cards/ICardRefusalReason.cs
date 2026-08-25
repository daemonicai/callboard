namespace Callboard.Cards;

/// <summary>
/// Implemented by every refusal-shaped case of a card-store outcome union that resolved a real,
/// parsed card (process-enforcement: "Refusals are explained and attributable" — "Every refused
/// transition SHALL state which rule refused it and what would satisfy that rule", §9 block A).
/// A tool-failure case (<c>ToolFailure</c>) or a reported-failure case (<c>CardCorrupt</c>) never
/// implements this — those two are kept distinct from a refusal by every outcome union's own doc
/// comment (ADR-0001: enforcement unavailable is a tool-failure, never a refusal), and this
/// interface is exactly the boundary a caller reacts to when deciding what to record: only a case
/// that implements it is eligible to be written as a <see cref="CardRefusalEntry"/> against the
/// card that produced it. A refusal-shaped case that never resolved a card at all — no card at the
/// path, a layout mismatch, an unparseable file (Architect ruling, §9 base: "only a card-addressed
/// refusal records") — is refusal-shaped for the CLI's exit-code purposes but does not implement
/// this either, since there is nothing to record it against.
/// </summary>
internal interface ICardRefusalReason
{
    /// <summary>The rule that refused the attempt, named the way an agent reading the refusal
    /// would recognise it — free text naming the requirement, never a machine code.</summary>
    string RefusingRule { get; }

    /// <summary>What would satisfy the rule that refused — stated concretely enough that a caller
    /// can act on it without inferring the fix from the rule name alone.</summary>
    string Remedy { get; }
}
