namespace Callboard.Cards;

/// <summary>
/// The per-kind scope table card-model's "Scope determines lifetime" requirement writes as prose:
/// a refusal rule over the <see cref="CardKind"/>/<see cref="CardScope"/> <em>pair</em>, not a
/// function from kind to scope — scope stays its own attribute on <see cref="CardFrontmatter"/>
/// (modelled as its own type in <see cref="CardScope"/>, not derived from kind), which is what
/// lets a rule be promoted from <see cref="CardScope.Change"/> to <see cref="CardScope.Repository"/>
/// without losing identity or thread. <see cref="CardKind.Block"/> is deliberately unconstrained —
/// the spec's scope table says nothing about it, so that is modelled here explicitly as always
/// <see cref="CardScopeValidationResult.Valid"/> rather than left to be inferred from an absent
/// case, which is indistinguishable from having been forgotten.
/// </summary>
internal static class CardScopeRules
{
    internal static CardScopeValidationResult Validate(CardKind kind, CardScope scope) => kind.Match(
        onBlock: static () => CardScopeValidationResult.Valid,
        onQuestion: () => RequireExactly(scope, CardScope.Repository, kind),
        onFinding: () => RequireExactly(scope, CardScope.Section, kind),
        onObligation: () => RequireExactly(scope, CardScope.Change, kind),
        onRule: () => ValidateRule(scope),
        onHazard: () => RequireExactly(scope, CardScope.Repository, kind),
        onDecision: () => RequireExactly(scope, CardScope.Capability, kind));

    /// <summary>
    /// <c>rule</c> is the one kind the table gives two legal scopes, and the one scenario the spec
    /// spells a specific refusal message for: "a rule applying to one section is a constraint in a
    /// brief" — <see cref="CardScope.Section"/> gets that exact wording rather than the generic
    /// message the other four constrained kinds share, and <see cref="CardScope.Capability"/> (the
    /// remaining unsupported value) gets the generic one.
    /// </summary>
    private static CardScopeValidationResult ValidateRule(CardScope scope) => scope.Match(
        onSection: static () => new CardScopeValidationResult.Refused(
            "a rule applying to one section is a constraint in a brief, not a rule; " +
            "'rule' cards take 'change' or 'repository' scope."),
        onChange: static () => CardScopeValidationResult.Valid,
        onCapability: static () => new CardScopeValidationResult.Refused(
            "'rule' cards take 'change' or 'repository' scope, not 'capability'."),
        onRepository: static () => CardScopeValidationResult.Valid);

    private static CardScopeValidationResult RequireExactly(CardScope scope, CardScope expected, CardKind kind) =>
        scope.Equals(expected)
            ? CardScopeValidationResult.Valid
            : new CardScopeValidationResult.Refused(
                $"'{kind.ToWireString()}' cards are {expected.ToWireString()}-scoped; '{scope.ToWireString()}' is not permitted.");
}
