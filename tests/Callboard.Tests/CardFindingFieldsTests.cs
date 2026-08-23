using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 6.1/6.4 — <c>instrument</c>, <c>extent</c>/<c>extent_value</c>, <c>verified_at</c> and
/// <c>blind_spot</c>/<c>blind_spot_card</c> as known frontmatter fields of a <c>finding</c> card
/// only (Architect ruling, §6 block A brief), the same "known only on this kind, preserved-unknown
/// on every other" discipline <see cref="CardBlockFieldsTests"/> already proves for a block card.
/// </summary>
public sealed class CardFindingFieldsTests
{
    private static readonly DateTimeOffset Created = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Updated = new(2026, 8, 20, 15, 30, 0, TimeSpan.Zero);

    private static CardFrontmatter Frontmatter(string id, string section = "6") => new(
        id, CardKind.Finding, "A clean finding", "open", CardOwner.Reviewer, CardScope.Section, section, Created, Updated);

    [Fact]
    public void RoundTrips_FindingWithInstrumentExtentAndBlindSpotNone()
    {
        var fields = new FindingCardFields(
            Instrument: "dotnet test",
            Extent: FindingExtent.Explicit(["src/Callboard/Cards/CardFileParser.cs"]),
            VerifiedAt: "8b44a51",
            BlindSpot: FindingBlindSpotDeclaration.None);
        var card = new CardFile(Frontmatter("F-0100"), "Nothing dangerous found.", [], [], FindingFields: fields);

        var parsed = AssertSuccess(CardFileParser.Parse(CardFileWriter.Serialize(card)));

        Assert.Equal(fields, parsed.FindingFields);
    }

    [Fact]
    public void RoundTrips_FindingWithBlindSpotRaisedAsAnotherCard()
    {
        var fields = new FindingCardFields(
            Instrument: "manual review",
            Extent: FindingExtent.Instrument("make gates"),
            VerifiedAt: "state-7",
            BlindSpot: FindingBlindSpotDeclaration.RaisedAs("O-0009"));
        var card = new CardFile(Frontmatter("F-0101"), "Clean, but see O-0009.", [], [], FindingFields: fields);

        var parsed = AssertSuccess(CardFileParser.Parse(CardFileWriter.Serialize(card)));

        Assert.Equal(fields, parsed.FindingFields);
    }

