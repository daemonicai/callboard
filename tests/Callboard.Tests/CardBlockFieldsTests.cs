using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 5.4 — <c>base</c>, <c>reviewed_state</c>, <c>tasks</c>, <c>round</c> and <c>blocked_by</c> as
/// known frontmatter fields of a <c>block</c> card only (Architect ruling, §5 block A brief). The
/// stated hazard: preserved-unknown values are stored raw and never tool-escaped, so promoting a
/// key to a known field moves it onto the escaping path — whatever the write path does, the read
/// path must invert exactly, checked here on the file's bytes, not the parsed object.
/// </summary>
public sealed class CardBlockFieldsTests
{
    private static readonly DateTimeOffset Created = new(2026, 8, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Updated = new(2026, 8, 20, 15, 30, 0, TimeSpan.Zero);

    [Fact]
    public void RoundTrips_BlockCardCarryingAllFiveFields()
    {
        var frontmatter = new CardFrontmatter(
            "B-0200", CardKind.Block, "Carry brief context", "briefed", CardOwner.Worker,
            CardScope.Change, "5", Created, Updated);
        var blockFields = new BlockCardFields(
            Base: "abc1234",
            ReviewedState: "def5678",
            Tasks: ["5.1", "5.4"],
            Round: 2,
            BlockedBy: ["Q-0007"]);
        var card = new CardFile(frontmatter, "Body.", [], [], BlockFields: blockFields);

        var parsed = AssertSuccess(CardFileParser.Parse(CardFileWriter.Serialize(card)));

        Assert.Equal(blockFields, parsed.BlockFields);
    }

    [Fact]
    public void RoundTrips_BlockCardWithNoneOfTheFiveFieldsSet_EmitsNoBlockLinesAtAll()
    {
        var frontmatter = new CardFrontmatter(
            "B-0201", CardKind.Block, "Nothing recorded yet", "drafting", CardOwner.Architect,
            CardScope.Change, "5", Created, Updated);
        var card = new CardFile(frontmatter, "Body.", [], []);

        var serialized = CardFileWriter.Serialize(card);

        Assert.DoesNotContain("base:", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("reviewed_state:", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("tasks:", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("round:", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("blocked_by:", serialized, StringComparison.Ordinal);

        var parsed = AssertSuccess(CardFileParser.Parse(serialized));
        Assert.Equal(BlockCardFields.Empty, parsed.BlockFields);
    }

    [Fact]
    public void NonBlockKind_KeepsTheFiveKeysAsPreservedUnknown_NeverPromoted()
    {
        // The Architect ruling this block scopes the hazard to: the same five keys, hand-written
        // on a card of any other kind, must stay exactly where they were before this type
        // existed — on UnknownFrontmatterFields, untouched — never promoted onto BlockFields.
        const string raw =
            "---\n" +
            "id: Q-0300\n" +
            "kind: question\n" +
            "title: t\n" +
            "status: open\n" +
            "owner: architect\n" +
            "scope: repository\n" +
            "section: 5\n" +
            "created: 2026-08-19T09:00:00+00:00\n" +
            "updated: 2026-08-19T09:00:00+00:00\n" +
            "base: C:\\north\n" +
            "reviewed_state: xyz\n" +
            "tasks: 5.1,5.4\n" +
            "round: 2\n" +
            "blocked_by: Q-0007\n" +
            "---\n" +
            "body\n";

        var parsed = AssertSuccess(CardFileParser.Parse(raw));

        Assert.Equal(BlockCardFields.Empty, parsed.BlockFields);
        Assert.Equal(
            new (string Key, string RawValue)[]
            {
                ("base", @"C:\north"),
                ("reviewed_state", "xyz"),
                ("tasks", "5.1,5.4"),
                ("round", "2"),
                ("blocked_by", "Q-0007"),
            },
            parsed.UnknownFrontmatterFields);

        // Never dropped, never re-homed, on the next write either.
        var reparsed = AssertSuccess(CardFileParser.Parse(CardFileWriter.Serialize(parsed)));
        Assert.Equal(parsed.UnknownFrontmatterFields, reparsed.UnknownFrontmatterFields);
        Assert.Equal(BlockCardFields.Empty, reparsed.BlockFields);
    }

    [Fact]
    public void Parse_BlockCardWithInvalidRound_Fails()
    {
        const string raw =
            "---\n" +
            "id: B-0301\nkind: block\ntitle: t\nstatus: open\nowner: worker\nscope: change\nsection: 5\n" +
            "created: 2026-08-19T09:00:00+00:00\nupdated: 2026-08-19T09:00:00+00:00\n" +
            "round: not-a-number\n" +
            "---\n" +
            "body\n";

        var result = CardFileParser.Parse(raw);

        result.Match<object?>(
            onSuccess: success => throw new Xunit.Sdk.XunitException($"expected failure, got success: {success.Card}"),
            onFailure: failure =>
            {
                Assert.Contains("round", failure.Reason, StringComparison.Ordinal);
                return null;
            });
    }

    /// <summary>
    /// The hazard this block must close, stated in full in the §5 block A brief: preserved
    /// unknown values are stored raw and never tool-escaped, so promoting a key to a known field
    /// moves it onto the escaping path — a hand-written value like <c>base: C:\north</c> can gain
    /// a newline on the next read if the write path's escaping isn't inverted exactly by the read
    /// path. This is that test: a hand-authored card carrying awkward raw values in all five keys
    /// (backslashes, an embedded escaped comma, an escaped newline and carriage return) round-trips
    /// byte-identically through parse → write — asserted on the file's bytes, per §3's rule that
    /// green tests on the parsed object do not exercise the machine contract.
    /// </summary>
    [Fact]
    public void HandAuthoredCard_WithAwkwardRawValuesInAllFiveBlockFields_RoundTripsByteIdentically()
    {
        const string raw =
            "---\n" +
            "id: B-0302\n" +
            "kind: block\n" +
            "title: Byte-identical hazard\n" +
            "status: briefed\n" +
            "owner: worker\n" +
            "scope: change\n" +
            "section: 5\n" +
            "created: 2026-08-19T09:00:00.0000000+00:00\n" + // DateTimeOffset "O" always carries 7 fractional digits — the raw text must match what FormatTimestamp re-emits for the byte comparison below to be meaningful
            "updated: 2026-08-19T09:00:00.0000000+00:00\n" +
            "base: C:\\\\north\\\\tmp\n" + // escaped form of the raw value C:\north\tmp
            "reviewed_state: line1\\nline2\\rline3\n" + // escaped form of a value with a real \n and \r
            "tasks: 5.1,5\\,4-with-comma,back\\\\slash\n" + // three items, one with an escaped comma, one with an escaped backslash
            "round: 3\n" +
            "blocked_by: B-0001,Q-0002\\\\odd\n" +
            "---\n" +
            "Body text.\n";

        var parsed = AssertSuccess(CardFileParser.Parse(raw));

        // The escaping actually inverted to the values a human intended, not left half-escaped.
        Assert.Equal("C:\\north\\tmp", parsed.BlockFields.Base);
        Assert.Equal("line1\nline2\rline3", parsed.BlockFields.ReviewedState);
        Assert.Equal(["5.1", "5,4-with-comma", "back\\slash"], parsed.BlockFields.Tasks);
        Assert.Equal(3, parsed.BlockFields.Round);
        Assert.Equal(["B-0001", "Q-0002\\odd"], parsed.BlockFields.BlockedBy);

        var reserialized = CardFileWriter.Serialize(parsed);

        Assert.Equal(Encoding.UTF8.GetBytes(raw), Encoding.UTF8.GetBytes(reserialized));
    }

    /// <summary>
    /// Reviewer finding 1 (§5 block A review) — closed by making the ambiguous value
    /// unrepresentable rather than encoding around it: an empty or whitespace-only item in
    /// <c>Tasks</c>/<c>BlockedBy</c> must be rejected at construction, not silently accepted and
    /// later confused with an empty list. This is the guard-fires test §2's rule asks for — not
    /// merely that a valid list is permitted.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RefusesAnEmptyOrWhitespaceOnlyTasksItem(string badItem)
    {
        Assert.Throws<ArgumentException>(() =>
            new BlockCardFields(null, null, ["5.1", badItem], null, []));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_RefusesAnEmptyOrWhitespaceOnlyBlockedByItem(string badItem)
    {
        Assert.Throws<ArgumentException>(() =>
            new BlockCardFields(null, null, [], null, ["B-0001", badItem]));
    }

    [Fact]
    public void Constructor_AcceptsTasksAndBlockedByWithNoEmptyItems()
    {
        var fields = new BlockCardFields(null, null, ["5.1", "5.4"], null, ["B-0001"]);

        Assert.Equal(["5.1", "5.4"], fields.Tasks);
        Assert.Equal(["B-0001"], fields.BlockedBy);
    }

    /// <summary>
    /// Reviewer's re-audit, bypass 1: <c>with</c> uses the record's synthesized clone-and-set
    /// path, which sets <c>init</c> properties directly — it must go through the same validation
    /// as the constructor, not around it.
    /// </summary>
    [Fact]
    public void WithExpression_RefusesAnEmptyOrWhitespaceOnlyTasksItem()
    {
        Assert.Throws<ArgumentException>(() => BlockCardFields.Empty with { Tasks = ["", "  "] });
    }

    [Fact]
    public void WithExpression_RefusesAnEmptyOrWhitespaceOnlyBlockedByItem()
    {
        Assert.Throws<ArgumentException>(() => BlockCardFields.Empty with { BlockedBy = ["", "  "] });
    }

    /// <summary>
    /// Reviewer's re-audit, bypass 2: a caller who constructs with a mutable list and keeps the
    /// reference must not be able to reach back into an already-built value by mutating the
    /// source afterward — the built value must be defensively copied at construction time.
    /// </summary>
    [Fact]
    public void Constructor_DefensivelyCopiesTasks_SoMutatingTheSourceListAfterConstructionLeavesTheBuiltValueUnchanged()
    {
        var source = new List<string> { "5.1" };
        var fields = new BlockCardFields(null, null, source, null, []);

        source.Add("");

        Assert.Equal(["5.1"], fields.Tasks);
    }

    [Fact]
    public void Constructor_DefensivelyCopiesBlockedBy_SoMutatingTheSourceListAfterConstructionLeavesTheBuiltValueUnchanged()
    {
        var source = new List<string> { "B-0001" };
        var fields = new BlockCardFields(null, null, [], null, source);

        source.Add("   ");

        Assert.Equal(["B-0001"], fields.BlockedBy);
    }

    /// <summary>
    /// The documented answer to the Architect's question: hand-authored input *can* still reach
    /// the parser with an empty item (<c>tasks: ,</c> splits into two empty strings) — this is
    /// what happens to it. The parser applies the same rule the constructor enforces, earlier,
    /// over the raw split items, so the empty item never reaches the constructor as an unhandled
    /// exception — it becomes an ordinary parse failure instead, same channel as an invalid
    /// <c>round</c>.
    /// </summary>
    [Fact]
    public void Parse_BlockCardWithAnEmptyTasksItem_Fails()
    {
        const string raw =
            "---\n" +
            "id: B-0303\nkind: block\ntitle: t\nstatus: open\nowner: worker\nscope: change\nsection: 5\n" +
            "created: 2026-08-19T09:00:00+00:00\nupdated: 2026-08-19T09:00:00+00:00\n" +
            "tasks: ,\n" +
            "---\n" +
            "body\n";

        var result = CardFileParser.Parse(raw);

        result.Match<object?>(
            onSuccess: success => throw new Xunit.Sdk.XunitException($"expected failure, got success: {success.Card}"),
            onFailure: failure =>
            {
                Assert.Contains("tasks", failure.Reason, StringComparison.Ordinal);
                return null;
            });
    }

    [Fact]
    public void Parse_BlockCardWithAWhitespaceOnlyBlockedByItem_Fails()
    {
        const string raw =
            "---\n" +
            "id: B-0304\nkind: block\ntitle: t\nstatus: open\nowner: worker\nscope: change\nsection: 5\n" +
            "created: 2026-08-19T09:00:00+00:00\nupdated: 2026-08-19T09:00:00+00:00\n" +
            "blocked_by: B-0001, \n" + // second item is a single space after the comma
            "---\n" +
            "body\n";

        var result = CardFileParser.Parse(raw);

        result.Match<object?>(
            onSuccess: success => throw new Xunit.Sdk.XunitException($"expected failure, got success: {success.Card}"),
            onFailure: failure =>
            {
                Assert.Contains("blocked_by", failure.Reason, StringComparison.Ordinal);
                return null;
            });
    }

    private static CardFile AssertSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
