using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §6 remediation, round 3 (reviewer blocker) — <see cref="FindingDegradationEvaluatorTests"/> and
/// <see cref="CommandDispatcherFindingStatusTests"/> each mutate the process-global current
/// directory (<see cref="Directory.SetCurrentDirectory"/>) to prove the empty-directory-component
/// fix, because the underlying reason they must is that <c>CommandDispatcher</c>'s
/// <c>workingDirectory</c> parameter does not govern relative-path resolution for a command's own
/// file argument — only the real process CWD does (a §7 concern, not touched here). Without an
/// explicit shared collection, xUnit's default one-collection-per-class parallelism lets these two
/// classes run concurrently against that same global state; the reviewer reproduced
/// <see cref="Directory.GetCurrentDirectory"/> itself throwing when a concurrent test's CWD change
/// raced it. This collection serializes the two classes against each other — remove it only if the
/// tests stop mutating the current directory, not because it looks like unused ceremony.
/// </summary>
[CollectionDefinition(Name)]
public sealed class CurrentDirectoryMutatingTests
{
    internal const string Name = "current-directory-mutating-tests";
}

/// <summary>
/// §6 block D — finding degradation at section close, at the domain layer:
/// <see cref="FindingDegradationEvaluator"/>. CLI-level coverage (the emitted JSON field, and its
/// independence from staleness) lives in <c>CommandDispatcherFindingStatusTests</c>; this file
/// proves the evaluator itself and — the block's structural done-gate — that closing a section
/// never writes or rewrites a finding card, nor any other card in the same directory that was not
/// itself the section card.
///
/// <para>
/// <b>§6 block D remediation (reviewer blocker).</b> The reviewer reproduced, against the
/// unmodified evaluator, that two <c>section</c> cards sharing one <c>Section</c> label made the
/// degradation answer depend on which file <see cref="CardStore.ReadAllCards"/> happened to
/// enumerate first — <see cref="TwoSectionCardsShareTheFindingsLabel_RefusesRatherThanPickingOne"/>
/// is that exact fixture, now asserted to refuse. The reviewer's second fixture — a corrupt card
/// silently reading identically to "no section card exists" — is
/// <see cref="UnparseableSectionCandidate_ReadsUnreadable_NotLive"/>.
/// </para>
///
/// <para>
/// <b>§6 section remediation (supervisor blocker B3).</b> Zero matches among readable candidates is
/// not the same as zero candidates: <see cref="ClosedSectionCardForADifferentSection_ReadsUnreadable_NotLive"/>
/// replaces the fixture that previously pinned the fail-open direction (a differently-labelled
/// section card made the finding read <c>Live</c> forever). <see
/// cref="NoMatchingSectionCardInDirectory_ReadsLive_NotDegraded"/> keeps proving the genuine
/// zero-candidates case still reads <c>Live</c> — that one is honest.
/// </para>
/// </summary>
[Collection(CurrentDirectoryMutatingTests.Name)]
public sealed class FindingDegradationEvaluatorTests : IDisposable
{
    private static readonly DateTimeOffset Recorded = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";
    private const string Section = "6";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-finding-degradation-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;

    public FindingDegradationEvaluatorTests()
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

    [Fact]
    public void OpenSection_FindingReadsLive()
    {
        var findingPath = WriteFinding("f-0001");
        WriteSectionCard("s-0001", "S-0001", closed: false);

        var card = AssertParseSuccess(CardStore.ReadCard(findingPath));
        Assert.Same(FindingDegradationStatus.Live, AssertResolved(FindingDegradationEvaluator.Evaluate(card, findingPath)));
    }

    [Fact]
    public void ClosedSection_FindingReadsDegraded()
    {
        var findingPath = WriteFinding("f-0002");
        WriteSectionCard("s-0002", "S-0002", closed: true);

        var card = AssertParseSuccess(CardStore.ReadCard(findingPath));
        Assert.Same(FindingDegradationStatus.Degraded, AssertResolved(FindingDegradationEvaluator.Evaluate(card, findingPath)));
    }

