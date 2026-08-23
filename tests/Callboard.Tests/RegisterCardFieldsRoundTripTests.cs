using System.Text.RegularExpressions;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §7 block C remediation — the round-trip coverage the reviewer named as missing. 544 tests were
/// green over a writer/parser that duplicated a frontmatter line on every parse-then-write cycle,
/// because every existing register-field test asserted on the <em>parsed value</em> only — and a
/// duplicated line still parses to the correct value (the dictionary read in <c>CardFileParser</c>
/// takes whichever duplicate parses last), so a value-only assertion cannot see the file rotting
/// underneath it. These tests assert on the <b>emitted frontmatter text itself</b>.
/// </summary>
public sealed class RegisterCardFieldsRoundTripTests
{
    private static readonly DateTimeOffset Created = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SerializedRegisterCard_WithEveryFieldSet_ParsesWithNoUnknownFrontmatterFields()
    {
        var card = BuildFullyPopulatedDecisionCard();

        var text = CardFileWriter.Serialize(card);
        var parsed = AssertParseSuccess(CardFileParser.Parse(text));

        Assert.Empty(parsed.UnknownFrontmatterFields);
        AssertRegisterFieldsEqual(card.RegisterFields, parsed.RegisterFields);
    }

    // The reviewer's exact reproduction, as a permanent regression test: a card already carrying
    // owed_by/supersedes/superseded_by is read and re-written a second time (the shape every
    // *UnderExistingLock read-decide-write method in CardStore performs) — the class of operation
    // "B supersedes A, then C supersedes B" performs against B's own file. Before this fix, the
    // second write's frontmatter carried each of the three unknown keys twice.
    [Fact]
    public void SecondReadModifyWriteCycle_OnACardAlreadyCarryingTheThreeNewFields_DoesNotDuplicateAnyLine()
    {
        var card = BuildFullyPopulatedDecisionCard();
        var firstWrite = CardFileWriter.Serialize(card);
        var afterFirstRead = AssertParseSuccess(CardFileParser.Parse(firstWrite));

        // A second write of the same content, exactly as a *UnderExistingLock method's
        // `card with { ... }` would produce for an unrelated field change (Updated, here) — the
        // three new fields are carried through unchanged, not re-declared.
        var mutated = afterFirstRead with { Frontmatter = afterFirstRead.Frontmatter with { Updated = Created.AddDays(1) } };
        var secondWrite = CardFileWriter.Serialize(mutated);

        AssertExactlyOneLineForEachKey(secondWrite);

        var afterSecondRead = AssertParseSuccess(CardFileParser.Parse(secondWrite));
        Assert.Empty(afterSecondRead.UnknownFrontmatterFields);
        AssertRegisterFieldsEqual(card.RegisterFields, afterSecondRead.RegisterFields);
    }

    // Three full cycles — proves the fix holds under repetition, not merely on the second write;
    // the reported defect specifically compounds ("another cycle makes three").
    [Fact]
    public void ThreeReadModifyWriteCycles_NeverDuplicateAnyLine()
    {
        var text = CardFileWriter.Serialize(BuildFullyPopulatedDecisionCard());

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var parsed = AssertParseSuccess(CardFileParser.Parse(text));
            Assert.Empty(parsed.UnknownFrontmatterFields);
            text = CardFileWriter.Serialize(parsed with { Frontmatter = parsed.Frontmatter with { Updated = Created.AddDays(cycle + 1) } });
            AssertExactlyOneLineForEachKey(text);
        }
    }

    private static CardFile BuildFullyPopulatedDecisionCard()
    {
        var frontmatter = new CardFrontmatter(
            "D-0001", CardKind.Decision, "Adopt option B", RegisterLifecycleState.Discharged.ToWireString(),
            CardOwner.ProductOwner, CardScope.Capability, string.Empty, Created, Created);
        var registerFields = new RegisterCardFields(
            Condition: "A condition",
            Cadence: "monthly",
            DischargedBy: CardOwner.ProductOwner,
            DischargedAt: Created,
            OwedBy: "S-0001",
            Supersedes: "D-0002",
            SupersededBy: "D-0003");
        return new CardFile(frontmatter, "Body.", [], [], RegisterFields: registerFields);
    }

    private static void AssertExactlyOneLineForEachKey(string frontmatterText)
    {
        foreach (var key in RegisterCardFieldKeys.All)
        {
            var matches = Regex.Matches(frontmatterText, $"(?m)^{Regex.Escape(key)}: ");
            Assert.True(matches.Count == 1, $"expected exactly one '{key}: ' line, found {matches.Count}. Text:\n{frontmatterText}");
        }
    }

    private static void AssertRegisterFieldsEqual(RegisterCardFields expected, RegisterCardFields actual)
    {
        Assert.Equal(expected.Condition, actual.Condition);
        Assert.Equal(expected.Cadence, actual.Cadence);
        Assert.Equal(expected.DischargedBy, actual.DischargedBy);
        Assert.Equal(expected.DischargedAt, actual.DischargedAt);
        Assert.Equal(expected.OwedBy, actual.OwedBy);
        Assert.Equal(expected.Supersedes, actual.Supersedes);
        Assert.Equal(expected.SupersededBy, actual.SupersededBy);
    }

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
