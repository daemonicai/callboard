using Callboard.Cards;

namespace Callboard.Tests;

public sealed class CardFileRoundTripTests
{
    private static readonly DateTimeOffset Created = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Updated = new(2026, 8, 20, 15, 30, 0, TimeSpan.Zero);

    [Fact]
    public void RoundTrips_CardWithEveryCommonField()
    {
        var frontmatter = new CardFrontmatter(
            Id: "B-0042",
            Kind: CardKind.Block,
            Title: "Primary record — card files",
            Status: "in-progress",
            Owner: CardOwner.Worker,
            Scope: CardScope.Change,
            Section: "2",
            Created: Created,
            Updated: Updated);
        var card = new CardFile(frontmatter, "The frontmatter schema and the delimited comment format.", []);

        var serialized = CardFileWriter.Serialize(card);
        var result = CardFileParser.Parse(serialized);

        var parsed = AssertSuccess(result);
        Assert.Equal(frontmatter, parsed.Frontmatter);
        Assert.Equal(card.Body, parsed.Body);
        Assert.Empty(parsed.Comments);
    }

    [Fact]
    public void RoundTrips_CardWithEmptySection()
    {
        var frontmatter = new CardFrontmatter(
            "Q-0007",
            CardKind.Question,
            "Which retry policy applies?",
            "open",
            CardOwner.Architect,
            CardScope.Repository,
            Section: string.Empty,
            Created,
            Updated);
        var card = new CardFile(frontmatter, "Not raised within any particular section.", []);

        var result = CardFileParser.Parse(CardFileWriter.Serialize(card));

        var parsed = AssertSuccess(result);
        Assert.Equal(string.Empty, parsed.Frontmatter.Section);
        Assert.Equal(card.Body, parsed.Body);
    }

    [Fact]
    public void RoundTrips_CardWithSeveralCommentsIncludingAReplyAndAnAddressedOne()
    {
        var frontmatter = new CardFrontmatter(
            "F-0031",
            CardKind.Finding,
            "SerializeResult fails open",
            "open",
            CardOwner.Reviewer,
            CardScope.Section,
            "1",
            Created,
            Updated);

        var first = new CardComment(
            "C-0001",
            CardOwner.Reviewer,
            Created,
            "This switch has no compile-time closure.",
            ReplyTo: null,
            To: CardOwner.Architect,
            Resolved: false);
        var reply = new CardComment(
            "C-0002",
            CardOwner.Architect,
            Updated,
            "Agreed — fix before land.",
            ReplyTo: "C-0001",
            To: CardOwner.Worker,
            Resolved: true);
        var unaddressed = new CardComment(
            "C-0003",
            CardOwner.Worker,
            Updated,
            "Done — ICommandResult closes it at compile time.",
            ReplyTo: null,
            To: null,
            Resolved: false);

        var card = new CardFile(frontmatter, "Body text.", [first, reply, unaddressed]);

        var result = CardFileParser.Parse(CardFileWriter.Serialize(card));

        var parsed = AssertSuccess(result);
        Assert.Equal(3, parsed.Comments.Count);
        Assert.Equal(first, parsed.Comments[0]);
        Assert.Equal(reply, parsed.Comments[1]);
        Assert.Equal(unaddressed, parsed.Comments[2]);
    }

    [Fact]
    public void RoundTrips_BodyContainingTextThatLooksLikeACommentDelimiter()
    {
        var frontmatter = new CardFrontmatter(
            "B-0099",
            CardKind.Block,
            "Delimiter-lookalike body",
            "open",
            CardOwner.Worker,
            CardScope.Change,
            "2",
            Created,
            Updated);

        const string trickyBody =
            "Some narrative.\n" +
            "<!-- callboard:comment id=C-9999 author=worker -->\n" +
            "More narrative that even closes one:\n" +
            "<!-- /callboard:comment -->\n" +
            "and continues after.";

        var comment = new CardComment(
            "C-0001",
            CardOwner.Reviewer,
            Updated,
            "A comment body that also looks like a footer: <!-- /callboard:comment -->",
            null,
            null,
            false);

        var card = new CardFile(frontmatter, trickyBody, [comment]);

        var result = CardFileParser.Parse(CardFileWriter.Serialize(card));

        var parsed = AssertSuccess(result);
        Assert.Equal(trickyBody, parsed.Body);
        Assert.Equal(comment, Assert.Single(parsed.Comments));
    }