    // No section card exists in the finding's own directory at all: the record cannot prove the
    // section closed, so this reads Live rather than guessing Degraded — see
    // FindingDegradationEvaluator's own doc comment.
    [Fact]
    public void NoMatchingSectionCardInDirectory_ReadsLive_NotDegraded()
    {
        var findingPath = WriteFinding("f-0003");

        var card = AssertParseSuccess(CardStore.ReadCard(findingPath));
        Assert.Same(FindingDegradationStatus.Live, AssertResolved(FindingDegradationEvaluator.Evaluate(card, findingPath)));
    }

    // §6 remediation (B3). A closed section card for a *different* section number must not degrade
    // this finding as if it were a match — but neither can it be reported Live, because
    // `--section` is unvalidated free text with no section-creation verb: this evaluator has no way
    // to tell "genuinely a different section" apart from "the finding's own section, mislabelled".
    // Before the fix this read Live — the exact fail-open direction the supervisor named, pinned by
    // this test's own previous assertion.
    [Fact]
    public void ClosedSectionCardForADifferentSection_ReadsUnreadable_NotLive()
    {
        var findingPath = WriteFinding("f-0004");
        var otherSectionPath = WriteSectionCard("s-0003", "S-0003", closed: true, section: "5");

        var card = AssertParseSuccess(CardStore.ReadCard(findingPath));
        var evaluation = FindingDegradationEvaluator.Evaluate(card, findingPath);

        var status = evaluation.Match(
            onResolved: static status => status,
            onAmbiguous: static (label, filePaths) => throw new Xunit.Sdk.XunitException($"expected Resolved(Unreadable), got Ambiguous('{label}')."));

        var reason = status.Match(
            onLive: static () => throw new Xunit.Sdk.XunitException("expected Unreadable, got Live."),
            onDegraded: static () => throw new Xunit.Sdk.XunitException("expected Unreadable, got Degraded."),
            onUnreadable: static reason => reason);
        Assert.Contains(otherSectionPath, reason, StringComparison.Ordinal);
    }

    // The structural done-gate: closing the section must not write or rewrite the finding card.
    // Demonstrated two ways — byte-identical content and an unchanged mtime — so a write-then-
    // write-back-the-same-bytes path would still be caught.
    [Fact]
    public void ClosingTheSection_NeverWritesOrRewritesTheFindingCard()
    {
        var findingPath = WriteFinding("f-0005");
        var sectionPath = WriteSectionCard("s-0004", "S-0004", closed: false);

        var bytesBefore = File.ReadAllText(findingPath);
        var mtimeBefore = File.GetLastWriteTimeUtc(findingPath);

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Recorded.AddDays(1), TimeSpan.FromSeconds(5), ChangeName);
        Assert.IsType<CardSectionCloseOutcome.Closed>(outcome);

