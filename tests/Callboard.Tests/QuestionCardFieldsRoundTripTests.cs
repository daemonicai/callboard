using System.Text.RegularExpressions;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §9 block D review finding: the seven question-only frontmatter fields
/// (<c>answered_by</c>/<c>answered_at</c>/<c>answer_decision</c>/<c>answer_inline</c>,
/// <c>deferred_by</c>/<c>deferred_at</c>/<c>deferred_target</c>) landed with no round-trip,
/// escaping, or unknown-field-survival coverage of their own — every other kind-specific field bag
/// this change has introduced (block, section, register, the refusal line itself) got exactly this
/// class of test when it landed; this one closes the gap. All seven are exercised together on one
/// card regardless of domain legality (a real question is never both answered and deferred at
/// once) — the same "wire grammar, not domain state" convention <c>RegisterCardFieldsRoundTripTests</c>
/// already follows for a decision card carrying both hazard-only and obligation-only fields at once.
/// </summary>
public sealed class QuestionCardFieldsRoundTripTests
{
    private static readonly DateTimeOffset Created = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

    private static readonly string[] QuestionOnlyKeys =
    [
        "answered_by", "answered_at", "answer_decision", "answer_inline",
        "deferred_by", "deferred_at", "deferred_target",
    ];

    [Fact]
    public void SerializedQuestionCard_WithEveryFieldSet_ParsesWithNoUnknownFrontmatterFields()
    {
        var card = BuildFullyPopulatedQuestionCard();

        var text = CardFileWriter.Serialize(card);
        var parsed = AssertParseSuccess(CardFileParser.Parse(text));

        Assert.Empty(parsed.UnknownFrontmatterFields);
        AssertQuestionFieldsEqual(card.QuestionFields, parsed.QuestionFields);
    }

    // The escaping half: a value containing a literal backslash and a newline — the two characters
    // CardFileFormat.EscapeFrontmatterValue exists to protect a scalar frontmatter value against —
    // must round-trip byte-for-byte, not merely "parse to something".
    [Fact]
    public void AnswerInlineAndDeferredTarget_ContainingBackslashesAndNewlines_RoundTripExactly()
    {
        const string trickyInline = "Because A \\ B\nand also C.";
        const string trickyTarget = "section 3 of \\later-change\nonce it exists";

        var frontmatter = new CardFrontmatter(
            "Q-0002", CardKind.Question, "A question", QuestionStatus.Answered.ToWireString(),
            CardOwner.ProductOwner, CardScope.Repository, string.Empty, Created, Created);
        var questionFields = new QuestionCardFields
        {
            AnsweredBy = CardOwner.ProductOwner,
            AnsweredAt = Created,
            AnswerInline = trickyInline,
            DeferredTarget = trickyTarget,
        };
        var card = new CardFile(frontmatter, "Body.", [], [], QuestionFields: questionFields);

        var text = CardFileWriter.Serialize(card);
        var parsed = AssertParseSuccess(CardFileParser.Parse(text));

        Assert.Equal(trickyInline, parsed.QuestionFields.AnswerInline);
        Assert.Equal(trickyTarget, parsed.QuestionFields.DeferredTarget);
        Assert.Empty(parsed.UnknownFrontmatterFields);
    }

    // Unknown-field survival, on a question card specifically: a future field this build's schema
    // does not model must be carried verbatim, never dropped and never mistaken for one of the
    // seven known question-only keys — the same CardFileRoundTripTests convention applied to the
    // isQuestionCard classification gate CardFileParser/CardFileWriter both added for this block.
    [Fact]
    public void QuestionCard_WithAnUnrecognisedFrontmatterField_SurvivesARepeatedReadModifyWriteCycle()
    {
        const string raw =
            "---\n" +
            "id: Q-0003\n" +
            "kind: question\n" +
            "title: A question\n" +
            "status: open\n" +
            "owner: product-owner\n" +
            "scope: repository\n" +
            "section: \n" +
            "created: 2026-08-25T09:00:00+00:00\n" +
            "updated: 2026-08-25T09:00:00+00:00\n" +
            "future-field: some-later-value\n" +
            "---\n" +
            "Body.\n";

        var parsed = AssertParseSuccess(CardFileParser.Parse(raw));
        Assert.Equal(("future-field", "some-later-value"), Assert.Single(parsed.UnknownFrontmatterFields));

        var text = CardFileWriter.Serialize(parsed);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var reparsed = AssertParseSuccess(CardFileParser.Parse(text));
            Assert.Equal(("future-field", "some-later-value"), Assert.Single(reparsed.UnknownFrontmatterFields));
            text = CardFileWriter.Serialize(reparsed with { Frontmatter = reparsed.Frontmatter with { Updated = Created.AddDays(cycle + 1) } });
            AssertExactlyOneLineFor(text, "future-field");
        }
    }

    // Repeated read-modify-write cycles never duplicate a known question-only line — the exact
    // defect §7 block C remediation fixed for the four register kinds, checked here for the fifth
    // kind-specific field bag this change adds.
    [Fact]
    public void ThreeReadModifyWriteCycles_NeverDuplicateAQuestionOnlyLine()
    {
        var text = CardFileWriter.Serialize(BuildFullyPopulatedQuestionCard());

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var parsed = AssertParseSuccess(CardFileParser.Parse(text));
            Assert.Empty(parsed.UnknownFrontmatterFields);
            text = CardFileWriter.Serialize(parsed with { Frontmatter = parsed.Frontmatter with { Updated = Created.AddDays(cycle + 1) } });
            foreach (var key in QuestionOnlyKeys)
            {
                AssertExactlyOneLineFor(text, key);
            }
        }
    }

    private static CardFile BuildFullyPopulatedQuestionCard()
    {
        var frontmatter = new CardFrontmatter(
            "Q-0001", CardKind.Question, "A question", QuestionStatus.Deferred.ToWireString(),
            CardOwner.ProductOwner, CardScope.Repository, string.Empty, Created, Created);
        var questionFields = new QuestionCardFields
        {
            AnsweredBy = CardOwner.ProductOwner,
            AnsweredAt = Created,
            AnswerDecisionId = "D-0099",
            AnswerInline = "A trivial inline answer",
            DeferredBy = CardOwner.Architect,
            DeferredAt = Created.AddDays(1),
            DeferredTarget = "section 3 of a-later-change",
        };
        return new CardFile(frontmatter, "Body.", [], [], QuestionFields: questionFields);
    }

    private static void AssertExactlyOneLineFor(string frontmatterText, string key)
    {
        var matches = Regex.Matches(frontmatterText, $"(?m)^{Regex.Escape(key)}: ");
        Assert.True(matches.Count == 1, $"expected exactly one '{key}: ' line, found {matches.Count}. Text:\n{frontmatterText}");
    }

    private static void AssertQuestionFieldsEqual(QuestionCardFields expected, QuestionCardFields actual)
    {
        Assert.Equal(expected.AnsweredBy, actual.AnsweredBy);
        Assert.Equal(expected.AnsweredAt, actual.AnsweredAt);
        Assert.Equal(expected.AnswerDecisionId, actual.AnswerDecisionId);
        Assert.Equal(expected.AnswerInline, actual.AnswerInline);
        Assert.Equal(expected.DeferredBy, actual.DeferredBy);
        Assert.Equal(expected.DeferredAt, actual.DeferredAt);
        Assert.Equal(expected.DeferredTarget, actual.DeferredTarget);
    }

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
