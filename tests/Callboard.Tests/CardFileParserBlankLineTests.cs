using Callboard.Cards;

namespace Callboard.Tests;

// §13.8 remediation: the block loop (CardFileParser.Parse, the appended-region while loop) had
// no blank-line skipping, so a single empty line in the appended region was fatal — Parse strips
// exactly one trailing '\n' before splitting, and an editor that guarantees a final newline
// turns the file's own closing '\n' into an empty line once that one is stripped. The Product
// Owner's ruling: skip an empty line at the cursor in the block loop, between blocks and at EOF;
// leave the writer and body parsing untouched. These tests cover the class the ruling names.
public sealed class CardFileParserBlankLineTests
{
    private const string FrontmatterHeader =
        "---\n" +
        "id: X-0500\n" +
        "kind: block\n" +
        "title: t\n" +
        "status: drafting\n" +
        "owner: worker\n" +
        "scope: change\n" +
        "section: 1\n" +
        "created: 2026-08-19T09:00:00+00:00\n" +
        "updated: 2026-08-19T09:00:00+00:00\n" +
        "---\n";

    [Fact]
    public void Parse_BlankLineBetweenTwoCommentBlocks_IsSkippedAndBothCommentsParse()
    {
        const string raw =
            FrontmatterHeader +
            "body\n" +
            "<!-- callboard:comment\n" +
            "id: C-0001\n" +
            "author: worker\n" +
            "timestamp: 2026-08-19T09:00:00+00:00\n" +
            "-->\n" +
            "first\n" +
            "<!-- /callboard:comment -->\n" +
            "\n" +
            "<!-- callboard:comment\n" +
            "id: C-0002\n" +
            "author: architect\n" +
            "timestamp: 2026-08-19T09:05:00+00:00\n" +
            "-->\n" +
            "second\n" +
            "<!-- /callboard:comment -->\n";

        var parsed = AssertSuccess(CardFileParser.Parse(raw));

        Assert.Equal(2, parsed.Comments.Count);
        Assert.Equal("C-0001", parsed.Comments[0].Id);
        Assert.Equal("C-0002", parsed.Comments[1].Id);
    }

    [Fact]
    public void Parse_TrailingBlankLineAtEndOfFile_IsSkipped()
    {
        // The editor case that bit the Product Owner: the file already ends '\n', Parse strips
        // exactly one, and an editor that also guarantees a final newline leaves this one blank
        // line behind at EOF.
        const string raw =
            FrontmatterHeader +
            "body\n" +
            "<!-- callboard:comment\n" +
            "id: C-0001\n" +
            "author: worker\n" +
            "timestamp: 2026-08-19T09:00:00+00:00\n" +
            "-->\n" +
            "first\n" +
            "<!-- /callboard:comment -->\n" +
            "\n";

        var parsed = AssertSuccess(CardFileParser.Parse(raw));

        var comment = Assert.Single(parsed.Comments);
        Assert.Equal("C-0001", comment.Id);
    }

    [Fact]
    public void Parse_SeveralConsecutiveBlankLinesBetweenBlocks_AreAllSkipped()
    {
        const string raw =
            FrontmatterHeader +
            "body\n" +
            "<!-- callboard:comment\n" +
            "id: C-0001\n" +
            "author: worker\n" +
            "timestamp: 2026-08-19T09:00:00+00:00\n" +
            "-->\n" +
            "first\n" +
            "<!-- /callboard:comment -->\n" +
            "\n" +
            "\n" +
            "\n" +
            "<!-- callboard:comment\n" +
            "id: C-0002\n" +
            "author: architect\n" +
            "timestamp: 2026-08-19T09:05:00+00:00\n" +
            "-->\n" +
            "second\n" +
            "<!-- /callboard:comment -->\n" +
            "\n" +
            "\n";

        var parsed = AssertSuccess(CardFileParser.Parse(raw));

        Assert.Equal(2, parsed.Comments.Count);
        Assert.Equal("C-0002", parsed.Comments[1].Id);
    }

    [Fact]
    public void Parse_BlankLineInsideACommentBody_IsPreservedAsContent()
    {
        // The trap the brief names: a blank line inside a body must stay content — only blank
        // lines between blocks (or trailing at EOF) are layout to be dropped.
        const string raw =
            FrontmatterHeader +
            "body\n" +
            "<!-- callboard:comment\n" +
            "id: C-0001\n" +
            "author: worker\n" +
            "timestamp: 2026-08-19T09:00:00+00:00\n" +
            "-->\n" +
            "first paragraph\n" +
            "\n" +
            "second paragraph\n" +
            "<!-- /callboard:comment -->\n";

        var parsed = AssertSuccess(CardFileParser.Parse(raw));

        var comment = Assert.Single(parsed.Comments);
        Assert.Equal("first paragraph\n\nsecond paragraph", comment.Body);
    }

    [Fact]
    public void Parse_BlankLineBeforeTheFirstBlock_IsAbsorbedByThePreExistingBodyLoop_StillParses()
    {
        // Not a regression test for the appended-region skip added above: the pre-append body
        // loop stops only on a recognised block-header predicate, never on blank-ness, so this
        // blank line is consumed as trailing body content before the cursor ever reaches the
        // appended-region loop. Kept because the assertion is true and worth pinning, but its
        // name says what it actually exercises (reviewer nit, §13 remediation).
        const string raw =
            FrontmatterHeader +
            "body\n" +
            "\n" +
            "<!-- callboard:comment\n" +
            "id: C-0001\n" +
            "author: worker\n" +
            "timestamp: 2026-08-19T09:00:00+00:00\n" +
            "-->\n" +
            "first\n" +
            "<!-- /callboard:comment -->\n";

        var parsed = AssertSuccess(CardFileParser.Parse(raw));

        var comment = Assert.Single(parsed.Comments);
        Assert.Equal("C-0001", comment.Id);
    }

    [Fact]
    public void Parse_NonBlankJunkLineInTheAppendedRegion_StillFails()
    {
        // The blank-line skip must not widen into tolerating arbitrary unrecognised lines — a
        // genuinely malformed line still refuses exactly as it did before this remediation. A
        // stray non-blank line right after the body is absorbed as body content (the body loop
        // stops only on a recognised block header), so the junk has to follow a real block to
        // land in the appended-region loop this remediation touches.
        const string raw =
            FrontmatterHeader +
            "body\n" +
            "<!-- callboard:comment\n" +
            "id: C-0001\n" +
            "author: worker\n" +
            "timestamp: 2026-08-19T09:00:00+00:00\n" +
            "-->\n" +
            "first\n" +
            "<!-- /callboard:comment -->\n" +
            "this is not a recognised block header\n";

        var reason = AssertFailure(CardFileParser.Parse(raw));

        Assert.Contains("expected a comment line", reason, StringComparison.Ordinal);
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