        Assert.Equal(bytesBefore, File.ReadAllText(findingPath));
        Assert.Equal(mtimeBefore, File.GetLastWriteTimeUtc(findingPath));
    }

    // findings' blind-spot obligation is not the only kind of obligation a section can carry —
    // this proves a hand-authored one, never touched by RecordFinding at all, is left exactly as
    // untouched by section close as the blind-spot-raised kind CardFindingRecordTests already
    // covers: CloseSection reaches only the section card's own file, regardless of which verb
    // wrote the obligation sitting beside it.
    [Fact]
    public void HandWrittenObligation_InTheSameSection_IsUntouchedBySectionClose_SameAsARaisedOne()
    {
        var obligationPath = Path.Combine(_directory, "o-0001.md");
        var obligationFrontmatter = new CardFrontmatter(
            "O-0001", CardKind.Obligation, "Hand-written obligation", "open", CardOwner.Worker, CardScope.Change, Section, Recorded, Recorded);
        var obligationCard = new CardFile(obligationFrontmatter, "Owed by someone, someday.", [], []);
        File.WriteAllText(obligationPath, CardFileWriter.Serialize(obligationCard), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var sectionPath = WriteSectionCard("s-0005", "S-0005", closed: false);
        var bytesBefore = File.ReadAllText(obligationPath);

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Recorded.AddDays(1), TimeSpan.FromSeconds(5), ChangeName);
        Assert.IsType<CardSectionCloseOutcome.Closed>(outcome);

        Assert.Equal(bytesBefore, File.ReadAllText(obligationPath));
    }

    // "Remains retrievable" (findings: "the finding is no longer offered as live and remains
    // retrievable by identity") means more than "the file is still on disk" — a degraded finding
    // must still parse cleanly and still round-trip through the writer byte-for-byte.
    [Fact]
    public void DegradedFinding_StillParsesAndRoundTrips()
    {
        var findingPath = WriteFinding("f-0006");
        WriteSectionCard("s-0006", "S-0006", closed: true);

        var card = AssertParseSuccess(CardStore.ReadCard(findingPath));
        Assert.Same(FindingDegradationStatus.Degraded, AssertResolved(FindingDegradationEvaluator.Evaluate(card, findingPath)));

        var reserialized = CardFileWriter.Serialize(card);
        Assert.Equal(File.ReadAllText(findingPath), reserialized);
        AssertParseSuccess(CardFileParser.Parse(reserialized));
    }

    // §6 block D remediation (reviewer blocker 1) — the fixture the reviewer built against the
    // unmodified evaluator: two `section` cards, both carrying the finding's label, one closed and
    // one open. Before this fix the answer flipped on which filename sorted first ordinally; now
    // it refuses instead, and names both conflicting file paths.
    [Fact]
    public void TwoSectionCardsShareTheFindingsLabel_RefusesRatherThanPickingOne()
    {
        var findingPath = WriteFinding("f-0007");
        var openPath = WriteSectionCard("s-a-open", "S-0100", closed: false);
        var closedPath = WriteSectionCard("s-b-closed", "S-0101", closed: true);

        var card = AssertParseSuccess(CardStore.ReadCard(findingPath));
        var evaluation = FindingDegradationEvaluator.Evaluate(card, findingPath);

        evaluation.Match<object?>(
            onResolved: status => throw new Xunit.Sdk.XunitException($"expected Ambiguous, got a resolved status."),
            onAmbiguous: (label, filePaths) =>
            {
                Assert.Equal(Section, label);
                Assert.Contains(openPath, filePaths);
                Assert.Contains(closedPath, filePaths);
                Assert.Equal(2, filePaths.Count);
                return null;
            });
    }

    // §6 block D remediation (reviewer blocker 2) — a card in the finding's directory that fails
    // to parse at all, with no other card matching the finding's label. Reads Unreadable, not
    // Live: the corrupt file cannot be ruled out as the finding's own section card, and silently
    // treating it as "no section card exists" would hide that uncertainty from the caller.
    [Fact]
    public void UnparseableSectionCandidate_ReadsUnreadable_NotLive()
    {
        var findingPath = WriteFinding("f-0008");
        var garbagePath = Path.Combine(_directory, "s-broken.md");
        File.WriteAllText(garbagePath, "not a card at all, no frontmatter fence", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var card = AssertParseSuccess(CardStore.ReadCard(findingPath));
        var evaluation = FindingDegradationEvaluator.Evaluate(card, findingPath);

        var status = evaluation.Match(
            onResolved: static status => status,
            onAmbiguous: (label, filePaths) => throw new Xunit.Sdk.XunitException($"expected Resolved(Unreadable), got Ambiguous('{label}')."));

        var reason = status.Match(
            onLive: static () => throw new Xunit.Sdk.XunitException("expected Unreadable, got Live."),
            onDegraded: static () => throw new Xunit.Sdk.XunitException("expected Unreadable, got Degraded."),
            onUnreadable: static reason => reason);
        Assert.Contains(garbagePath, reason, StringComparison.Ordinal);
    }

    // A definitive match still wins even when an unrelated, unparseable file sits in the same
    // directory — unreadable-ness only takes over when there is no confident answer otherwise.
    [Fact]
    public void DefiniteMatch_TakesPrecedenceOverAnUnrelatedUnreadableCard()
    {
        var findingPath = WriteFinding("f-0009");
        WriteSectionCard("s-0007", "S-0102", closed: true);
        var garbagePath = Path.Combine(_directory, "s-broken-2.md");
        File.WriteAllText(garbagePath, "garbage", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var card = AssertParseSuccess(CardStore.ReadCard(findingPath));
        Assert.Same(FindingDegradationStatus.Degraded, AssertResolved(FindingDegradationEvaluator.Evaluate(card, findingPath)));
    }

    // §6 section remediation, round 2 (supervisor blocker) — Path.GetDirectoryName returns "" for
    // a bare filename with no directory component, and the evaluator used to treat that as "no
    // directory to look in" and answer Live without reading a single card. This is the one domain
    // fixture that constructs a non-absolute findingFilePath rather than one built through
    // Path.Combine(_directory, ...) — every other fixture in this file would pass even with the
    // bug present, which is exactly why the CLI-level test in
    // CommandDispatcherFindingStatusTests is the load-bearing one.
    [Fact]
    public void BareFilenameWithNoDirectoryComponent_StillResolvesToTheCurrentDirectory()
    {
        var findingPath = WriteFinding("f-0010");
        WriteSectionCard("s-0008", "S-0103", closed: true);
        var card = AssertParseSuccess(CardStore.ReadCard(findingPath));

        var previousDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(_directory);
            var evaluation = FindingDegradationEvaluator.Evaluate(card, "f-0010.md");
            Assert.Same(FindingDegradationStatus.Degraded, AssertResolved(evaluation));
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }

    private string WriteFinding(string fileStem)
    {
        var findingPath = Path.Combine(_directory, fileStem + ".md");
        var outcome = CardStore.RecordFinding(
            _root, findingPath, "A finding", CardOwner.Worker, Section, "Body of the finding.",
            instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest: null,
            FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(5), ChangeName);
        AssertRecorded(outcome);
        return findingPath;
    }

    private string WriteSectionCard(string fileStem, string id, bool closed, string? section = null)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "Title", closed ? "closed" : "open", CardOwner.Architect, CardScope.Change,
            section ?? Section, Recorded, Recorded);
        var sectionFields = closed
            ? new SectionCardFields(null, CardOwner.Architect, Recorded, [])
            : SectionCardFields.Empty;
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], sectionFields);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static FindingDegradationStatus AssertResolved(FindingDegradationEvaluation evaluation) =>
        evaluation.Match(
            onResolved: static status => status,
            onAmbiguous: static (label, filePaths) =>
                throw new Xunit.Sdk.XunitException($"expected Resolved, got Ambiguous('{label}', [{string.Join(", ", filePaths)}])."));

    private static CardFindingRecordOutcome.Recorded AssertRecorded(CardFindingRecordOutcome outcome) =>
        outcome.Match(
            onRecorded: static recorded => recorded,
            onFindingAlreadyExists: static already => throw new Xunit.Sdk.XunitException($"expected Recorded, got FindingAlreadyExists('{already.FilePath}')"),
            onBlindSpotCardAlreadyExists: static already => throw new Xunit.Sdk.XunitException($"expected Recorded, got BlindSpotCardAlreadyExists('{already.FilePath}')"),
            onFindingLayoutMismatch: static mismatch => throw new Xunit.Sdk.XunitException($"expected Recorded, got FindingLayoutMismatch: {mismatch.Reason}"),
            onBlindSpotLayoutMismatch: static mismatch => throw new Xunit.Sdk.XunitException($"expected Recorded, got BlindSpotLayoutMismatch: {mismatch.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Recorded, got ToolFailure: {toolFailure.Reason}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
