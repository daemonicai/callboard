using Callboard.Cards;

namespace Callboard.Tests;

// §13.8 remediation, round two: the frontmatter key-value loop (CardFileParser.Parse) had the
// same blank-line exposure the appended-region loop did, and a second, worse one: it required
// the two-character ": " separator, but CardFileWriter always emits "key: value" — for an
// empty-valued field that's "key: " with a trailing space, and an editor that strips trailing
// whitespace on save (VS Code's default, and near-universal elsewhere) turns that into "key:"
// with nothing after the colon. Opening a tool-written card and saving it, changing nothing,
// corrupted it. The Product Owner's ruling: the frontmatter loop skips blank lines, and accepts
// "key:" as that key with an empty value, alongside "key: value". The writer is unchanged.
public sealed class CardFileParserFrontmatterToleranceTests
{
    private static readonly DateTimeOffset Created = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Updated = new(2026, 8, 20, 15, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Parse_BlankLineBetweenFrontmatterFields_IsSkipped()
    {
        const string raw =
            "---\n" +
            "id: X-0600\n" +
            "kind: block\n" +
            "title: t\n" +
            "\n" +
            "status: drafting\n" +
            "owner: worker\n" +
            "scope: change\n" +
            "section: 1\n" +
            "created: 2026-08-19T09:00:00+00:00\n" +
            "updated: 2026-08-19T09:00:00+00:00\n" +
            "---\n" +
            "body\n";

        var parsed = AssertSuccess(CardFileParser.Parse(raw));

        Assert.Equal("X-0600", parsed.Frontmatter.Id);
        Assert.Equal("drafting", parsed.Frontmatter.Status);
    }

    [Fact]
    public void Parse_KeyWithNoTrailingSpace_OnAnEmptyValuedField_ParsesAsEmptyValue()
    {
        // The real defect: "section: " (the writer's own output for an empty section) with the
        // trailing space stripped by an editor's format-on-save, changing nothing else.
        const string raw =
            "---\n" +
            "id: Q-0600\n" +
            "kind: question\n" +
            "title: t\n" +
            "status: open\n" +
            "owner: architect\n" +
            "scope: repository\n" +
            "section:\n" +
            "created: 2026-08-19T09:00:00+00:00\n" +
            "updated: 2026-08-19T09:00:00+00:00\n" +
            "---\n" +
            "body\n";

        var parsed = AssertSuccess(CardFileParser.Parse(raw));

        Assert.Equal(string.Empty, parsed.Frontmatter.Section);
    }

    [Fact]
    public void Parse_KeyWithNoTrailingSpace_OnAFieldThatWouldNormallyCarryAValue_ParsesAsEmptyValue()
    {
        // Proves the tolerance is general — "key:" means "this key, empty value" for any key,
        // not a special case wired to "section" alone. An unrecognised field is the cleanest way
        // to observe the raw (key, value) pair the loop produced.
        const string raw =
            "---\n" +
            "id: X-0601\n" +
            "kind: block\n" +
            "title: t\n" +
            "status: drafting\n" +
            "owner: worker\n" +
            "scope: change\n" +
            "section: 1\n" +
            "created: 2026-08-19T09:00:00+00:00\n" +
            "updated: 2026-08-19T09:00:00+00:00\n" +
            "future-field:\n" +
            "---\n" +
            "body\n";

        var parsed = AssertSuccess(CardFileParser.Parse(raw));

        Assert.Equal(("future-field", string.Empty), Assert.Single(parsed.UnknownFrontmatterFields));
    }

    [Fact]
    public void Parse_OrdinaryKeyValueFrontmatterLine_IsUnaffected()
    {
        const string raw =
            "---\n" +
            "id: X-0602\n" +
            "kind: block\n" +
            "title: Ordinary title\n" +
            "status: drafting\n" +
            "owner: worker\n" +
            "scope: change\n" +
            "section: 1\n" +
            "created: 2026-08-19T09:00:00+00:00\n" +
            "updated: 2026-08-19T09:00:00+00:00\n" +
            "---\n" +
            "body\n";

        var parsed = AssertSuccess(CardFileParser.Parse(raw));

        Assert.Equal("Ordinary title", parsed.Frontmatter.Title);
        Assert.Equal("1", parsed.Frontmatter.Section);
    }

    [Fact]
    public void Parse_NoColonFrontmatterLine_StillFails()
    {
        // The tolerance is narrow: a colon as the line's very last character, nothing more. A
        // line with no colon at all is still malformed, with the same message as before.
        const string raw =
            "---\n" +
            "id: X-0603\n" +
            "kind: block\n" +
            "title: t\n" +
            "status: drafting\n" +
            "owner: worker\n" +
            "scope: change\n" +
            "section: 1\n" +
            "created: 2026-08-19T09:00:00+00:00\n" +
            "updated: 2026-08-19T09:00:00+00:00\n" +
            "this line has no colon at all\n" +
            "---\n" +
            "body\n";

        var reason = AssertFailure(CardFileParser.Parse(raw));

        Assert.StartsWith("malformed frontmatter line:", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AFullToolWrittenCard_SurvivesATrailingWhitespaceStripOnEveryLine()
    {
        // The real-world reproduction: opening a tool-written card in an editor and saving it —
        // changing nothing — with format-on-save trailing-whitespace stripping active. Before
        // this fix, "section: " (empty section, repository scope) became "section:" and the
        // card no longer parsed.
        var frontmatter = new CardFrontmatter(
            Id: "Q-0700",
            Kind: CardKind.Question,
            Title: "Which retry policy applies?",
            Status: "open",
            Owner: CardOwner.Architect,
            Scope: CardScope.Repository,
            Section: string.Empty,
            Created: Created,
            Updated: Updated);
        var comment = new CardComment("C-0001", CardOwner.Worker, Updated, "Body.", null, null, null, []);
        var card = new CardFile(frontmatter, "Not raised within any particular section.", [comment], []);

        var written = CardFileWriter.Serialize(card);
        var stripped = string.Join(
            '\n',
            written.Split('\n').Select(line => line.TrimEnd(' ', '\t')));

        var parsed = AssertSuccess(CardFileParser.Parse(stripped));

        Assert.Equal(frontmatter, parsed.Frontmatter);
        Assert.Equal(card.Body, parsed.Body);
        Assert.Equal(comment, Assert.Single(parsed.Comments));
    }

    private static CardFile AssertSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    private static string AssertFailure(CardFileParseResult result) =>
        result.Match(
            onSuccess: success => throw new Xunit.Sdk.XunitException($"expected parse failure, got success: {success.Card}"),
            onFailure: failure => failure.Reason);
}
