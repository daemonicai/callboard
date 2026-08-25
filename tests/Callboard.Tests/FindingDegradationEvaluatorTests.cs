using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §6 block D — finding degradation at section close, at the domain layer:
/// <see cref="FindingDegradationEvaluator"/>. CLI-level coverage (the emitted JSON field, and its
/// independence from staleness) lives in <c>CommandDispatcherFindingStatusTests</c>; this file
/// proves the evaluator itself and — the block's structural done-gate — that closing a section
/// never writes or rewrites a finding card, nor any other card in the same directory that was not
/// itself the section card.
///
/// <para>
/// <b>§7 block B rewire.</b> A finding's own <see cref="CardFrontmatter.Section"/> is now the
/// section card's <c>id</c> (Product Owner ruling: "identity is the reference, and identity
/// resolves"), resolved by <see cref="CardIdentityResolver"/> across the whole record — not a
/// free-text label matched by scanning the finding's own directory. Every fixture below that used
/// to depend on directory-local, label-based matching is gone or reshaped around exact id
/// resolution: <see cref="TwoCardFilesClaimTheSameSectionId_RefusesRatherThanPickingOne"/> replaces
/// the old same-label fixture with a genuine duplicate-id collision (only reachable by a
/// hand-edited file — nothing through the allocator can produce one), and
/// <see cref="SectionIdDoesNotResolve_ButAnUnrelatedFileCouldNotBeRead_ReadsUnreadable_NotLive"/>
/// replaces the old "differently-labelled section card" fixture, since two distinct ids are simply
/// two distinct answers now — there is no "typo of a label" ambiguity left to guard against. The
/// old bare-filename CWD-dependence fixture is gone entirely: the evaluator no longer takes a file
/// path to derive a directory from at all, only <c>cardsRoot</c>, so that bug class cannot recur —
/// see <see cref="FindingDegradationEvaluator.Evaluate"/>'s own signature.
/// </para>
/// </summary>
public sealed class FindingDegradationEvaluatorTests : IDisposable
{
    private static readonly DateTimeOffset Recorded = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-finding-degradation-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;
    private readonly string _registerDirectory;

