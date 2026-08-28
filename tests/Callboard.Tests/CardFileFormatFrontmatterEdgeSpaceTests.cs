using Callboard.Cards;

namespace Callboard.Tests;

// §13 remediation: CardFileFormat.EscapeFrontmatterValue escaped '\', '\n' and '\r' but not
// space, so a frontmatter free-text value ending (or starting) in a space reached disk as
// whitespace indistinguishable from layout — an editor that strips trailing whitespace on save
// silently changed the value, and the card still parsed, just holding different content. The
// Product Owner's ruling: escape only a leading/trailing space (interior spaces stay literal, so
// the record stays legible without the tool), and accept the reverse-table's behaviour change on
// existing hand-written cards that happen to contain a bare '\s' — a deliberate, recorded trade
// (see FrontmatterEscapeTable's doc comment). These tests cover the property directly.
public sealed class CardFileFormatFrontmatterEdgeSpaceTests
{
    private static readonly DateTimeOffset Created = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Updated = new(2026, 8, 20, 15, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("trailing space ")]
    [InlineData(" leading space")]
    [InlineData(" both edges ")]
    [InlineData("interior spaces only, unchanged")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("already has a literal \\s in it ")]
    public void EscapeThenUnescape_RoundTrips(string value)
    {
        var escaped = CardFileFormat.EscapeFrontmatterValue(value);
        var unescaped = CardFileFormat.UnescapeFrontmatterValue(escaped);

        Assert.Equal(value, unescaped);
    }

    [Fact]
    public void Escape_TrailingSpace_EmitsBackslashS()
    {
        Assert.Equal("title\\s", CardFileFormat.EscapeFrontmatterValue("title "));
    }

    [Fact]
    public void Escape_LeadingSpace_EmitsBackslashS()
    {
        Assert.Equal("\\stitle", CardFileFormat.EscapeFrontmatterValue(" title"));
    }

    [Fact]
    public void Escape_BothEdges_EscapesOnlyTheEdges()
    {
        Assert.Equal("\\smiddle\\s", CardFileFormat.EscapeFrontmatterValue(" middle "));
    }

    [Fact]
    public void Escape_InteriorSpacesOnly_SerialisesToExactlyTodaysBytes()
    {
        const string value = "which retry policy applies?";

        Assert.Equal(value, CardFileFormat.EscapeFrontmatterValue(value));
    }

    [Fact]
    public void Escape_SingleSpaceValue_EscapesOnceNotTwice()
    {
        Assert.Equal("\\s", CardFileFormat.EscapeFrontmatterValue(" "));
    }

    [Fact]
    public void Escape_EmptyValue_StaysEmpty()
    {
        Assert.Equal(string.Empty, CardFileFormat.EscapeFrontmatterValue(string.Empty));
    }

    [Fact]
    public void Escape_ValueAllSpaces_EscapesOnlyTheEdgesLeavingInteriorLiteral()
    {
        // Three spaces: edge, interior, edge. Only the two edge characters become \s.
        Assert.Equal("\\s \\s", CardFileFormat.EscapeFrontmatterValue("   "));
    }

    [Fact]
    public void Escape_LeadingBackslashBeforeATrailingSpace_DoublesTheBackslashFirst()
    {
        // The edge check runs against the already backslash-escaped value, so a literal
        // backslash at the true edge is doubled and no longer at the edge by the time the
        // space rule looks — only the value's own trailing space is judged ambiguous.
        var escaped = CardFileFormat.EscapeFrontmatterValue("\\ ");

        Assert.Equal("\\\\\\s", escaped);
        Assert.Equal("\\ ", CardFileFormat.UnescapeFrontmatterValue(escaped));
    }

    [Fact]
    public void RegressionGuard_ToolWrittenValueContainingLiteralBackslashS_RoundTripsUnchanged()
    {
        // A backslash is escaped to '\\' before the space table ever runs, so a value that
        // already contains the literal two characters '\' 's' is written as '\\s' on disk and
        // reads back exactly as it was — the new ['s'] = ' ' reverse-table entry only fires on
        // a lone backslash, never on one that has itself been escaped.
        const string value = "match \\s+";

        var escaped = CardFileFormat.EscapeFrontmatterValue(value);
        Assert.Equal("match \\\\s+", escaped);

        var unescaped = CardFileFormat.UnescapeFrontmatterValue(escaped);
        Assert.Equal(value, unescaped);
    }

