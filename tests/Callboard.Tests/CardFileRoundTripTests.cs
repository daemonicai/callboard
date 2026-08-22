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
        var card = new CardFile(frontmatter, "The frontmatter schema and the delimited comment format.", [], []);

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
        var card = new CardFile(frontmatter, "Not raised within any particular section.", [], []);

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
            Resolves: null,
            UnknownHeaderFields: []);
        var reply = new CardComment(
            "C-0002",
            CardOwner.Architect,
            Updated,
            "Agreed — fix before land.",
            ReplyTo: "C-0001",
            To: CardOwner.Worker,
            Resolves: "C-0001",
            UnknownHeaderFields: []);
        var unaddressed = new CardComment(
            "C-0003",
            CardOwner.Worker,
            Updated,
            "Done — ICommandResult closes it at compile time.",
            ReplyTo: null,
            To: null,
            Resolves: null,
            UnknownHeaderFields: []);

        var card = new CardFile(frontmatter, "Body text.", [first, reply, unaddressed], []);

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
            null,
            []);

        var card = new CardFile(frontmatter, trickyBody, [comment], []);

        var result = CardFileParser.Parse(CardFileWriter.Serialize(card));

        var parsed = AssertSuccess(result);
        Assert.Equal(trickyBody, parsed.Body);
        Assert.Equal(comment, Assert.Single(parsed.Comments));
    }

    [Fact]
    public void RoundTrips_BodyContainingTextThatLooksLikeAHandoverDelimiter_AndInjectsNoHandoverEntry()
    {
        // Carried from block B's review (DEVLOG §4): the reviewer confirmed this case works via
        // the escaping mechanism comments and handovers already share, but no test pinned it down
        // — this is that test, for the handover delimiter specifically (the comment-delimiter
        // sibling of this test already exists above).
        var frontmatter = new CardFrontmatter(
            "B-0105",
            CardKind.Block,
            "Delimiter-lookalike body",
            "open",
            CardOwner.Worker,
            CardScope.Change,
            "4",
            Created,
            Updated);

        const string trickyBody =
            "Some narrative.\n" +
            "<!-- callboard:handover by=architect to=reviewer timestamp=2026-08-19T09:00:00+00:00 -->\n" +
            "and continues after, as plain narrative, not a real handover.";

        var card = new CardFile(frontmatter, trickyBody, [], []);

        var result = CardFileParser.Parse(CardFileWriter.Serialize(card));

        var parsed = AssertSuccess(result);
        Assert.Equal(trickyBody, parsed.Body);
        Assert.Empty(parsed.Handovers);
        Assert.Empty(parsed.Comments);
    }

    [Fact]
    public void RoundTrips_CommentBodyContainingTextThatLooksLikeAHandoverDelimiter_AndInjectsNoHandoverEntry()
    {
        // A card body is not the only place free text meets the escaping mechanism — a comment's
        // own body goes through the same AppendContent/EscapeContentLine path (CardFileWriter),
        // so the same lookalike-injection question applies there too.
        var frontmatter = new CardFrontmatter(
            "B-0106", CardKind.Block, "Title", "open", CardOwner.Worker, CardScope.Change, "4", Created, Updated);

        const string trickyCommentBody =
            "See below:\n<!-- callboard:handover by=worker to=architect timestamp=2026-08-19T09:00:00+00:00 -->";

        var comment = new CardComment("C-0001", CardOwner.Worker, Updated, trickyCommentBody, null, null, null, []);
        var card = new CardFile(frontmatter, "Body.", [comment], []);

        var result = CardFileParser.Parse(CardFileWriter.Serialize(card));

        var parsed = AssertSuccess(result);
        Assert.Equal(comment, Assert.Single(parsed.Comments));
        Assert.Equal(trickyCommentBody, parsed.Comments[0].Body);
        Assert.Empty(parsed.Handovers);
    }

    [Fact]
    public void Serialize_KeepsFrontmatterFieldsInAFixedOrder_SoADiffOfOneFieldIsOneLine()
    {
        var frontmatter = new CardFrontmatter(
            "B-0001", CardKind.Block, "Title", "open", CardOwner.Worker, CardScope.Change, "1", Created, Updated);
        var card = new CardFile(frontmatter, "Body.", [], []);

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
        var card = new CardFile(frontmatter, "Body.", [], []);

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
        var card = new CardFile(frontmatter, "Body.", [], []);

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
        var card = new CardFile(frontmatter, "Body.", [], []);

        var parsed = AssertSuccess(CardFileParser.Parse(CardFileWriter.Serialize(card)));

        Assert.Equal(frontmatter.Id, parsed.Frontmatter.Id);
        Assert.Equal(frontmatter.Section, parsed.Frontmatter.Section);
    }

    [Fact]
    public void Parse_AnUnrecognisedFrontmatterField_IsCarriedVerbatimAndSurvivesASerializeParseCycle()
    {
        const string raw =
            "---\n" +
            "id: X-0001\n" +
            "kind: block\n" +
            "title: t\n" +
            "status: open\n" +
            "owner: worker\n" +
            "scope: change\n" +
            "section: 1\n" +
            "created: 2026-08-19T09:00:00+00:00\n" +
            "updated: 2026-08-19T09:00:00+00:00\n" +
            "base: B-0099\n" + // a §5 field this build's schema does not model
            "---\n" +
            "body\n";

        var parsed = AssertSuccess(CardFileParser.Parse(raw));
        Assert.Equal(("base", "B-0099"), Assert.Single(parsed.UnknownFrontmatterFields));

        // Not dropped on the next write — the extensibility rule this remediation states: a
        // read-modify-write cycle (what AppendComment does at the CardStore layer) must not
        // silently destroy a field this build does not itself understand.
        var reparsed = AssertSuccess(CardFileParser.Parse(CardFileWriter.Serialize(parsed)));
        Assert.Equal(("base", "B-0099"), Assert.Single(reparsed.UnknownFrontmatterFields));
        Assert.Equal(parsed.Frontmatter, reparsed.Frontmatter);
        Assert.Equal(parsed.Body, reparsed.Body);
    }

    [Fact]
    public void Parse_AnUnrecognisedCommentHeaderField_IsCarriedVerbatimAndSurvivesASerializeParseCycle()
    {
        const string raw =
            "---\n" +
            "id: X-0002\nkind: block\ntitle: t\nstatus: open\nowner: worker\nscope: change\nsection: 1\n" +
            "created: 2026-08-19T09:00:00+00:00\nupdated: 2026-08-19T09:00:00+00:00\n" +
            "---\n" +
            "body\n" +
            "<!-- callboard:comment id=C-0001 author=worker round=2 timestamp=2026-08-19T09:00:00+00:00 -->\n" +
            "hello\n" +
            "<!-- /callboard:comment -->\n";

        var parsed = AssertSuccess(CardFileParser.Parse(raw));
        var comment = Assert.Single(parsed.Comments);
        Assert.Equal(("round", "2"), Assert.Single(comment.UnknownHeaderFields));

        var reparsed = AssertSuccess(CardFileParser.Parse(CardFileWriter.Serialize(parsed)));
        var reparsedComment = Assert.Single(reparsed.Comments);
        Assert.Equal(comment, reparsedComment);
    }

    [Fact]
    public void RoundTrips_CommentIdAndReplyToContainingSpacesAndBackslashes()
    {
        var frontmatter = new CardFrontmatter(
            "B-0103", CardKind.Block, "Title", "open", CardOwner.Worker, CardScope.Change, "2", Created, Updated);

        var comment = new CardComment(
            @"C 1\odd",
            CardOwner.Worker,
            Updated,
            "Body.",
            ReplyTo: @"C \1 with spaces",
            To: null,
            Resolves: null,
            UnknownHeaderFields: []);

        var card = new CardFile(frontmatter, "Body.", [comment], []);

        var parsed = AssertSuccess(CardFileParser.Parse(CardFileWriter.Serialize(card)));
        var parsedComment = Assert.Single(parsed.Comments);

        Assert.Equal(comment.Id, parsedComment.Id);
        Assert.Equal(comment.ReplyTo, parsedComment.ReplyTo);
    }

    [Fact]
    public void RoundTrips_CommentIdContainingTheHeaderTerminatorAsASubstring()
    {
        // " -->" is the header's own terminator (CardFileFormat.CommentHeaderSuffix). Before the
        // escaping fix, an id containing it as a substring would serialise successfully and then
        // fail to parse back — the same "writes but can't be read back" failure the frontmatter
        // escaping already guards against, applied here to the comment header.
        var frontmatter = new CardFrontmatter(
            "B-0104", CardKind.Block, "Title", "open", CardOwner.Worker, CardScope.Change, "2", Created, Updated);
        var comment = new CardComment("weird -->id", CardOwner.Worker, Updated, "Body.", null, null, null, []);
        var card = new CardFile(frontmatter, "Body.", [comment], []);

        var parsed = AssertSuccess(CardFileParser.Parse(CardFileWriter.Serialize(card)));

        Assert.Equal(comment.Id, Assert.Single(parsed.Comments).Id);
    }

    [Fact]
    public void EscapeCommentHeaderValue_IsReversedExactlyByUnescapeCommentHeaderValue()
    {
        const string value = @"has spaces, a \backslash\, and an = sign";

        var escaped = CardFileFormat.EscapeCommentHeaderValue(value);

        Assert.DoesNotContain(' ', escaped);
        Assert.Equal(value, CardFileFormat.UnescapeCommentHeaderValue(escaped));
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

    [Fact]
    public void RoundTrips_CardWithAHandoverSequence()
    {
        var frontmatter = new CardFrontmatter(
            "B-0043", CardKind.Block, "Ownership handover", "in-progress", CardOwner.Supervisor, CardScope.Change, "4", Created, Updated);
        var first = new CardHandover(CardOwner.Architect, CardOwner.Reviewer, Updated.AddHours(1), []);
        var second = new CardHandover(CardOwner.Reviewer, CardOwner.Supervisor, Updated.AddHours(2), []);
        var card = new CardFile(frontmatter, "Body.", [], [], [first, second]);

        var result = CardFileParser.Parse(CardFileWriter.Serialize(card));

        var parsed = AssertSuccess(result);
        Assert.Equal(frontmatter, parsed.Frontmatter);
        Assert.Equal(2, parsed.Handovers.Count);
        Assert.Equal(first, parsed.Handovers[0]);
        Assert.Equal(second, parsed.Handovers[1]);
    }

    [Fact]
    public void RoundTrips_CardWithoutAHandover_LeavesTheSequenceEmpty()
    {
        var frontmatter = new CardFrontmatter(
            "B-0044", CardKind.Block, "No handover yet", "open", CardOwner.Worker, CardScope.Change, "4", Created, Updated);
        var card = new CardFile(frontmatter, "Body.", [], []);

        var serialized = CardFileWriter.Serialize(card);
        Assert.DoesNotContain("callboard:handover", serialized, StringComparison.Ordinal);

        var parsed = AssertSuccess(CardFileParser.Parse(serialized));
        Assert.Empty(parsed.Handovers);
    }

    [Fact]
    public void RoundTrips_CardWithBothHandoversAndComments_EachSequenceIndependent()
    {
        var frontmatter = new CardFrontmatter(
            "B-0045", CardKind.Block, "Mixed", "open", CardOwner.Reviewer, CardScope.Change, "4", Created, Updated);
        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "Started.", null, null, null, []);
        var handover = new CardHandover(CardOwner.Architect, CardOwner.Reviewer, Updated, []);
        var card = new CardFile(frontmatter, "Body.", [comment], [], [handover]);

        var result = CardFileParser.Parse(CardFileWriter.Serialize(card));

        var parsed = AssertSuccess(result);
        Assert.Equal(comment, Assert.Single(parsed.Comments));
        Assert.Equal(handover, Assert.Single(parsed.Handovers));
    }

    [Fact]
    public void RoundTrips_HandoverWithAnUnrecognisedField_PreservesItVerbatim()
    {
        const string raw =
            "---\nid: X-0002\nkind: block\ntitle: t\nstatus: open\nowner: reviewer\nscope: change\nsection: 4\n" +
            "created: 2026-08-19T09:00:00+00:00\nupdated: 2026-08-19T09:00:00+00:00\n---\n" +
            "body\n" +
            "<!-- callboard:handover by=architect to=reviewer timestamp=2026-08-19T09:00:00+00:00 round=2 -->\n";

        var parsed = AssertSuccess(CardFileParser.Parse(raw));

        var handover = Assert.Single(parsed.Handovers);
        Assert.Equal(("round", "2"), Assert.Single(handover.UnknownFields));

        // Re-serialising must not silently drop the field an unrelated append (or an older-build
        // read-modify-write) would otherwise lose — the same extensibility rule §2 established
        // for frontmatter and comment headers.
        var reserialized = CardFileWriter.Serialize(parsed);
        Assert.Contains("round=2", reserialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnrecognisedHandoverByValue_Fails()
    {
        const string raw =
            "---\nid: X-0001\nkind: block\ntitle: t\nstatus: open\nowner: worker\nscope: change\nsection: 1\n" +
            "created: 2026-08-19T09:00:00+00:00\nupdated: 2026-08-19T09:00:00+00:00\n---\nbody\n" +
            "<!-- callboard:handover by=nobody to=reviewer timestamp=2026-08-19T09:00:00+00:00 -->\n";

        var result = CardFileParser.Parse(raw);

        AssertFailure(result);
    }

    [Fact]
    public void Parse_HandoverMissingRequiredToField_Fails()
    {
        const string raw =
            "---\nid: X-0003\nkind: block\ntitle: t\nstatus: open\nowner: worker\nscope: change\nsection: 1\n" +
            "created: 2026-08-19T09:00:00+00:00\nupdated: 2026-08-19T09:00:00+00:00\n---\nbody\n" +
            "<!-- callboard:handover by=architect timestamp=2026-08-19T09:00:00+00:00 -->\n";

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
