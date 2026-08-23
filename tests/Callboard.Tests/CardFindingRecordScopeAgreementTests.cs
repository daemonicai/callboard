using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §6 block B — <c>CardStore.ScopeForRaisedCard</c>'s own doc comment names this file as the check
/// that its two hardcoded scopes never drift from <see cref="CardScopeRules.Validate"/>'s
/// independent statement of the same rule. Both are asserted directly rather than through
/// <see cref="CardStore.RecordFinding"/> so a future change to either table trips this test first,
/// before it ever reaches a real write.
/// </summary>
public sealed class CardFindingRecordScopeAgreementTests
{
    [Fact]
    public void Obligation_ChangeScope_IsWhatCardScopeRulesRequires()
    {
        Assert.Equal(CardScopeValidationResult.Valid, CardScopeRules.Validate(CardKind.Obligation, CardScope.Change));
    }

    [Fact]
    public void Hazard_RepositoryScope_IsWhatCardScopeRulesRequires()
    {
        Assert.Equal(CardScopeValidationResult.Valid, CardScopeRules.Validate(CardKind.Hazard, CardScope.Repository));
    }

    [Fact]
    public void Obligation_IsNeverValidAtRepositoryScope_SoTheHardcodedChoiceIsNotAccidentallyPermissive()
    {
        var validation = CardScopeRules.Validate(CardKind.Obligation, CardScope.Repository);
        Assert.IsType<CardScopeValidationResult.Refused>(validation);
    }

    [Fact]
    public void Hazard_IsNeverValidAtChangeScope_SoTheHardcodedChoiceIsNotAccidentallyPermissive()
    {
        var validation = CardScopeRules.Validate(CardKind.Hazard, CardScope.Change);
        Assert.IsType<CardScopeValidationResult.Refused>(validation);
    }
}
