namespace Callboard.Cards;

/// <summary>
/// The character-based token budget the working-context response is measured against (D6, §10
/// block B; working-context: "the working-context response SHALL fit a stated budget, targeting
/// under 3,000 tokens ... SHALL be a requirement of the response and not a target it may
/// exceed"). No tokenizer is used — D6 is explicit that shipping a real tokenizer's BPE
/// vocabulary in an AOT binary would tie the tool to one model family's tokenization, so token
/// count is estimated from a plain character count instead (ADR-0002 / D2).
/// </summary>
internal static class WorkingContextBudget
{
    /// <summary>The stated token target (working-context: "targeting under 3,000 tokens").</summary>
    internal const int TokenBudget = 3000;

    /// <summary>
    /// Characters-per-token divisor used to turn <see cref="TokenBudget"/> into a character
    /// ceiling. Deliberately conservative: ordinary English runs nearer 4 characters per token,
    /// so dividing by 3.0 <em>over</em>-estimates the token count a given character count
    /// represents, erring toward truncating narrative slightly early — the trade-off D6 already
    /// accepts ("slight over-truncation of narrative").
    /// </summary>
    internal const double CharactersPerToken = 3.0;

    /// <summary>
    /// Safety margin subtracted from the raw character budget (<see cref="TokenBudget"/> ×
    /// <see cref="CharactersPerToken"/>), so the ceiling this build measures against sits below,
    /// not at, the point where a real tokenizer might disagree with this estimate.
    /// </summary>
    internal const double MarginFraction = 0.10;

    /// <summary>
    /// The character ceiling the response is measured against: <see cref="TokenBudget"/> ×
    /// <see cref="CharactersPerToken"/>, less <see cref="MarginFraction"/> — 3,000 × 3.0 = 9,000,
    /// less 10% = 8,100. A compile-time constant, computed once here rather than re-derived (or
    /// hand-copied as a magic number) at each call site.
    /// </summary>
    internal const int CharacterCeiling = (int)(TokenBudget * CharactersPerToken * (1.0 - MarginFraction));

    /// <summary>
    /// The budget stated in prose, for the response to carry verbatim — working-context calls it
    /// "a stated budget"; this is what makes it stated, the same convention <see cref="
    /// WorkingContextAssembler.QueueOrderDescription"/> and <see cref="WorkingContextAssembler.
    /// ConstraintsRuleDescription"/> already follow for their own rules.
    /// </summary>
    internal static string Statement { get; } =
        $"targets under {TokenBudget} tokens, estimated as characters using a conservative " +
        $"{CharactersPerToken:0.0} characters-per-token divisor (ordinary English runs nearer 4, " +
        $"so this errs toward truncating narrative slightly early), less a {MarginFraction:P0} " +
        $"margin, for a character ceiling of {CharacterCeiling}. The register and the brief are " +
        "never shortened; only narrative — comment body text — is, and only when the ceiling would " +
        "otherwise be exceeded. Every omission is stated explicitly.";
}
