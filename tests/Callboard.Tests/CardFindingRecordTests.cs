using System.Reflection;
using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §6 block B, at the domain layer: <see cref="CardStore.RecordFinding"/> — the "two cards, two
/// locks in ordinal order, all-or-nothing" write (§6 block B remediation: the brief's original "one
/// CardLock" claim was wrong — see <see cref="CardStore.RecordFinding"/>'s own doc comment). CLI-
/// level coverage (6.2's refusal message, the flag surface) lives in
/// <c>CommandDispatcherFindingRecordTests</c>; this file proves the write itself: both-cards-or-
/// neither, the mutual reference, the cold-directory case, and — 6.3's "does not degrade" — what
/// <c>section close</c> does and does not touch today.
///
/// <para>
/// <b>Neither <c>_directory</c> nor <c>_registerDirectory</c> is pre-created (§6 block B
/// remediation, reviewer blocker 3).</b> The first version of this file created both in its
/// constructor, which is exactly why the cold-directory defect this remediation fixes was never
/// caught: every test unconditionally ran against a directory that already existed, so nothing ever
/// exercised the verb's primary use case — the first finding a section raises, the first obligation
/// a change raises, where the directory does not exist yet. A test that needs a pre-existing file at
/// a path (to force an <c>AlreadyExists</c> outcome) creates that path's directory itself, locally,
/// rather than relying on shared setup.
/// </para>
/// </summary>
public sealed class CardFindingRecordTests : IDisposable
{
    private static readonly DateTimeOffset Recorded = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";
    private const string Section = "6";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-finding-record-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;
    private readonly string _registerDirectory;

