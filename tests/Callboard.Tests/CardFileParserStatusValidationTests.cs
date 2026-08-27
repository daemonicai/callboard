using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §12 block A round two, item 2 — the "each kind's status validated against its own enum" and
/// "<c>Failure.Reason</c> naming field, value, kind and recognised values" direct coverage the
/// reviewer's first round found missing. <see cref="CardFileParser.Parse"/> is exercised end to
/// end (not <c>ValidateStatus</c> directly — it is <see langword="private"/>, and the parser's own
/// public surface is the contract this coverage answers to) via <see cref="CardFileWriter.
/// Serialize"/> round-trips, the same construction <see cref="CardRegisterDischargeTests"/>'s own
/// parse-door test already uses.
/// </summary>
public sealed class CardFileParserStatusValidationTests
{
    private static readonly DateTimeOffset Created = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);

    // Cross-kind: a value legal for one kind's vocabulary is not legal for another's just because
    // it parses as a string. "drafting" is a real BlockFlowState value; a question card SHALL NOT
    // accept it.
    [Fact]
    public void Parse_ABlockFlowStateValueOnAQuestionCard_Fails_NamingFieldValueKindAndRecognisedValues()
    {
        var failure = ParseWithStatus(CardKind.Question, "drafting");

        Assert.Contains("status", failure, StringComparison.Ordinal);
        Assert.Contains("'drafting'", failure, StringComparison.Ordinal);
        Assert.Contains("'question'", failure, StringComparison.Ordinal);
        Assert.Contains(QuestionStatusWireFormat.RecognisedValues, failure, StringComparison.Ordinal);
        Assert.DoesNotContain(BlockFlowStateWireFormat.RecognisedValues, failure, StringComparison.Ordinal);
    }

    // Cross-kind, the other direction: a register-lifecycle value is not legal on a block card.
    [Fact]
    public void Parse_ARegisterLifecycleValueOnABlockCard_Fails_NamingFieldValueKindAndRecognisedValues()
    {
        var failure = ParseWithStatus(CardKind.Block, "open");

        Assert.Contains("status", failure, StringComparison.Ordinal);
        Assert.Contains("'open'", failure, StringComparison.Ordinal);
        Assert.Contains("'block'", failure, StringComparison.Ordinal);
        Assert.Contains(BlockFlowStateWireFormat.RecognisedValues, failure, StringComparison.Ordinal);
    }

    // finding's own vocabulary: the one literal "open" (findings: never closed) — anything else,
    // including a value that is legal on some other kind, fails.
    [Fact]
    public void Parse_FindingCard_WithTheLiteralOpenStatus_Succeeds()
    {
        var result = ParseCard(CardKind.Finding, "open");

        result.Match<object?>(
            onSuccess: static _ => null,
            onFailure: static failure => throw new Xunit.Sdk.XunitException($"expected Success, got Failure: {failure.Reason}"));
    }

    [Fact]
    public void Parse_FindingCard_WithAnyOtherStatus_Fails_NamingFieldValueKindAndTheLiteral()
    {
        var failure = ParseWithStatus(CardKind.Finding, "verified");

        Assert.Contains("status", failure, StringComparison.Ordinal);
        Assert.Contains("'verified'", failure, StringComparison.Ordinal);
        Assert.Contains("'finding'", failure, StringComparison.Ordinal);
        Assert.Contains("open", failure, StringComparison.Ordinal);
    }

    // section's own vocabulary: SectionFlowStateWireFormat, not the register-lifecycle pair.
    [Fact]
    public void Parse_SectionCard_WithARegisterLifecycleValue_Fails_NamingFieldValueKindAndRecognisedValues()
    {
        var failure = ParseWithStatus(CardKind.Section, "discharged");

        Assert.Contains("status", failure, StringComparison.Ordinal);
        Assert.Contains("'discharged'", failure, StringComparison.Ordinal);
        Assert.Contains("'section'", failure, StringComparison.Ordinal);
        Assert.Contains(SectionFlowStateWireFormat.RecognisedValues, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_SectionCard_WithItsOwnLegalStatus_Succeeds()
    {
        var result = ParseCard(CardKind.Section, "open");

        result.Match<object?>(
            onSuccess: static _ => null,
            onFailure: static failure => throw new Xunit.Sdk.XunitException($"expected Success, got Failure: {failure.Reason}"));
    }

    // The four register kinds share RegisterLifecycleStateWireFormat — spot-checked on hazard and
    // decision (rule and obligation already have direct coverage of their own, via the six
    // rewritten CardCorrupt tests this block's first round landed).
    [Fact]
    public void Parse_HazardCard_WithAFlowStateValue_Fails_NamingRegisterLifecycleRecognisedValues()
    {
        var failure = ParseWithStatus(CardKind.Hazard, "in-review");

        Assert.Contains(RegisterLifecycleStateWireFormat.RecognisedValues, failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_DecisionCard_WithAFlowStateValue_Fails_NamingRegisterLifecycleRecognisedValues()
    {
        var failure = ParseWithStatus(CardKind.Decision, "in-review");

        Assert.Contains(RegisterLifecycleStateWireFormat.RecognisedValues, failure, StringComparison.Ordinal);
    }

    private static string ParseWithStatus(CardKind kind, string status) =>
        ParseCard(kind, status).Match(
            onSuccess: static success => throw new Xunit.Sdk.XunitException($"expected Failure, got Success: status '{success.Card.Frontmatter.Status}'"),
            onFailure: static failure => failure.Reason);

    private static CardFileParseResult ParseCard(CardKind kind, string status)
    {
        var frontmatter = new CardFrontmatter(
            "X-0001", kind, "Title", status, CardOwner.Architect, CardScope.Repository, string.Empty, Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], []);
        var serialized = CardFileWriter.Serialize(card);
        return CardFileParser.Parse(serialized);
    }
}