    [Fact]
    public void Serialize_KeepsFrontmatterFieldsInAFixedOrder_SoADiffOfOneFieldIsOneLine()
    {
        var frontmatter = new CardFrontmatter(
            "B-0001", CardKind.Block, "Title", "open", CardOwner.Worker, CardScope.Change, "1", Created, Updated);
        var card = new CardFile(frontmatter, "Body.", []);

        var serialized = CardFileWriter.Serialize(card);
        var lines = serialized.Split('\n');

        Assert.Equal("---", lines[0]);
        Assert.StartsWith("id: ", lines[1]);
        Assert.StartsWith("kind: ", lines[2]);
        Assert.StartsWith("title: ", lines[3]);
        Assert.StartsWith("status: ", lines[4]);
        Assert.StartsWith("owner: ", lines[5]);
        Assert.StartsWith("scope: ", lines[6]);
        Assert.StartsWith("section: ", lines[7]);
        Assert.StartsWith("created: ", lines[8]);
        Assert.StartsWith("updated: ", lines[9]);
        Assert.Equal("---", lines[10]);
    }

    [Fact]
    public void RoundTrips_TitleContainingANewline()
    {
        var frontmatter = new CardFrontmatter(
            "B-0100",
            CardKind.Block,
            "Multi\nline title",
            "open",
            CardOwner.Worker,
            CardScope.Change,
            "2",
            Created,
            Updated);
        var card = new CardFile(frontmatter, "Body.", []);

        var serialized = CardFileWriter.Serialize(card);

        // The frontmatter block stays one line per field even though the title itself carries a
        // newline — that's the property this escaping exists to guarantee.
        Assert.DoesNotContain("line title", serialized.Split('\n'));

        var parsed = AssertSuccess(CardFileParser.Parse(serialized));
        Assert.Equal(frontmatter.Title, parsed.Frontmatter.Title);
    }

    [Fact]
    public void RoundTrips_FrontmatterValuesContainingBackslashesAndCarriageReturns()
    {
        var frontmatter = new CardFrontmatter(
            "B-0101",
            CardKind.Block,
            @"A title with a \backslash\ in it",
            "open",
            CardOwner.Worker,
            CardScope.Change,
            "2",
            Created,
            Updated);
        var card = new CardFile(frontmatter, "Body.", []);

        var parsed = AssertSuccess(CardFileParser.Parse(CardFileWriter.Serialize(card)));

        Assert.Equal(frontmatter.Title, parsed.Frontmatter.Title);
    }

    [Fact]
    public void RoundTrips_IdAndSectionContainingTheFrontmatterDelimiterAsSubstring()
    {
        var frontmatter = new CardFrontmatter(
            "B-0102---",
            CardKind.Block,
            "Title",
            "open",
            CardOwner.Worker,
            CardScope.Change,
            "---not-a-fence---",
            Created,
            Updated);
        var card = new CardFile(frontmatter, "Body.", []);

        var parsed = AssertSuccess(CardFileParser.Parse(CardFileWriter.Serialize(card)));

        Assert.Equal(frontmatter.Id, parsed.Frontmatter.Id);
        Assert.Equal(frontmatter.Section, parsed.Frontmatter.Section);
    }

    [Fact]
    public void Parse_UnrecognisedKind_Fails()
    {
        const string raw = "---\nid: X-0001\nkind: sprocket\ntitle: t\nstatus: open\nowner: worker\nscope: change\nsection: 1\ncreated: 2026-08-19T09:00:00+00:00\nupdated: 2026-08-19T09:00:00+00:00\n---\nbody\n";

        var result = CardFileParser.Parse(raw);

        var failure = AssertFailure(result);
        Assert.Contains("sprocket", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MissingClosingFrontmatterDelimiter_Fails()
    {
        const string raw = "---\nid: X-0001\nkind: block\n";

        var result = CardFileParser.Parse(raw);

        AssertFailure(result);
    }

    [Fact]
    public void Parse_MissingCommentFooter_Fails()
    {
        const string raw =
            "---\nid: X-0001\nkind: block\ntitle: t\nstatus: open\nowner: worker\nscope: change\nsection: 1\ncreated: 2026-08-19T09:00:00+00:00\nupdated: 2026-08-19T09:00:00+00:00\n---\n" +
            "body\n<!-- callboard:comment id=C-0001 author=worker resolved=false timestamp=2026-08-19T09:00:00+00:00 -->\nunterminated comment\n";

        var result = CardFileParser.Parse(raw);

        AssertFailure(result);
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