    public CardFindingRecordTests()
    {
        // Deliberately not created here — see this type's own doc comment.
        _directory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        _registerDirectory = Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void BlindSpotNone_WritesOnlyTheFinding_NoSecondCardAnywhereInTheDirectory()
    {
        var outcome = CardStore.RecordFinding(
            _root, "Everything checked clean", CardOwner.Worker, Section, "Body of the finding.",
            instrument: "make gates", FindingExtent.BlockScope, verifiedAt: "abc123",
            raiseRequest: null, FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(5), ChangeName);

        var recorded = AssertRecorded(outcome);
        Assert.Null(recorded.RaisedCard);
        Assert.Null(recorded.RaisedCardFilePath);
        Assert.Equal("none", recorded.Finding.FindingFields.BlindSpot.Match(onNone: static () => "none", onRaisedAs: static _ => "raised-as"));

        // 14.5-remediation: the written file's basename is the minted id — the property no door
        // that mints a card had ever asserted before this remediation (§14 supervisor finding).
        Assert.Equal(CardLayout.FileNameFor(recorded.Finding.Frontmatter.Id), Path.GetFileName(recorded.FindingFilePath));

        // No card besides the finding itself exists anywhere the raised card could have landed —
        // proves "none" really did skip the second write, not merely that the outcome says so.
        var filesInDirectory = Directory.GetFiles(_directory, "*.md");
        Assert.Single(filesInDirectory);
        Assert.Equal(recorded.FindingFilePath, filesInDirectory[0]);

        var read = AssertParseSuccess(CardStore.ReadCard(recorded.FindingFilePath));
        Assert.Equal("make gates", read.FindingFields.Instrument);
        Assert.Equal("abc123", read.FindingFields.VerifiedAt);
    }

    [Theory]
    [InlineData("obligation")]
    [InlineData("hazard")]
    public void BlindSpotRaised_WritesBothCards_EachReferencingTheOther(string kindText)
    {
        var raisedKind = kindText == "obligation" ? CardKind.Obligation : CardKind.Hazard;
        var raiseRequest = new FindingBlindSpotRaiseRequest(raisedKind, "Blind spot title", "Blind spot content.");

        var outcome = CardStore.RecordFinding(
            _root, "Checked, with a gap", CardOwner.Worker, Section, "Body of the finding.",
            instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest, FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(5), ChangeName);

        var recorded = AssertRecorded(outcome);
        Assert.NotNull(recorded.RaisedCard);
        Assert.NotNull(recorded.RaisedCardFilePath);
        var raisedId = recorded.RaisedCard!.Frontmatter.Id;

        // 14.5-remediation: both minted files' basenames are their own ids.
        Assert.Equal(CardLayout.FileNameFor(recorded.Finding.Frontmatter.Id), Path.GetFileName(recorded.FindingFilePath));
        Assert.Equal(CardLayout.FileNameFor(raisedId), Path.GetFileName(recorded.RaisedCardFilePath));

        // The finding's own reference: BlindSpot.RaisedAs names the raised card's id.
        var declaredId = recorded.Finding.FindingFields.BlindSpot.Match(
            onNone: static () => (string?)null,
            onRaisedAs: id => id);
        Assert.Equal(raisedId, declaredId);

        // The raised card's own reference: its body names the finding's id.
        Assert.Contains(recorded.Finding.Frontmatter.Id, recorded.RaisedCard.Body, StringComparison.Ordinal);
        Assert.Contains("Blind spot content.", recorded.RaisedCard.Body, StringComparison.Ordinal);

        Assert.Equal(raisedKind, recorded.RaisedCard.Frontmatter.Kind);
        Assert.Equal(raisedKind == CardKind.Obligation ? CardScope.Change : CardScope.Repository, recorded.RaisedCard.Frontmatter.Scope);

        // §7 block C: a raised obligation gets a real owed_by, set from the finding's own section —
        // "give that obligation a real owed_by like any other" (Architect ruling). A raised hazard
        // carries none; register gives owed_by to an obligation only.
        Assert.Equal(raisedKind == CardKind.Obligation ? Section : null, recorded.RaisedCard.RegisterFields.OwedBy);

        // Both files actually landed on disk, independently readable.
        var findingRead = AssertParseSuccess(CardStore.ReadCard(recorded.FindingFilePath));
        var raisedRead = AssertParseSuccess(CardStore.ReadCard(recorded.RaisedCardFilePath!));
        Assert.Equal(raisedId, findingRead.FindingFields.BlindSpot.Match(onNone: static () => (string?)null, onRaisedAs: id => id));
        Assert.Equal(raisedKind, raisedRead.Frontmatter.Kind);
        Assert.Equal(raisedKind == CardKind.Obligation ? Section : null, raisedRead.RegisterFields.OwedBy);
    }

    // §6 block B remediation, reviewer blocker 3, named explicitly: the verb's primary use case is
    // the first finding a section ever raises (or the first obligation a change ever raises) —
    // where the target directory does not exist yet. What would have to break for this to go red:
    // RecordFinding acquiring a CardLock before creating the finding's own directory, which spins
    // for the full lock timeout and reports a misleading tool-failure instead of succeeding.
    [Fact]
    public void RecordingTheFirstFindingInASection_AgainstDirectoriesThatDoNotYetExist_Succeeds()
    {
        Assert.False(Directory.Exists(_directory), "test setup precondition: the change directory must not exist yet.");
        Assert.False(Directory.Exists(_registerDirectory), "test setup precondition: the register directory must not exist yet.");

        var raiseRequest = new FindingBlindSpotRaiseRequest(CardKind.Hazard, "Blind spot title", "Blind spot content.");

        var outcome = CardStore.RecordFinding(
            _root, "Everything checked clean", CardOwner.Worker, Section, "Body of the finding.",
            instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest, FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(5), ChangeName);

        var recorded = AssertRecorded(outcome);
        Assert.True(File.Exists(recorded.FindingFilePath));
        Assert.True(File.Exists(recorded.RaisedCardFilePath));
    }

    // The partial-failure case, demonstrated by execution (§6 block B brief): pre-occupy the
    // finding's own path so its write — which happens second, after the raised card's — fails.
    // What would have to break for this to go red: RecordFindingUnderLock skipping its rollback
    // call on the finding's AlreadyExists path.
    [Fact]
    public void FindingWriteFailsAfterRaisedCardAlreadyWritten_RaisedCardIsRolledBack()
    {
        // 14.5-remediation: the finding's target path is no longer a caller's to choose — it is
        // CardLayout.FileNameFor("F-0001"), the first identity a fresh counter in this test's own
        // _root ever mints. Pre-occupying it means writing there directly, under an unrelated,
        // readable card (not garbage text — §13: the identity allocator confirms, against the
        // whole record, that the id it is about to issue is not already borne; an unparseable file
        // anywhere in the record reports Unreadable rather than "confirmed unclaimed", failing the
        // allocation outright and masking the AlreadyExists case this test targets under a
        // ToolFailure instead). Its own id ("F-9999") deliberately does not collide with "F-0001" —
        // this fixture is about the finding's own *path* colliding, not its id. The directory has
        // to exist for this stray write itself (unrelated to what RecordFinding does for its own
        // writes), so it is created here, locally, not in shared setup.
        Directory.CreateDirectory(_directory);
        var findingPath = Path.Combine(_directory, CardLayout.FileNameFor("F-0001"));
        var unrelatedFrontmatter = new CardFrontmatter(
            "F-9999", CardKind.Finding, "Unrelated", "open", CardOwner.Architect, CardScope.Change, Section, Recorded, Recorded);
        File.WriteAllText(
            findingPath, CardFileWriter.Serialize(new CardFile(unrelatedFrontmatter, "Unrelated.", [], [])),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var raiseRequest = new FindingBlindSpotRaiseRequest(CardKind.Hazard, "Blind spot title", "Blind spot content.");

        var outcome = CardStore.RecordFinding(
            _root, "Checked, with a gap", CardOwner.Worker, Section, "Body of the finding.",
            instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest, FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(5), ChangeName);

        var alreadyExists = Assert.IsType<CardFindingRecordOutcome.FindingAlreadyExists>(outcome);
        Assert.Equal(findingPath, alreadyExists.FilePath);

        // The raised card must not be left behind — this is what "all-or-nothing" means in
        // practice: a write that got as far as landing bytes on disk, then rolled back. Its own
        // path is derived the same way; only one hazard was minted, so it is the only *.md the
        // register directory could contain.
        Assert.False(Directory.Exists(_registerDirectory) && Directory.GetFiles(_registerDirectory, "*.md").Length > 0,
            "the raised card was left behind after the finding's own write failed.");

        // The pre-existing content at the finding's path is untouched — the finding write really
        // did refuse rather than silently succeeding over it.
        Assert.Equal("F-9999", AssertParseSuccess(CardStore.ReadCard(findingPath)).Frontmatter.Id);

        // Nothing else in the directory either — the only two candidate paths are accounted for.
        var filesInDirectory = Directory.GetFiles(_directory, "*.md");
        Assert.Single(filesInDirectory);
    }

    [Fact]
    public void RaisedCardPathAlreadyOccupied_RefusesBeforeWritingTheFinding()
    {
        // 14.5-remediation: same reasoning as the finding's own pre-occupy fixture above, but for
        // the raised card's own target — CardLayout.FileNameFor("H-0001"), the first hazard identity
        // a fresh counter in this test's own _root ever mints, under _registerDirectory (Repository
        // scope).
        Directory.CreateDirectory(_registerDirectory);
        var raisedPath = Path.Combine(_registerDirectory, CardLayout.FileNameFor("H-0001"));

        // A readable, unrelated card — not garbage text; see the sibling fixture above for why
        // (§13). Its own id ("H-9999") deliberately does not collide with the "H-0001" the fresh
        // counter is about to issue.
        var unrelatedFrontmatter = new CardFrontmatter(
            "H-9999", CardKind.Hazard, "Unrelated", "open", CardOwner.Architect, CardScope.Repository, string.Empty, Recorded, Recorded);
        File.WriteAllText(
            raisedPath, CardFileWriter.Serialize(new CardFile(unrelatedFrontmatter, "Unrelated.", [], [])),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var raiseRequest = new FindingBlindSpotRaiseRequest(CardKind.Hazard, "Blind spot title", "Blind spot content.");

        var outcome = CardStore.RecordFinding(
            _root, "Checked, with a gap", CardOwner.Worker, Section, "Body of the finding.",
            instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest, FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(5), ChangeName);

        var alreadyExists = Assert.IsType<CardFindingRecordOutcome.BlindSpotCardAlreadyExists>(outcome);
        Assert.Equal(raisedPath, alreadyExists.FilePath);

        // The finding was never written — the check on the raised card's path runs first. Its own
        // target ("F-0001.md") would land under _directory, which the pre-occupying hazard card
        // never touches (that sits under _registerDirectory instead), so no *.md anywhere in
        // _directory is proof nothing was written there.
        Assert.False(Directory.Exists(_directory) && Directory.GetFiles(_directory, "*.md").Length > 0,
            "the finding was written even though the raised card's own path was already occupied.");
    }

    // 6.3's "does not degrade" — what a test can prove today, and what it cannot yet. Nothing in
    // this codebase currently degrades *anything* at section close (6.7 is what builds finding
    // degradation, since the raised card's fixed scope (Change for an obligation, Repository for
    // a hazard — never Section) means CloseSection, which only ever writes the section card's own
    // file, has no way to reach it at all. That half is unchanged by 6.7 and stays a byte
    // comparison.
    //
    // §6 block D inverts the other half. The original (block B) version of this test asserted the
    // *finding's* bytes were also unchanged after close — true then only because nothing degraded
    // anything yet, so the assertion passed for a reason unrelated to the property it claimed to
    // prove (the same casing-coincidence shape §8's carried caveat warns against, DEVLOG §6). Block
    // D still asserts the finding's bytes are unchanged (derivation never rewrites the finding —
    // that half of the old claim was actually right, just for the wrong reason) and additionally
    // asserts what actually changes: the finding reads Live before close and Degraded after,
    // through the same evaluator `finding status` calls. Break <see
    // cref="FindingDegradationEvaluator.Evaluate"/> (e.g. make it always return
    // <see cref="FindingDegradationStatus.Live"/>) and this test goes red on the post-close
    // assertion — the failure block B's version could never produce.
    [Fact]
    public void ClosingTheSection_LeavesTheRaisedCardUntouchedAndDegradesTheFinding()
    {
        // §7 block B: a finding's own 'section' field is now the section card's id, not a
        // free-text label. This domain-level call bypasses the CLI's own `--section` validation
        // (CommandDispatcher.ValidateSection), so — same as before this rewire — the finding can
        // still be recorded before the section card it names exists on disk; the id is simply
        // chosen up front, ahead of either write.
        const string sectionId = "S-0001";

        var raiseRequest = new FindingBlindSpotRaiseRequest(CardKind.Hazard, "Blind spot title", "Blind spot content.");

        // RecordFinding runs first, against _directory before it exists — proving blocker 3's fix
        // in passing — which is also what makes WriteInitialSectionCard's own plain File.WriteAllText
        // below able to land in the same, now-existing directory.
        var recordOutcome = CardStore.RecordFinding(
            _root, "Checked, with a gap", CardOwner.Worker, sectionId, "Body of the finding.",
            instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest, FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(5), ChangeName);
        var recorded = AssertRecorded(recordOutcome);
        var findingPath = recorded.FindingFilePath;
        var raisedPath = recorded.RaisedCardFilePath!;

        var sectionPath = WriteInitialSectionCard("s-0001", sectionId);

        var raisedBytesBefore = File.ReadAllText(raisedPath);
        var findingBytesBefore = File.ReadAllText(findingPath);

        var findingBeforeClose = AssertParseSuccess(CardStore.ReadCard(findingPath));
        Assert.Same(FindingDegradationStatus.Live, AssertResolved(FindingDegradationEvaluator.Evaluate(findingBeforeClose, _root)));

        var closeOutcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Recorded.AddDays(1), TimeSpan.FromSeconds(5), ChangeName);
        Assert.IsType<CardSectionCloseOutcome.Closed>(closeOutcome);

        // The raised card: byte-identical. This is the part 6.7 must not break.
        Assert.Equal(raisedBytesBefore, File.ReadAllText(raisedPath));

        // The finding: byte-identical too — degradation is derived, never written (6.7's ruling).
        Assert.Equal(findingBytesBefore, File.ReadAllText(findingPath));

        // But it now reads as degraded, through the exact same read-and-evaluate path re-reading
        // the (unchanged) bytes off disk.
        var findingAfterClose = AssertParseSuccess(CardStore.ReadCard(findingPath));
        Assert.Same(FindingDegradationStatus.Degraded, AssertResolved(FindingDegradationEvaluator.Evaluate(findingAfterClose, _root)));
    }

    // §6 block B remediation, reviewer blocker 2, exercised directly against RollbackRaisedCard
    // itself (reflection — the same established pattern CommandDispatcherTests already uses for
    // CommandDispatcher's own private handlers) rather than only through the CardStore.RecordFinding
    // call graph: with blocker 1's locking fix in place, the mismatch this guards against is no
    // longer reachable through that call graph at all (two locked writers can never race the same
    // path), so a test that only ever drove RecordFinding could never exercise the "content
    // mismatch, refuse to delete" branch — this proves that branch directly, the same way CardLock's
    // own compare-then-delete is proven by its own dedicated tests rather than only by
    // higher-level ones that happen to exercise CardLock along the way.
    [Fact]
    public void RollbackRaisedCard_ContentStillMatches_Deletes()
    {
        var directory = Path.Combine(_root, "rollback-match");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "h-0100.md");
        const string content = "exactly what this call wrote";
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        InvokeRollbackRaisedCard(path, content);

        Assert.False(File.Exists(path), "the file was left behind even though its content matched exactly what this call wrote.");
    }

    // The load-bearing half of blocker 2: something else's content sitting at the path — not what
    // this call wrote — must never be deleted. What would have to break for this to go red:
    // RollbackRaisedCard reverting to File.Exists + unconditional File.Delete (delete-by-path).
    [Fact]
    public void RollbackRaisedCard_ContentDoesNotMatch_LeavesTheFileAlone()
    {
        var directory = Path.Combine(_root, "rollback-mismatch");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "h-0101.md");
        const string someoneElsesContent = "content this call never wrote";
        File.WriteAllText(path, someoneElsesContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        InvokeRollbackRaisedCard(path, "what this call actually wrote");

        Assert.True(File.Exists(path), "content this call never wrote was deleted anyway.");
        Assert.Equal(someoneElsesContent, File.ReadAllText(path));
    }

    private static void InvokeRollbackRaisedCard(string raisedFilePath, string raisedContent)
    {
        var method = typeof(CardStore).GetMethod("RollbackRaisedCard", BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, [raisedFilePath, raisedContent]);
    }

    // §6 block B third remediation's own "identical path" fixture — retired here (14.5-
    // remediation, §14 supervisor finding), not merely deleted without a trace: it drove
    // RecordFinding with a finding path and a --blind-spot-file path deliberately spelled as case
    // variants of the same physical file, which is exactly the CLI door this remediation closes.
    // Both paths are now minted from CardLayout.FileNameFor over two distinct, freshly-allocated
    // ids, so the two can never again be the identical physical file this fixture forced — there is
    // no longer a caller input that could reconstruct it. The still-live half of what this test
    // proved — two distinct locks, in the same directory, acquired without a cross-invocation
    // ordering deadlock — remains covered by BlindSpotRaised_WritesBothCards_EachReferencingTheOther's
    // obligation case (Change scope shares the finding's own directory) and by
    // CardFindingRecordConcurrencyTests' own dedicated coverage of AcquireLocksAndRecord's
    // acquire-probe-release-retry shape.

    private string WriteInitialSectionCard(string fileStem, string id)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "Title", "open", CardOwner.Architect, CardScope.Change, Section, Recorded, Recorded);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

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

    private static FindingDegradationStatus AssertResolved(FindingDegradationEvaluation evaluation) =>
        evaluation.Match(
            onResolved: static status => status,
            onAmbiguous: static (label, filePaths) =>
                throw new Xunit.Sdk.XunitException($"expected Resolved, got Ambiguous('{label}', [{string.Join(", ", filePaths)}])."));
}
