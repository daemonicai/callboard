using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 5.8 — recording a supervisor verdict under lock (§5 block E, the same read-decide-write shape
/// §5 block C's <see cref="CardStore.ApplyBlockTransition"/> established). Owed evidence 2: a
/// recorded verdict carries its range and acting role — proven here by reading the field back off
/// the entry actually appended, not the argument the test happened to pass.
/// </summary>
public sealed class CardSectionVerdictTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-section-verdict-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;

    public CardSectionVerdictTests()
    {
        _directory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // Owed evidence 2: the recorded entry carries the range (both endpoints) and the acting role —
    // not just that a verdict landed. What would have to break for this to go red: RecordGateResult
    // recording the verdict value but dropping RangeFrom/RangeTo/By, or the CLI-level test's
    // equivalent silently substituting a default.
    [Fact]
    public void RecordSectionVerdict_FirstRecording_CarriesRangeAndActingRole()
    {
        var path = WriteInitialSectionCard("s-0001", "S-0001");

        var outcome = CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "e055e5b", "a52cd7a", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName);

        var recorded = AssertRecorded(outcome);
        Assert.Equal(SectionVerdict.RequestChanges, recorded.Entry.Verdict);
        Assert.Equal("e055e5b", recorded.Entry.RangeFrom);
        Assert.Equal("a52cd7a", recorded.Entry.RangeTo);
        Assert.Equal(CardOwner.Supervisor, recorded.Entry.By);
        Assert.Equal(Created, recorded.Entry.Timestamp);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var only = Assert.Single(read.SectionFields.Verdicts);
        Assert.Equal(SectionVerdict.RequestChanges, only.Verdict);
        Assert.Equal("e055e5b", only.RangeFrom);
        Assert.Equal("a52cd7a", only.RangeTo);
        Assert.Equal(CardOwner.Supervisor, only.By);
    }

    // A section can accumulate more than one verdict across remediation rounds — a second verdict
    // is a second entry, not an upsert (unlike RecordGateResult's label-keyed replace). What would
    // have to break for this to go red: RecordSectionVerdict replacing the first entry instead of
    // appending, or CardFileWriter/CardFileParser dropping one on the round trip.
    [Fact]
    public void RecordSectionVerdict_SecondRecording_AppendsRatherThanReplacing()
    {
        var path = WriteInitialSectionCard("s-0002", "S-0002");

        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.RequestChanges, "e055e5b", "cdcd6fa", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName));
        AssertRecorded(CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.Approve, "e055e5b", "a52cd7a", CardOwner.Supervisor, Created.AddDays(1), TimeSpan.FromSeconds(5), ChangeName));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(2, read.SectionFields.Verdicts.Length);
        Assert.Equal(SectionVerdict.RequestChanges, read.SectionFields.Verdicts[0].Verdict);
        Assert.Equal(SectionVerdict.Approve, read.SectionFields.Verdicts[1].Verdict);
    }

    [Fact]
    public void RecordSectionVerdict_TargetIsNotASectionCard_Refuses()
    {
        var path = Path.Combine(_directory, "q-0001.md");
        var frontmatter = new CardFrontmatter(
            "Q-0001", CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Change, "5", Created, Created);
        AssertWriteSuccess(CardStore.WriteCard(_root, path, new NewCardFile(frontmatter, "Body."), TimeSpan.FromSeconds(5), ChangeName));

        var outcome = CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.Approve, "a", "b", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notASection = Assert.IsType<CardSectionVerdictOutcome.NotASectionCard>(outcome);
        Assert.Equal(CardKind.Question, notASection.Kind);
    }

    [Fact]
    public void RecordSectionVerdict_WhenNoCardExistsAtThatPath_Fails()
    {
        var path = Path.Combine(_directory, "missing.md");

        var outcome = CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.Approve, "a", "b", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notFound = Assert.IsType<CardSectionVerdictOutcome.CardNotFound>(outcome);
        Assert.Equal(path, notFound.FilePath);
    }

    [Fact]
    public void RecordSectionVerdict_LayoutMismatch_ReturnsLayoutMismatch_NotCardNotFound()
    {
        var path = WriteInitialSectionCard("s-0003", "S-0003");

        var outcome = CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.Approve, "a", "b", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), "a-different-change");

        Assert.IsType<CardSectionVerdictOutcome.LayoutMismatch>(outcome);
    }

    [Fact]
    public void RecordSectionVerdict_WhenTheCardFileIsCorrupt_ReturnsCardCorrupt_NotARefusalShapedOutcome()
    {
        var path = Path.Combine(_directory, "corrupt.md");
        File.WriteAllText(path, "not a card file at all");

        var outcome = CardStore.RecordSectionVerdict(
            _root, path, SectionVerdict.Approve, "a", "b", CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName);

        var corrupt = Assert.IsType<CardSectionVerdictOutcome.CardCorrupt>(outcome);
        Assert.Equal(path, corrupt.FilePath);
    }

    [Fact]
    public void RecordSectionVerdict_WhenTheLockIsHeldByAnotherCaller_ReturnsToolFailure_NotARefusalShapedOutcome()
    {
        var path = WriteInitialSectionCard("s-0004", "S-0004");
        var holder = AssertAcquired(CardLock.Acquire(path, TimeSpan.FromSeconds(5)));

        try
        {
            var outcome = CardStore.RecordSectionVerdict(
                _root, path, SectionVerdict.Approve, "a", "b", CardOwner.Supervisor, Created, TimeSpan.FromMilliseconds(200), ChangeName);

            Assert.IsType<CardSectionVerdictOutcome.ToolFailure>(outcome);
        }
        finally
        {
            holder.Dispose();
        }
    }

    /// <summary>

    /// <summary>
    /// The gap the reviewer flagged as missing (§5 block E remediation) — the same hazard block D's
    /// <c>gate_results</c> byte-identical round-trip test closed: a hand-authored card carrying a
    /// verdict line with awkward raw values (an escaped backslash and an escaped space in
    /// <c>range-from</c>, an unrecognised extra field) round-trips byte-identically through
    /// parse → write, asserted on the file's bytes, not the parsed object.
    /// </summary>
    [Fact]
    public void HandAuthoredCard_WithAnAwkwardVerdictLine_RoundTripsByteIdentically()
    {
        const string raw =
            "---\n" +
            "id: S-0301\n" +
            "kind: section\n" +
            "title: Byte-identical verdict\n" +
            "status: open\n" +
            "owner: architect\n" +
            "scope: change\n" +
            "section: \n" +
            "created: 2026-08-22T09:00:00.0000000+00:00\n" +
            "updated: 2026-08-22T09:00:00.0000000+00:00\n" +
            "base: e055e5b\n" +
            "---\n" +
            "Body text.\n" +
            "<!-- callboard:verdict by=supervisor verdict=request-changes range-from=odd\\\\path\\swith\\sspaces range-to=a52cd7a timestamp=2026-08-22T10:00:00.0000000+00:00 future-field=kept -->\n";

        var parsed = AssertParseSuccess(CardFileParser.Parse(raw));

        var only = Assert.Single(parsed.SectionFields.Verdicts);
        Assert.Equal(CardOwner.Supervisor, only.By);
        Assert.Equal(SectionVerdict.RequestChanges, only.Verdict);
        Assert.Equal("odd\\path with spaces", only.RangeFrom);
        Assert.Equal("a52cd7a", only.RangeTo);
        Assert.Equal(("future-field", "kept"), Assert.Single(only.UnknownFields));

        var reserialized = CardFileWriter.Serialize(parsed);

        Assert.Equal(Encoding.UTF8.GetBytes(raw), Encoding.UTF8.GetBytes(reserialized));
    }

    private string WriteInitialSectionCard(string fileStem, string id)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "Title", "open", CardOwner.Architect, CardScope.Change, "5", Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static CardSectionVerdictOutcome.Recorded AssertRecorded(CardSectionVerdictOutcome outcome) =>
        outcome.Match(
            onRecorded: static recorded => recorded,
            onNotASectionCard: static n => throw new Xunit.Sdk.XunitException($"expected Recorded, got NotASectionCard({n.Kind.ToWireString()})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Recorded, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Recorded, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Recorded, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Recorded, got ToolFailure: {toolFailure.Reason}"));

    private static CardLock AssertAcquired(CardLockResult result) =>
        result.Match(
            onAcquired: static acquired => acquired.Lock,
            onTimedOut: static timedOut => throw new Xunit.Sdk.XunitException($"expected to acquire the lock, timed out: {timedOut.Message}"));

    private static void AssertWriteSuccess(CardWriteResult result) =>
        result.Match<object?>(
            onSuccess: static _ => null,
            onNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected write success, got NotFound: '{notFound.FilePath}'"),
            onAlreadyExists: alreadyExists => throw new Xunit.Sdk.XunitException($"expected write success, got AlreadyExists: '{alreadyExists.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected write success, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected write success, got Corrupt: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected write success, got ToolFailure: {toolFailure.Reason}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