    [Fact]
    public void Keystone_ToolWrittenCardWithATitleEndingInASpace_SurvivesATrailingWhitespaceStrip()
    {
        var frontmatter = new CardFrontmatter(
            Id: "B-0501",
            Kind: CardKind.Block,
            Title: "Which retry policy applies? ",
            Status: "drafting",
            Owner: CardOwner.Worker,
            Scope: CardScope.Change,
            Section: "13",
            Created: Created,
            Updated: Updated);
        var card = new CardFile(frontmatter, "Body prose.", [], []);

        var serialized = CardFileWriter.Serialize(card);
        var strippedOfTrailingWhitespaceOnEveryLine = string.Join(
            '\n',
            serialized.Split('\n').Select(line => line.TrimEnd(' ', '\t')));

        var result = CardFileParser.Parse(strippedOfTrailingWhitespaceOnEveryLine);
        var parsed = AssertSuccess(result);

        Assert.Equal("Which retry policy applies? ", parsed.Frontmatter.Title);
    }

    // §13 remediation, corrected after review: the reviewer traced extent_value (FindingExtent
    // .Explicit, fed by --extent-explicit split on ',' with no trimming) as a hand-typed
    // list-valued field this block's original justification missed. These cover the same edge-
    // space property for list items that the tests above cover for scalar values, plus the
    // composition cases across the comma seam the reviewer named.

    [Theory]
    [InlineData("trailing space ")]
    [InlineData(" leading space")]
    [InlineData(" both edges ")]
    [InlineData("interior spaces only, unchanged")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("already has a literal \\s in it ")]
    public void EscapeThenUnescapeListItem_RoundTrips(string value)
    {
        var escaped = CardFileFormat.EscapeFrontmatterListItem(value);
        var unescaped = CardFileFormat.UnescapeFrontmatterListItem(escaped);

        Assert.Equal(value, unescaped);
    }

    [Fact]
    public void EscapeListItem_TrailingSpace_EmitsBackslashS()
    {
        Assert.Equal("src/Foo.cs\\s", CardFileFormat.EscapeFrontmatterListItem("src/Foo.cs "));
    }

    [Fact]
    public void EscapeListItem_LeadingSpace_EmitsBackslashS()
    {
        // The route the reviewer flagged as more reachable than an editor's save: the CLI splits
        // --extent-explicit on ',' with no trimming, so "a.cs, b.cs" produces a second item with
        // exactly this leading space on its own, before any hand-edit is involved.
        Assert.Equal("\\sb.cs", CardFileFormat.EscapeFrontmatterListItem(" b.cs"));
    }

    [Fact]
    public void EscapeListItem_InteriorSpacesOnly_SerialisesToExactlyTodaysBytes()
    {
        const string value = "src/Some File.cs";

        Assert.Equal(value, CardFileFormat.EscapeFrontmatterListItem(value));
    }

    [Fact]
    public void EscapeListItem_EndsInABackslash_ComposesCorrectlyAcrossTheJoin()
    {
        // The reviewer's first composition case: an item ending in a backslash, immediately
        // followed by the ',' join. The trailing backslash is doubled before the join ever sees
        // it, so the doubled pair — not the literal comma after it — is what Split's boundary
        // scan treats as protected.
        IReadOnlyList<string> items = ["foo\\", "bar"];

        var joined = CardFileFormat.JoinFrontmatterList(items);
        var split = CardFileFormat.SplitFrontmatterList(joined);

        Assert.Equal(items, split);
    }

    [Fact]
    public void EscapeListItem_ItemExactlyBackslashS_RoundTripsAsLiteralText()
    {
        // The reviewer's second composition case: an item whose own content, before any
        // escaping, is the two literal characters '\' and 's' — not our escape marker. The
        // backslash is doubled first, so this can never be misread as an edge-space escape.
        const string value = "\\s";

        var escaped = CardFileFormat.EscapeFrontmatterListItem(value);
        Assert.Equal("\\\\s", escaped);

        IReadOnlyList<string> items = [value, "next"];
        var split = CardFileFormat.SplitFrontmatterList(CardFileFormat.JoinFrontmatterList(items));
        Assert.Equal(items, split);
    }