    [Fact]
    public void UndeclaredExtent_RoundTripsToBlockScope_AndEmitsNoExtentLines()
    {
        var fields = new FindingCardFields(null, FindingExtent.BlockScope, null, FindingBlindSpotDeclaration.None);
        var card = new CardFile(Frontmatter("F-0102"), "Body.", [], [], FindingFields: fields);

        var serialized = CardFileWriter.Serialize(card);
        Assert.DoesNotContain("extent:", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("extent_value:", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("instrument:", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("verified_at:", serialized, StringComparison.Ordinal);
        Assert.Contains("blind_spot: none", serialized, StringComparison.Ordinal);

        var parsed = AssertSuccess(CardFileParser.Parse(serialized));
        Assert.Equal(FindingExtent.BlockScope, parsed.FindingFields.Extent);
    }

    [Fact]
    public void Parse_FindingWithNoExtentKeyAtAll_DefaultsToBlockScope()
    {
        var parsed = AssertSuccess(CardFileParser.Parse(RawFinding("blind_spot: none\n")));

        Assert.Equal(FindingExtent.BlockScope, parsed.FindingFields.Extent);
    }

    [Fact]
    public void NonFindingKind_KeepsTheFindingKeysAsPreservedUnknown_NeverPromoted()
    {
        const string raw =
            "---\n" +
            "id: Q-0300\n" +
            "kind: question\n" +
            "title: t\n" +
            "status: open\n" +
            "owner: architect\n" +
            "scope: repository\n" +
            "section: 6\n" +
            "created: 2026-08-19T09:00:00+00:00\n" +
            "updated: 2026-08-19T09:00:00+00:00\n" +
            "instrument: dotnet test\n" +
            "extent: block-scope\n" +
            "verified_at: abc\n" +
            "blind_spot: none\n" +
            "---\n" +
            "body\n";

        var parsed = AssertSuccess(CardFileParser.Parse(raw));

        Assert.Equal(FindingCardFields.Empty, parsed.FindingFields);
        Assert.Equal(4, parsed.UnknownFrontmatterFields.Count);
        Assert.Contains(("instrument", "dotnet test"), parsed.UnknownFrontmatterFields);
        Assert.Contains(("extent", "block-scope"), parsed.UnknownFrontmatterFields);
        Assert.Contains(("verified_at", "abc"), parsed.UnknownFrontmatterFields);
        Assert.Contains(("blind_spot", "none"), parsed.UnknownFrontmatterFields);

        // Not dropped on the next write — the same extensibility rule §2 established.
        var reserialized = CardFileWriter.Serialize(parsed);
        Assert.Contains("instrument: dotnet test", reserialized, StringComparison.Ordinal);
        Assert.Contains("blind_spot: none", reserialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_FindingMissingBlindSpot_Fails()
    {
        // findings: "the system SHALL refuse to record a clean finding" without a declaration —
        // BlindSpot cannot represent "undeclared" at all (FindingBlindSpotDeclaration's own doc
        // comment), so a finding card genuinely missing the field is malformed input, the same way
        // a card missing `id` is.
        var failure = AssertFailure(CardFileParser.Parse(RawFinding(string.Empty)));

        Assert.Contains("blind_spot", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_FindingWithUnrecognisedBlindSpotForm_Fails()
    {
        var failure = AssertFailure(CardFileParser.Parse(RawFinding("blind_spot: maybe\n")));

        Assert.Contains("blind_spot", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_FindingWithRaisedAsBlindSpotButNoCardId_Fails()
    {
        var failure = AssertFailure(CardFileParser.Parse(RawFinding("blind_spot: raised-as\n")));

        Assert.Contains("blind_spot_card", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_FindingWithUnrecognisedExtentForm_Fails()
    {
        var failure = AssertFailure(CardFileParser.Parse(RawFinding("blind_spot: none\nextent: everywhere\n")));

        Assert.Contains("extent", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_FindingWithInstrumentExtentButNoExtentValue_Fails()
    {
        var failure = AssertFailure(CardFileParser.Parse(RawFinding("blind_spot: none\nextent: instrument\n")));

        Assert.Contains("extent_value", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_FindingWithExplicitExtentButNoItems_Fails()
    {
        var failure = AssertFailure(CardFileParser.Parse(RawFinding("blind_spot: none\nextent: explicit\n")));

        Assert.Contains("extent_value", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_FindingWithExplicitExtentContainingAnEmptyItem_Fails()
    {
        var failure = AssertFailure(CardFileParser.Parse(
            RawFinding("blind_spot: none\nextent: explicit\nextent_value: src/a.cs,,src/b.cs\n")));

        Assert.NotEmpty(failure);
    }

    [Fact]
    public void ExplicitExtent_CannotBeConstructedEmpty()
    {
        Assert.Throws<ArgumentException>(static () => FindingExtent.Explicit([]));
    }

    [Fact]
    public void ExplicitExtent_CannotBeConstructedWithAWhitespaceOnlyItem()
    {
        Assert.Throws<ArgumentException>(static () => FindingExtent.Explicit(["src/a.cs", "  "]));
    }

    [Fact]
    public void InstrumentExtent_CannotBeConstructedWithAnEmptyCommand()
    {
        Assert.Throws<ArgumentException>(static () => FindingExtent.Instrument(string.Empty));
    }

    [Fact]
    public void RaisedAsBlindSpot_CannotBeConstructedWithAnEmptyCardId()
    {
        Assert.Throws<ArgumentException>(static () => FindingBlindSpotDeclaration.RaisedAs(string.Empty));
    }

    // A caller cannot build FindingCardFields.BlindSpot as "undeclared": the property is typed
    // FindingBlindSpotDeclaration, not FindingBlindSpotDeclaration?, so — under this project's
    // <Nullable>enable</Nullable> + <TreatWarningsAsErrors>true</TreatWarningsAsErrors> — either of
    // the following fails the build with CS8625 rather than compiling and failing at runtime:
    //
    //   new FindingCardFields(null, FindingExtent.BlockScope, null, null);
    //   new FindingCardFields(null, FindingExtent.BlockScope, null, FindingBlindSpotDeclaration.None) with { BlindSpot = null };
    //
    // Demonstrated by omission rather than a checked-in failing compile (the harness has no way to
    // assert "this line does not compile" from inside a green test suite) — see the 6.1 DEVLOG post.

    private static string RawFinding(string findingLinesAfterVerifiedAt) =>
        "---\n" +
        "id: F-0200\n" +
        "kind: finding\n" +
        "title: t\n" +
        "status: open\n" +
        "owner: reviewer\n" +
        "scope: section\n" +
        "section: 6\n" +
        "created: 2026-08-19T09:00:00+00:00\n" +
        "updated: 2026-08-19T09:00:00+00:00\n" +
        findingLinesAfterVerifiedAt +
        "---\n" +
        "body\n";

    private static CardFile AssertSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    private static string AssertFailure(CardFileParseResult result) =>
        result.Match(
            onSuccess: success => throw new Xunit.Sdk.XunitException($"expected parse failure, got success: {success.Card}"),
            onFailure: failure => failure.Reason);
}
