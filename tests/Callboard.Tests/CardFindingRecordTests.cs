using System.Diagnostics;
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

    // An obligation is Change-scoped, so it lands beside the finding in the change's own
    // directory; a hazard is Repository-scoped, so it lands in callboard/register/ instead — the
    // same fixed-by-kind table CardScopeRules.Validate and CardStore.ScopeForRaisedCard both state
    // (see CardFindingRecordScopeAgreementTests).
    private string RaisedCardPath(CardKind kind, string fileStem) =>
        Path.Combine(kind == CardKind.Obligation ? _directory : _registerDirectory, fileStem + ".md");

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
        var findingPath = Path.Combine(_directory, "f-0001.md");

        var outcome = CardStore.RecordFinding(
            _root, findingPath, "Everything checked clean", CardOwner.Worker, Section, "Body of the finding.",
            instrument: "make gates", FindingExtent.BlockScope, verifiedAt: "abc123",
            raiseRequest: null, FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(5), ChangeName);

        var recorded = AssertRecorded(outcome);
        Assert.Null(recorded.RaisedCard);
        Assert.Equal("none", recorded.Finding.FindingFields.BlindSpot.Match(onNone: static () => "none", onRaisedAs: static _ => "raised-as"));

        // No card besides the finding itself exists anywhere the raised card could have landed —
        // proves "none" really did skip the second write, not merely that the outcome says so.
        var filesInDirectory = Directory.GetFiles(_directory, "*.md");
        Assert.Single(filesInDirectory);
        Assert.Equal(findingPath, filesInDirectory[0]);

        var read = AssertParseSuccess(CardStore.ReadCard(findingPath));
        Assert.Equal("make gates", read.FindingFields.Instrument);
        Assert.Equal("abc123", read.FindingFields.VerifiedAt);
    }

    [Theory]
    [InlineData("obligation")]
    [InlineData("hazard")]
    public void BlindSpotRaised_WritesBothCards_EachReferencingTheOther(string kindText)
    {
        var findingPath = Path.Combine(_directory, "f-0002.md");
        var raisedKind = kindText == "obligation" ? CardKind.Obligation : CardKind.Hazard;
        var raisedPath = RaisedCardPath(raisedKind, "h-0001");
        var raiseRequest = new FindingBlindSpotRaiseRequest(raisedKind, raisedPath, "Blind spot title", "Blind spot content.");

        var outcome = CardStore.RecordFinding(
            _root, findingPath, "Checked, with a gap", CardOwner.Worker, Section, "Body of the finding.",
            instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest, FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(5), ChangeName);

        var recorded = AssertRecorded(outcome);
        Assert.NotNull(recorded.RaisedCard);
        var raisedId = recorded.RaisedCard!.Frontmatter.Id;

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

        // Both files actually landed on disk, independently readable.
        var findingRead = AssertParseSuccess(CardStore.ReadCard(findingPath));
        var raisedRead = AssertParseSuccess(CardStore.ReadCard(raisedPath));
        Assert.Equal(raisedId, findingRead.FindingFields.BlindSpot.Match(onNone: static () => (string?)null, onRaisedAs: id => id));
        Assert.Equal(raisedKind, raisedRead.Frontmatter.Kind);
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

        var findingPath = Path.Combine(_directory, "f-0000.md");
        var raisedPath = RaisedCardPath(CardKind.Hazard, "h-0000");
        var raiseRequest = new FindingBlindSpotRaiseRequest(CardKind.Hazard, raisedPath, "Blind spot title", "Blind spot content.");

        var outcome = CardStore.RecordFinding(
            _root, findingPath, "Everything checked clean", CardOwner.Worker, Section, "Body of the finding.",
            instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest, FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(5), ChangeName);

        AssertRecorded(outcome);
        Assert.True(File.Exists(findingPath));
        Assert.True(File.Exists(raisedPath));
    }

    // The partial-failure case, demonstrated by execution (§6 block B brief): pre-occupy the
    // finding's own path so its write — which happens second, after the raised card's — fails.
    // What would have to break for this to go red: RecordFindingUnderLock skipping its rollback
    // call on the finding's AlreadyExists path.
    [Fact]
    public void FindingWriteFailsAfterRaisedCardAlreadyWritten_RaisedCardIsRolledBack()
    {
        var findingPath = Path.Combine(_directory, "f-0003.md");
        var raisedPath = RaisedCardPath(CardKind.Hazard, "h-0002");

        // Pre-occupy the finding's path with unrelated content — forces the finding's own
        // create-only write to report AlreadyExists once RecordFindingUnderLocks reaches it. The
        // directory has to exist for this stray write itself (unrelated to what RecordFinding does
        // for its own writes), so it is created here, locally, not in shared setup.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(findingPath, "not a card", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var raiseRequest = new FindingBlindSpotRaiseRequest(CardKind.Hazard, raisedPath, "Blind spot title", "Blind spot content.");

        var outcome = CardStore.RecordFinding(
            _root, findingPath, "Checked, with a gap", CardOwner.Worker, Section, "Body of the finding.",
            instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest, FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(5), ChangeName);

        var alreadyExists = Assert.IsType<CardFindingRecordOutcome.FindingAlreadyExists>(outcome);
        Assert.Equal(findingPath, alreadyExists.FilePath);

        // The raised card must not be left behind — this is what "all-or-nothing" means in
        // practice: a write that got as far as landing bytes on disk, then rolled back.
        Assert.False(File.Exists(raisedPath), "the raised card was left behind after the finding's own write failed.");

        // The pre-existing content at the finding's path is untouched — the finding write really
        // did refuse rather than silently succeeding over it.
        Assert.Equal("not a card", File.ReadAllText(findingPath));

        // Nothing else in the directory either — the only two candidate paths are accounted for.
        var filesInDirectory = Directory.GetFiles(_directory, "*.md");
        Assert.Single(filesInDirectory);
    }

    [Fact]
    public void RaisedCardPathAlreadyOccupied_RefusesBeforeWritingTheFinding()
    {
        var findingPath = Path.Combine(_directory, "f-0004.md");
        var raisedPath = RaisedCardPath(CardKind.Hazard, "h-0003");
        Directory.CreateDirectory(_registerDirectory);
        File.WriteAllText(raisedPath, "not a card", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var raiseRequest = new FindingBlindSpotRaiseRequest(CardKind.Hazard, raisedPath, "Blind spot title", "Blind spot content.");

        var outcome = CardStore.RecordFinding(
            _root, findingPath, "Checked, with a gap", CardOwner.Worker, Section, "Body of the finding.",
            instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest, FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(5), ChangeName);

        var alreadyExists = Assert.IsType<CardFindingRecordOutcome.BlindSpotCardAlreadyExists>(outcome);
        Assert.Equal(raisedPath, alreadyExists.FilePath);

        // The finding was never written — the check on the raised card's path runs first.
        Assert.False(File.Exists(findingPath));
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

        var findingPath = Path.Combine(_directory, "f-0005.md");
        var raisedPath = RaisedCardPath(CardKind.Hazard, "h-0004");
        var raiseRequest = new FindingBlindSpotRaiseRequest(CardKind.Hazard, raisedPath, "Blind spot title", "Blind spot content.");

        // RecordFinding runs first, against _directory before it exists — proving blocker 3's fix
        // in passing — which is also what makes WriteInitialSectionCard's own plain File.WriteAllText
        // below able to land in the same, now-existing directory.
        var recordOutcome = CardStore.RecordFinding(
            _root, findingPath, "Checked, with a gap", CardOwner.Worker, sectionId, "Body of the finding.",
            instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest, FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(5), ChangeName);
        AssertRecorded(recordOutcome);

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

        InvokeRollbackRaisedCard(new FindingBlindSpotRaiseRequest(CardKind.Hazard, path, "t", "b"), content);

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

        InvokeRollbackRaisedCard(new FindingBlindSpotRaiseRequest(CardKind.Hazard, path, "t", "b"), "what this call actually wrote");

        Assert.True(File.Exists(path), "content this call never wrote was deleted anyway.");
        Assert.Equal(someoneElsesContent, File.ReadAllText(path));
    }

    private static void InvokeRollbackRaisedCard(FindingBlindSpotRaiseRequest raiseRequest, string raisedContent)
    {
        var method = typeof(CardStore).GetMethod("RollbackRaisedCard", BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, [raiseRequest, raisedContent]);
    }

    // §6 block B third remediation, reviewer's "identical path" finding: a finding path and a
    // --blind-spot-file path that name the same file two different ways used to make
    // AcquireLocksAndRecord's fast path miss (StringComparison.Ordinal saw two different strings)
    // and fall into the two-lock branch, which then self-deadlocked for the full lock timeout on
    // this project's own default-case-insensitive shipped platform (APFS). An obligation is
    // Change-scoped, which resolves to the identical directory a finding's own Section scope does
    // (CardLayout.DirectoryFor), so a case-variant filename collision between the two is a real,
    // reachable shape — not a hazard's Repository-scoped callboard/register/, which never shares a
    // directory with a finding at all. Adapts its assertion to the actual volume's case sensitivity
    // rather than assuming one: on a case-insensitive volume the two paths really are one file, and
    // the correct outcome is a clean FindingAlreadyExists refusal (nothing corrupted, nothing
    // orphaned); on a case-sensitive volume they are genuinely different files, and both are
    // written. Either way, the load-bearing property is the same: this returns fast, never anywhere
    // near the lock timeout, and never ToolFailure.
    [Fact]
    public void SameFileNamedByACaseVariantPath_NeverSelfDeadlocks_RegardlessOfVolumeCaseSensitivity()
    {
        var findingPath = Path.Combine(_directory, "case-variant-target.md");
        var raisedPath = Path.Combine(_directory, "CASE-VARIANT-TARGET.MD");
        var raiseRequest = new FindingBlindSpotRaiseRequest(CardKind.Obligation, raisedPath, "Blind spot title", "Blind spot content.");

        var stopwatch = Stopwatch.StartNew();
        var outcome = CardStore.RecordFinding(
            _root, findingPath, "Checked, with a gap", CardOwner.Worker, Section, "Body of the finding.",
            instrument: null, FindingExtent.BlockScope, verifiedAt: null, raiseRequest, FindingDisposition.Measured, Recorded, TimeSpan.FromSeconds(5), ChangeName);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"took {stopwatch.Elapsed} deciding whether the two paths are the same lock — looks like the self-deadlock reproduced.");

        outcome.Match<object?>(
            onRecorded: recorded =>
            {
                // Case-sensitive volume: genuinely two different files, both written successfully.
                Assert.True(File.Exists(findingPath));
                Assert.NotNull(recorded.RaisedCard);
                return null;
            },
            onFindingAlreadyExists: already =>
            {
                // Case-insensitive volume (this sandbox's own TMPDIR, confirmed by direct
                // filesystem probe during this remediation): the two paths are one physical file —
                // the raised card's write landed there first, and the finding's own create-only
                // check then correctly found it occupied. Nothing was left corrupted or orphaned.
                Assert.Equal(findingPath, already.FilePath);
                return null;
            },
            onBlindSpotCardAlreadyExists: already => throw new Xunit.Sdk.XunitException(
                $"unexpected BlindSpotCardAlreadyExists('{already.FilePath}')"),
            onFindingLayoutMismatch: mismatch => throw new Xunit.Sdk.XunitException($"unexpected FindingLayoutMismatch: {mismatch.Reason}"),
            onBlindSpotLayoutMismatch: mismatch => throw new Xunit.Sdk.XunitException($"unexpected BlindSpotLayoutMismatch: {mismatch.Reason}"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException(
                $"got ToolFailure — looks like the self-deadlock reproduced: {toolFailure.Reason}"));
    }

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