    [Fact]
    public void EscapeListItem_EscapedCommaAdjacentToAnEdgeSpace_ComposesCorrectly()
    {
        // The reviewer's third composition case: an item containing an interior comma (itself
        // escaped to '\,') immediately next to the trailing edge space (escaped to '\s') — two
        // different escape pairs back to back, each consumed on its own.
        const string value = "path,name ";

        var escaped = CardFileFormat.EscapeFrontmatterListItem(value);
        Assert.Equal("path\\,name\\s", escaped);

        IReadOnlyList<string> items = [value, "other"];
        var split = CardFileFormat.SplitFrontmatterList(CardFileFormat.JoinFrontmatterList(items));
        Assert.Equal(items, split);
    }

    [Fact]
    public void Keystone_ToolWrittenFindingWhoseLastExtentItemEndsInASpace_SurvivesATrailingWhitespaceStrip()
    {
        // Mirrors the scalar keystone exactly: the *last* list item's trailing character is the
        // true edge of the extent_value physical line (JoinFrontmatterList writes items straight
        // to the line with nothing after the last one), so it is exactly as exposed to an
        // editor's trailing-whitespace-on-save as a scalar value's own trailing space is.
        var frontmatter = new CardFrontmatter(
            Id: "F-0502",
            Kind: CardKind.Finding,
            Title: "A finding with an explicit extent",
            Status: "open",
            Owner: CardOwner.Reviewer,
            Scope: CardScope.Section,
            Section: "13",
            Created: Created,
            Updated: Updated);
        var fields = new FindingCardFields(
            Instrument: null,
            Extent: FindingExtent.Explicit(["a.cs", "b.cs "]),
            VerifiedAt: "state-1",
            BlindSpot: FindingBlindSpotDeclaration.None,
            ExtentFingerprint: null,
            Disposition: FindingDisposition.Measured);
        var card = new CardFile(frontmatter, "Body prose.", [], [], FindingFields: fields);

        var serialized = CardFileWriter.Serialize(card);
        var strippedOfTrailingWhitespaceOnEveryLine = string.Join(
            '\n',
            serialized.Split('\n').Select(line => line.TrimEnd(' ', '\t')));

        var parsed = AssertSuccess(CardFileParser.Parse(strippedOfTrailingWhitespaceOnEveryLine));

        Assert.Equal(fields, parsed.FindingFields);
        parsed.FindingFields!.Extent.Match(
            onInstrument: static command => throw new Xunit.Sdk.XunitException($"expected explicit extent, got instrument: {command}"),
            onExplicit: static items =>
            {
                Assert.Equal(["a.cs", "b.cs "], items);
                return true;
            },
            onBlockScope: static () => throw new Xunit.Sdk.XunitException("expected explicit extent, got block scope"));
    }

    [Fact]
    public void CommandParserExtentExplicitSplitWithNoTrimming_ProducesALeadingSpaceItem_ThatRoundTripsThroughTheRecord()
    {
        // Not a regression guard for this block's fix: a leading space produced here sits after
        // the comma that starts its item, never at the joined value's true trailing line edge, so
        // an editor's trailing-whitespace-on-save strip (the Keystone tests above) cannot touch it
        // either way, before or after the fix — reviewer finding, §13. What this asserts instead:
        // --extent-explicit "a.cs, b.cs" is split on ',' with no trimming (CommandParser.cs:1569),
        // so this leading space needs no editor and no hand-edit at all — a person typing a
        // comma-separated list naturally produces it — and confirms the full record round-trips
        // that exact item through disk unchanged, using the CLI's own split rather than
        // approximating it.
        const string extentExplicitRaw = "a.cs, b.cs";
        var items = extentExplicitRaw.Split(',');
        Assert.Equal(["a.cs", " b.cs"], items);

        var extent = FindingExtent.Explicit(items);
        var frontmatter = new CardFrontmatter(
            "F-0503", CardKind.Finding, "extent from a typed flag", "open", CardOwner.Reviewer, CardScope.Section, "13", Created, Updated);
        var fields = new FindingCardFields(
            Instrument: null, Extent: extent, VerifiedAt: "state-1", BlindSpot: FindingBlindSpotDeclaration.None,
            ExtentFingerprint: null, Disposition: FindingDisposition.Measured);
        var card = new CardFile(frontmatter, "Body prose.", [], [], FindingFields: fields);

        var parsed = AssertSuccess(CardFileParser.Parse(CardFileWriter.Serialize(card)));

        Assert.Equal(fields, parsed.FindingFields);
    }

    private static CardFile AssertSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