    public FindingDegradationEvaluatorTests()
    {
        _directory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        _registerDirectory = Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar));
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
        var sectionPath = WriteSectionCard("s-0001", "S-0001", closed: false);
        var findingPath = WriteFinding("f-0001", "S-0001");
        _ = sectionPath;

        var card = AssertParseSuccess(CardStore.ReadCard(findingPath));
        Assert.Same(FindingDegradationStatus.Live, AssertResolved(FindingDegradationEvaluator.Evaluate(card, _root)));
    }

    [Fact]
    public void ClosedSection_FindingReadsDegraded()
    {
        WriteSectionCard("s-0002", "S-0002", closed: true);
        var findingPath = WriteFinding("f-0002", "S-0002");

        var card = AssertParseSuccess(CardStore.ReadCard(findingPath));
        Assert.Same(FindingDegradationStatus.Degraded, AssertResolved(FindingDegradationEvaluator.Evaluate(card, _root)));
    }

    // No card anywhere in the record carries the id this finding's own 'section' field names: the
    // resolver has exhaustively searched the whole record and confirmed absence, so this reads
    // Live rather than guessing Degraded — see FindingDegradationEvaluator's own doc comment.
    [Fact]
    public void SectionIdDoesNotResolveToAnyCard_ReadsLive_NotDegraded()
    {
        var findingPath = WriteFinding("f-0003", "S-9999");

        var card = AssertParseSuccess(CardStore.ReadCard(findingPath));
        Assert.Same(FindingDegradationStatus.Live, AssertResolved(FindingDegradationEvaluator.Evaluate(card, _root)));
    }

    // The id resolves, but the resolver's own record-wide walk also found a card elsewhere that
    // could not be read — B3's "zero matches is not zero candidates" lesson, now the resolver's
    // job rather than the evaluator's. Confirmed absence is not available, so this reads
    // Unreadable, never Live.
    [Fact]
    public void SectionIdDoesNotResolve_ButAnUnrelatedFileCouldNotBeRead_ReadsUnreadable_NotLive()
    {
        var findingPath = WriteFinding("f-0004", "S-9999");

        Directory.CreateDirectory(_registerDirectory);
        var garbagePath = Path.Combine(_registerDirectory, "r-broken.md");
        File.WriteAllText(garbagePath, "not a card at all, no frontmatter fence", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var card = AssertParseSuccess(CardStore.ReadCard(findingPath));
        var evaluation = FindingDegradationEvaluator.Evaluate(card, _root);

        var status = evaluation.Match(
            onResolved: static status => status,
            onAmbiguous: static (id, filePaths) => throw new Xunit.Sdk.XunitException($"expected Resolved(Unreadable), got Ambiguous('{id}')."));

        var reason = status.Match(
            onLive: static () => throw new Xunit.Sdk.XunitException("expected Unreadable, got Live."),
            onDegraded: static () => throw new Xunit.Sdk.XunitException("expected Unreadable, got Degraded."),
            onUnreadable: static reason => reason);
        Assert.Contains(garbagePath, reason, StringComparison.Ordinal);
    }

    // The structural done-gate: closing the section must not write or rewrite the finding card.
    // Demonstrated two ways — byte-identical content and an unchanged mtime — so a write-then-
    // write-back-the-same-bytes path would still be caught.
    [Fact]
    public void ClosingTheSection_NeverWritesOrRewritesTheFindingCard()
    {
        var sectionPath = WriteSectionCard("s-0004", "S-0004", closed: false);
        var findingPath = WriteFinding("f-0005", "S-0004");

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
        var sectionPath = WriteSectionCard("s-0005", "S-0005", closed: false);

        var obligationPath = Path.Combine(_directory, "o-0001.md");
        var obligationFrontmatter = new CardFrontmatter(
            "O-0001", CardKind.Obligation, "Hand-written obligation", "open", CardOwner.Worker, CardScope.Change, "S-0005", Recorded, Recorded);
        var obligationCard = new CardFile(obligationFrontmatter, "Owed by someone, someday.", [], []);
        File.WriteAllText(obligationPath, CardFileWriter.Serialize(obligationCard), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

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
        WriteSectionCard("s-0006", "S-0006", closed: true);
        var findingPath = WriteFinding("f-0006", "S-0006");

        var card = AssertParseSuccess(CardStore.ReadCard(findingPath));
        Assert.Same(FindingDegradationStatus.Degraded, AssertResolved(FindingDegradationEvaluator.Evaluate(card, _root)));

        var reserialized = CardFileWriter.Serialize(card);
        Assert.Equal(File.ReadAllText(findingPath), reserialized);
        AssertParseSuccess(CardFileParser.Parse(reserialized));
    }

    // §7 block B — the reshaped duplicate fixture: two files claim the same id, only reachable by
    // hand-editing (CardIdentityAllocator never issues a repeat). Before this rewire the equivalent
    // defect was two `section` cards sharing one free-text label; the fail-closed shape is
    // identical, the underlying mechanism is not.
    [Fact]
    public void TwoCardFilesClaimTheSameSectionId_RefusesRatherThanPickingOne()
    {
        var openPath = WriteSectionCard("s-a-open", "S-0100", closed: false);
        var closedPath = WriteSectionCard("s-b-closed", "S-0100", closed: true);
        var findingPath = WriteFinding("f-0007", "S-0100");

        var card = AssertParseSuccess(CardStore.ReadCard(findingPath));
        var evaluation = FindingDegradationEvaluator.Evaluate(card, _root);

        evaluation.Match<object?>(
            onResolved: static _ => throw new Xunit.Sdk.XunitException("expected Ambiguous, got a resolved status."),
            onAmbiguous: (id, filePaths) =>
            {
                Assert.Equal("S-0100", id);
                Assert.Contains(openPath, filePaths);
                Assert.Contains(closedPath, filePaths);
                Assert.Equal(2, filePaths.Count);
                return null;
            });
    }

    // A definite match still wins even when an unrelated, unparseable file sits elsewhere in the
    // record — unreadable-ness only takes over when there is no confident answer otherwise (the
    // resolver's own "zero matches" gate, not reached once one match is found).
    [Fact]
    public void DefiniteMatch_TakesPrecedenceOverAnUnrelatedUnreadableCard()
    {
        WriteSectionCard("s-0007", "S-0102", closed: true);
        var findingPath = WriteFinding("f-0009", "S-0102");

        var garbagePath = Path.Combine(_directory, "s-broken-2.md");
        File.WriteAllText(garbagePath, "garbage", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var card = AssertParseSuccess(CardStore.ReadCard(findingPath));
        Assert.Same(FindingDegradationStatus.Degraded, AssertResolved(FindingDegradationEvaluator.Evaluate(card, _root)));
    }

    // The id resolves, but to a card that is not a `section` card at all — degradation cannot be
    // confirmed, so this reads Unreadable and names the actual kind found.
    [Fact]
    public void SectionIdResolvesToANonSectionCard_ReadsUnreadable_NotLive()
    {
        var ruleFrontmatter = new CardFrontmatter(
            "R-0001", CardKind.Rule, "A rule", "open", CardOwner.Architect, CardScope.Repository, string.Empty, Recorded, Recorded);
        var ruleCard = new CardFile(ruleFrontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty);
        Directory.CreateDirectory(_registerDirectory);
        File.WriteAllText(
            Path.Combine(_registerDirectory, "r-0001.md"), CardFileWriter.Serialize(ruleCard), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var findingPath = WriteFinding("f-0010", "R-0001");
        var card = AssertParseSuccess(CardStore.ReadCard(findingPath));
        var evaluation = FindingDegradationEvaluator.Evaluate(card, _root);

        var status = evaluation.Match(
            onResolved: static status => status,
            onAmbiguous: static (id, filePaths) => throw new Xunit.Sdk.XunitException($"expected Resolved(Unreadable), got Ambiguous('{id}')."));

        var reason = status.Match(
            onLive: static () => throw new Xunit.Sdk.XunitException("expected Unreadable, got Live."),
            onDegraded: static () => throw new Xunit.Sdk.XunitException("expected Unreadable, got Degraded."),
            onUnreadable: static reason => reason);
        Assert.Contains("rule", reason, StringComparison.Ordinal);
    }

    private string WriteFinding(string fileStem, string sectionId)
    {
        var findingPath = Path.Combine(_directory, fileStem + ".md");
        var outcome = CardStore.RecordFinding(
            _root, findingPath, "A finding", CardOwner.Worker, sectionId, "Body of the finding.",
            instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest: null,
            FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(5), ChangeName);
        AssertRecorded(outcome);
        return findingPath;
    }

    private string WriteSectionCard(string fileStem, string id, bool closed)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "Title", closed ? "closed" : "open", CardOwner.Architect, CardScope.Change,
            string.Empty, Recorded, Recorded);
        var sectionFields = closed
            ? new SectionCardFields(null, CardOwner.Architect, Recorded, [], [])
            : SectionCardFields.Empty;
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], sectionFields);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static FindingDegradationStatus AssertResolved(FindingDegradationEvaluation evaluation) =>
        evaluation.Match(
            onResolved: static status => status,
            onAmbiguous: static (id, filePaths) =>
                throw new Xunit.Sdk.XunitException($"expected Resolved, got Ambiguous('{id}', [{string.Join(", ", filePaths)}])."));

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
