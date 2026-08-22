using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 4.4 — the per-kind scope table (card-model: "Scope determines lifetime") as a refusal rule over
/// the kind/scope pair, not a function from kind to scope. Every refusal here is proved refusing,
/// not merely proved permitting the one legal scope — see §2's working rule, cited in the block A
/// brief.
/// </summary>
public sealed class CardScopeRulesTests
{
    [Fact]
    public void Block_AcceptsEveryScope_BecauseTheSpecConstrainsItNot()
    {
        AssertValid(CardScopeRules.Validate(CardKind.Block, CardScope.Section));
        AssertValid(CardScopeRules.Validate(CardKind.Block, CardScope.Change));
        AssertValid(CardScopeRules.Validate(CardKind.Block, CardScope.Capability));
        AssertValid(CardScopeRules.Validate(CardKind.Block, CardScope.Repository));
    }

    [Fact]
    public void Question_AcceptsRepositoryScope() =>
        AssertValid(CardScopeRules.Validate(CardKind.Question, CardScope.Repository));

    [Theory]
    [InlineData("Section")]
    [InlineData("Change")]
    [InlineData("Capability")]
    public void Question_RefusesEveryOtherScope(string scopeName) =>
        AssertRefused(CardScopeRules.Validate(CardKind.Question, ScopeByName(scopeName)));

    [Fact]
    public void Hazard_AcceptsRepositoryScope() =>
        AssertValid(CardScopeRules.Validate(CardKind.Hazard, CardScope.Repository));

    [Theory]
    [InlineData("Section")]
    [InlineData("Change")]
    [InlineData("Capability")]
    public void Hazard_RefusesEveryOtherScope(string scopeName) =>
        AssertRefused(CardScopeRules.Validate(CardKind.Hazard, ScopeByName(scopeName)));

    [Fact]
    public void Obligation_AcceptsChangeScope() =>
        AssertValid(CardScopeRules.Validate(CardKind.Obligation, CardScope.Change));

    [Theory]
    [InlineData("Section")]
    [InlineData("Capability")]
    [InlineData("Repository")]
    public void Obligation_RefusesEveryOtherScope(string scopeName) =>
        AssertRefused(CardScopeRules.Validate(CardKind.Obligation, ScopeByName(scopeName)));

    [Fact]
    public void Decision_AcceptsCapabilityScope() =>
        AssertValid(CardScopeRules.Validate(CardKind.Decision, CardScope.Capability));

    [Theory]
    [InlineData("Section")]
    [InlineData("Change")]
    [InlineData("Repository")]
    public void Decision_RefusesEveryOtherScope(string scopeName) =>
        AssertRefused(CardScopeRules.Validate(CardKind.Decision, ScopeByName(scopeName)));

    [Fact]
    public void Finding_AcceptsSectionScope() =>
        AssertValid(CardScopeRules.Validate(CardKind.Finding, CardScope.Section));

    [Theory]
    [InlineData("Change")]
    [InlineData("Capability")]
    [InlineData("Repository")]
    public void Finding_RefusesEveryOtherScope(string scopeName) =>
        AssertRefused(CardScopeRules.Validate(CardKind.Finding, ScopeByName(scopeName)));

    [Fact]
    public void Rule_AcceptsChangeScope() =>
        AssertValid(CardScopeRules.Validate(CardKind.Rule, CardScope.Change));

    [Fact]
    public void Rule_AcceptsRepositoryScope_AfterPromotion() =>
        AssertValid(CardScopeRules.Validate(CardKind.Rule, CardScope.Repository));

    [Fact]
    public void Rule_RefusesSectionScope_NamingThatARuleIsAConstraintInABrief()
    {
        var reason = AssertRefused(CardScopeRules.Validate(CardKind.Rule, CardScope.Section));
        Assert.Contains("a constraint in a brief", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Rule_RefusesCapabilityScope() =>
        AssertRefused(CardScopeRules.Validate(CardKind.Rule, CardScope.Capability));

    private static CardScope ScopeByName(string name) => name switch
    {
        "Section" => CardScope.Section,
        "Change" => CardScope.Change,
        "Capability" => CardScope.Capability,
        "Repository" => CardScope.Repository,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown scope name in test data."),
    };

    private static void AssertValid(CardScopeValidationResult result) =>
        result.Match<object?>(
            onValid: static () => null,
            onRefused: refused => throw new Xunit.Sdk.XunitException($"expected valid, got refused: {refused.Reason}"));

    private static string AssertRefused(CardScopeValidationResult result) =>
        result.Match(
            onValid: static () => throw new Xunit.Sdk.XunitException("expected refused, got valid."),
            onRefused: refused => refused.Reason);
}
