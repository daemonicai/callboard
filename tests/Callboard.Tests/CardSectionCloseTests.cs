using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 5.8 / §8a block A — closing a section under lock (§5 block E, work-lifecycle: "closing it SHALL
/// record the acting role and the time"; §8a block A, "Approval is provisional until the section
/// closes"). This type never checks §9's closing conditions (open obligations, undeferred
/// questions, unresolved threads) — see <see cref="CardSectionCloseOutcome"/>'s own doc comment;
/// these tests cover the entity's own state plus §8a's two landing refusals (not-approved,
/// non-zero-or-absent gate — the `reviewed_state` comparison this section briefly carried was cut
/// by the Product Owner's "approved is terminal" ruling) and the all-or-none, idempotent-retry
/// write.
/// </summary>
public sealed class CardSectionCloseTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-section-close-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;

    public CardSectionCloseTests()
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
    public void CloseSection_OnAnOpenSection_RecordsActingRoleAndTime_AndFlipsStatus()
    {
        var path = WriteInitialSectionCard("s-0001", "S-0001");

        var outcome = CardStore.CloseSection(_root, path, CardOwner.Architect, Created.AddDays(3), TimeSpan.FromSeconds(5), ChangeName);

        var closed = AssertClosed(outcome);
        Assert.Equal("closed", closed.Card.Frontmatter.Status);
        Assert.Equal(CardOwner.Architect, closed.Card.SectionFields.ClosedBy);
        Assert.Equal(Created.AddDays(3), closed.Card.SectionFields.ClosedAt);
        Assert.Empty(closed.LandedBlocks);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("closed", read.Frontmatter.Status);
        Assert.Equal(CardOwner.Architect, read.SectionFields.ClosedBy);
        Assert.Equal(Created.AddDays(3), read.SectionFields.ClosedAt);
    }

    // Owed evidence — closing does not re-record a new acting role/time over the first: what would
    // have to break for this to go red is CloseSectionUnderExistingLock skipping the
    // already-closed check and silently overwriting ClosedBy/ClosedAt on a second call.
    [Fact]
    public void CloseSection_AlreadyClosed_Refuses_AndDoesNotOverwriteTheFirstClosure_AndRecordsTheRefusal()
    {
        var path = WriteInitialSectionCard("s-0002", "S-0002");
        AssertClosed(CardStore.CloseSection(_root, path, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName));

        var outcome = CardStore.CloseSection(_root, path, CardOwner.Supervisor, Created.AddDays(1), TimeSpan.FromSeconds(5), ChangeName);

        var already = Assert.IsType<CardSectionCloseOutcome.AlreadyClosed>(outcome);
        Assert.Equal(path, already.FilePath);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(CardOwner.Architect, read.SectionFields.ClosedBy);
        Assert.Equal(Created, read.SectionFields.ClosedAt);
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Supervisor, recorded.By);
        Assert.Equal(already.RefusingRule, recorded.Rule);
        Assert.Equal(already.Remedy, recorded.Remedy);
    }

    [Fact]
    public void CloseSection_TargetIsNotASectionCard_Refuses_AndRecordsTheRefusal()
    {
        var path = Path.Combine(_directory, "q-0001.md");
        var frontmatter = new CardFrontmatter(
            "Q-0001", CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Change, "5", Created, Created);
        AssertWriteSuccess(CardStore.WriteCard(_root, path, new NewCardFile(frontmatter, "Body."), TimeSpan.FromSeconds(5), ChangeName));

        var outcome = CardStore.CloseSection(_root, path, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notASection = Assert.IsType<CardSectionCloseOutcome.NotASectionCard>(outcome);
        Assert.Equal(CardKind.Question, notASection.Kind);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.Equal(notASection.RefusingRule, recorded.Rule);
        Assert.Equal(notASection.Remedy, recorded.Remedy);
    }

    [Fact]
    public void CloseSection_WhenNoCardExistsAtThatPath_Fails()
    {
        var path = Path.Combine(_directory, "missing.md");

        var outcome = CardStore.CloseSection(_root, path, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notFound = Assert.IsType<CardSectionCloseOutcome.CardNotFound>(outcome);
        Assert.Equal(path, notFound.FilePath);
    }

    [Fact]
    public void CloseSection_LayoutMismatch_ReturnsLayoutMismatch_NotCardNotFound()
    {
        var path = WriteInitialSectionCard("s-0003", "S-0003");

        var outcome = CardStore.CloseSection(_root, path, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), "a-different-change");

        Assert.IsType<CardSectionCloseOutcome.LayoutMismatch>(outcome);
    }

    [Fact]
    public void CloseSection_WhenTheCardFileIsCorrupt_ReturnsCardCorrupt_NotARefusalShapedOutcome()
    {
        var path = Path.Combine(_directory, "corrupt.md");
        File.WriteAllText(path, "not a card file at all");

        var outcome = CardStore.CloseSection(_root, path, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var corrupt = Assert.IsType<CardSectionCloseOutcome.CardCorrupt>(outcome);
        Assert.Equal(path, corrupt.FilePath);
    }

    [Fact]
    public void CloseSection_WhenTheLockIsHeldByAnotherCaller_ReturnsToolFailure_NotARefusalShapedOutcome()
    {
        var path = WriteInitialSectionCard("s-0004", "S-0004");
        var holder = AssertAcquired(CardLock.Acquire(path, TimeSpan.FromSeconds(5)));

        try
        {
            var outcome = CardStore.CloseSection(_root, path, CardOwner.Architect, Created, TimeSpan.FromMilliseconds(200), ChangeName);

            Assert.IsType<CardSectionCloseOutcome.ToolFailure>(outcome);
        }
        finally
        {
            holder.Dispose();
        }
    }

    // 8a.3 / 8a.4 — every approved block in the section lands, in one operation.
    [Fact]
    public void CloseSection_WithApprovedBlocks_LandsEveryOne_AsOneOperation()
    {
        var sectionPath = WriteInitialSectionCard("s-0005", "S-0005");
        var block1Path = WriteApprovedBlockCard("b-0001", "B-0001", "S-0005");
        var block2Path = WriteApprovedBlockCard("b-0002", "B-0002", "S-0005");

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Supervisor, Created, TimeSpan.FromSeconds(5), ChangeName);

        var closed = AssertClosed(outcome);
        Assert.Equal(2, closed.LandedBlocks.Count);
        Assert.All(closed.LandedBlocks, block => Assert.Equal("landed", block.Frontmatter.Status));

        var readBlock1 = AssertParseSuccess(CardStore.ReadCard(block1Path));
        Assert.Equal("landed", readBlock1.Frontmatter.Status);
        var readBlock2 = AssertParseSuccess(CardStore.ReadCard(block2Path));
        Assert.Equal("landed", readBlock2.Frontmatter.Status);
    }

    // 8a.3 — a card in a *different* section is never touched, only ever scanned and skipped.
    [Fact]
    public void CloseSection_NeverTouchesABlockBelongingToAnotherSection()
    {
        var sectionPath = WriteInitialSectionCard("s-0006", "S-0006");
        WriteInitialSectionCard("s-0007", "S-0007");
        var otherBlockPath = WriteApprovedBlockCard("b-0003", "B-0003", "S-0007");
        var bytesBefore = File.ReadAllText(otherBlockPath);

        AssertClosed(CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName));

        Assert.Equal(bytesBefore, File.ReadAllText(otherBlockPath));
    }

    // 8a.3 — a block already landed is skipped, not refused: the whole close proceeds and the
    // already-landed block is reported back exactly as landed, untouched a second time.
    [Fact]
    public void CloseSection_ABlockAlreadyLanded_IsSkipped_NotRefused()
    {
        var sectionPath = WriteInitialSectionCard("s-0008", "S-0008");
        var landedPath = WriteLandedBlockCard("b-0004", "B-0004", "S-0008");
        var bytesBefore = File.ReadAllText(landedPath);
        var mtimeBefore = File.GetLastWriteTimeUtc(landedPath);

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var closed = AssertClosed(outcome);
        Assert.Single(closed.LandedBlocks);
        Assert.Equal("B-0004", closed.LandedBlocks[0].Frontmatter.Id);
        Assert.Equal(bytesBefore, File.ReadAllText(landedPath));
        Assert.Equal(mtimeBefore, File.GetLastWriteTimeUtc(landedPath));
    }

    // 8a.4 — any block not approved refuses the whole close, and leaves every other card
    // untouched. The offending block itself is not "untouched" any more (§9 block E): the refusal
    // records against it, under its own already-held lock — see the coverage-gate test below for
    // the recorded-entry assertion this test predates.
    [Fact]
    public void CloseSection_ABlockNotApproved_RefusesTheWholeClose_LeavesEveryOtherCardUntouched()
    {
        var sectionPath = WriteInitialSectionCard("s-0009", "S-0009");
        var approvedPath = WriteApprovedBlockCard("b-0005", "B-0005", "S-0009");
        var inReviewPath = WriteBlockCardInState("b-0006", "B-0006", "S-0009", "in-review");
        var sectionBytesBefore = File.ReadAllText(sectionPath);
        var approvedBytesBefore = File.ReadAllText(approvedPath);

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notApproved = Assert.IsType<CardSectionCloseOutcome.BlockNotApproved>(outcome);
        Assert.Equal("B-0006", notApproved.BlockId);
        Assert.Equal(inReviewPath, notApproved.BlockFilePath);
        Assert.Equal(BlockFlowState.InReview, notApproved.ActualState);

        Assert.Equal(sectionBytesBefore, File.ReadAllText(sectionPath));
        Assert.Equal(approvedBytesBefore, File.ReadAllText(approvedPath));

        var recorded = Assert.Single(AssertParseSuccess(CardStore.ReadCard(inReviewPath)).Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.Equal(notApproved.RefusingRule, recorded.Rule);
        Assert.Equal(notApproved.Remedy, recorded.Remedy);
    }

    // 8a.5 was cut in full (Product Owner ruling: "approved is terminal") — closing a section no
    // longer compares reviewed_state against anything, so a block with an "old" reviewed_state
    // lands exactly like any other approved block, proven here rather than merely asserted absent.
    [Fact]
    public void CloseSection_ABlockWithAnOldReviewedState_StillLands_NoComparisonIsMade()
    {
        var sectionPath = WriteInitialSectionCard("s-0010", "S-0010");
        var blockPath = WriteBlockCard("b-0007", "B-0007", "S-0010", "approved", "an-older-state", round: null, gateResults: null);

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var closed = AssertClosed(outcome);
        Assert.Single(closed.LandedBlocks);
        Assert.Equal("landed", AssertParseSuccess(CardStore.ReadCard(blockPath)).Frontmatter.Status);
    }

    // 8a.6 — a gate recorded non-zero refuses the close.
    [Fact]
    public void CloseSection_ABlockWithAFailingGate_Refuses_AndRecordsTheRefusal()
    {
        var sectionPath = WriteInitialSectionCard("s-0011", "S-0011");
        var blockPath = WriteApprovedBlockCard("b-0008", "B-0008", "S-0011", gateResults: [new GateResult("build", 1, 1)]);

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var failed = Assert.IsType<CardSectionCloseOutcome.BlockGateFailed>(outcome);
        Assert.Equal("B-0008", failed.BlockId);
        Assert.Equal(blockPath, failed.BlockFilePath);
        Assert.Equal("build", failed.GateLabel);
        Assert.Equal(1, failed.ExitCode);

        var recorded = Assert.Single(AssertParseSuccess(CardStore.ReadCard(blockPath)).Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.Equal(failed.RefusingRule, recorded.Rule);
        Assert.Equal(failed.Remedy, recorded.Remedy);
    }

    // 8a.6 — absent is a refusal in its own right, not a pass by default: a gate this block has
    // evidence for in an earlier round, with nothing recorded for the current round, still refuses.
    [Fact]
    public void CloseSection_ABlockWithAnAbsentGateThisRound_Refuses_NotAPassByDefault_AndRecordsTheRefusal()
    {
        var sectionPath = WriteInitialSectionCard("s-0012", "S-0012");
        // Round 2, but the only recorded gate result is from round 1 — GateStatusOf("build") for
        // round 2 reports Absent (BlockCardFields's own "current round only is evidence" rule).
        var blockPath = WriteApprovedBlockCard(
            "b-0009", "B-0009", "S-0012", round: 2, gateResults: [new GateResult("build", 0, 1)]);

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var absent = Assert.IsType<CardSectionCloseOutcome.BlockGateAbsent>(outcome);
        Assert.Equal("B-0009", absent.BlockId);
        Assert.Equal(blockPath, absent.BlockFilePath);
        Assert.Equal("build", absent.GateLabel);

        var recorded = Assert.Single(AssertParseSuccess(CardStore.ReadCard(blockPath)).Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.Equal(absent.RefusingRule, recorded.Rule);
        Assert.Equal(absent.Remedy, recorded.Remedy);
    }

    // 8a.6 — a green gate this round does not refuse.
    [Fact]
    public void CloseSection_ABlockWithAGreenGate_Lands()
    {
        var sectionPath = WriteInitialSectionCard("s-0013", "S-0013");
        WriteApprovedBlockCard("b-0010", "B-0010", "S-0013", gateResults: [new GateResult("build", 0, 1)]);

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var closed = AssertClosed(outcome);
        Assert.Single(closed.LandedBlocks);
    }

    // 8a.3 — a card that fails to parse at all is conservatively treated as possibly one of this
    // section's own blocks: its `section` field cannot be checked, so the whole close refuses
    // rather than silently ignoring it (the same discipline ArchiveChange already applies).
    [Fact]
    public void CloseSection_AnUnreadableCardInTheDirectory_Refuses_NotSilentlyIgnored()
    {
        var sectionPath = WriteInitialSectionCard("s-0014", "S-0014");
        var corruptPath = Path.Combine(_directory, "corrupt-block.md");
        File.WriteAllText(corruptPath, "not a card file at all");

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var corrupt = Assert.IsType<CardSectionCloseOutcome.CardCorrupt>(outcome);
        Assert.Equal(corruptPath, corrupt.FilePath);
    }

    // 8a.17 / §9 block E — a block's stored round disagreeing with its own transition history
    // refuses the whole close, and records against that block.
    [Fact]
    public void CloseSection_ABlockWithADisagreeingRound_Refuses_NamesBothFigures_AndRecordsTheRefusal()
    {
        var sectionPath = WriteInitialSectionCard("s-0015", "S-0015");
        // Stored round 3, but no round-incrementing transition in history at all — expected round 1.
        var blockPath = Path.Combine(_directory, "b-0011.md");
        var blockFrontmatter = new CardFrontmatter(
            "B-0011", CardKind.Block, "A block", "approved", CardOwner.Architect, CardScope.Change, "S-0015", Created, Created);
        var blockFields = new BlockCardFields(Base: "base-commit", ReviewedState: "reviewed-state", Tasks: ["5.1"], Round: 3, BlockedBy: [], GateResults: []);
        var blockCard = new CardFile(blockFrontmatter, "Body.", [], [], [], blockFields, []);
        File.WriteAllText(blockPath, CardFileWriter.Serialize(blockCard), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var disagreement = Assert.IsType<CardSectionCloseOutcome.RoundDisagreesWithHistory>(outcome);
        Assert.Equal(blockPath, disagreement.BlockFilePath);
        Assert.Equal(3, disagreement.StoredRound);
        Assert.Equal(1, disagreement.ExpectedRound);

        var recorded = Assert.Single(AssertParseSuccess(CardStore.ReadCard(blockPath)).Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.Equal(disagreement.RefusingRule, recorded.Rule);
        Assert.Equal(disagreement.Remedy, recorded.Remedy);
    }

    // process-enforcement: "Section close settles its obligations" (9.4) — an open obligation owed
    // by this section refuses the close, lists it, and records against the section.
    [Fact]
    public void CloseSection_AnOpenObligationOwedByTheSection_Refuses_AndRecordsTheRefusal()
    {
        var sectionPath = WriteInitialSectionCard("s-0016", "S-0016");
        var obligationPath = Path.Combine(_directory, "o-0001.md");
        var obligationFrontmatter = new CardFrontmatter(
            "O-0001", CardKind.Obligation, "Discharge the debt", RegisterLifecycleState.Open.ToWireString(), CardOwner.Worker, CardScope.Change, string.Empty, Created, Created);
        var obligationCard = new CardFile(
            obligationFrontmatter, "Body.", [], [], RegisterFields: new RegisterCardFields(null, null, null, null, OwedBy: "S-0016"));
        File.WriteAllText(obligationPath, CardFileWriter.Serialize(obligationCard), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var openObligations = Assert.IsType<CardSectionCloseOutcome.OpenObligations>(outcome);
        Assert.Equal("S-0016", openObligations.SectionId);
        var only = Assert.Single(openObligations.Obligations);
        Assert.Equal("O-0001", only.Id);
        Assert.Equal("Discharge the debt", only.Title);

        var recorded = Assert.Single(AssertParseSuccess(CardStore.ReadCard(sectionPath)).Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.Equal(openObligations.RefusingRule, recorded.Rule);
        Assert.Equal(openObligations.Remedy, recorded.Remedy);
    }

    // process-enforcement: "Section close settles its obligations" — a discharged obligation owed
    // by the section does not block the close.
    [Fact]
    public void CloseSection_ADischargedObligationOwedByTheSection_DoesNotRefuse()
    {
        var sectionPath = WriteInitialSectionCard("s-0017", "S-0017");
        var obligationPath = Path.Combine(_directory, "o-0002.md");
        var obligationFrontmatter = new CardFrontmatter(
            "O-0002", CardKind.Obligation, "Already paid", RegisterLifecycleState.Discharged.ToWireString(), CardOwner.Worker, CardScope.Change, string.Empty, Created, Created);
        var obligationCard = new CardFile(
            obligationFrontmatter, "Body.", [], [], RegisterFields: new RegisterCardFields(null, null, CardOwner.Worker, Created, OwedBy: "S-0017"));
        File.WriteAllText(obligationPath, CardFileWriter.Serialize(obligationCard), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        AssertClosed(outcome);
    }

    // process-enforcement: "Section close settles its questions" (9.5) — an open question raised
    // in this section refuses the close, names it, and records against the section.
    [Fact]
    public void CloseSection_AnOpenQuestionRaisedInTheSection_Refuses_AndRecordsTheRefusal()
    {
        var sectionPath = WriteInitialSectionCard("s-0018", "S-0018");
        WriteQuestionCard("q-0001", "Q-0001", "S-0018", QuestionStatus.Open);

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var openQuestion = Assert.IsType<CardSectionCloseOutcome.OpenUndeferredQuestion>(outcome);
        Assert.Equal("S-0018", openQuestion.SectionId);
        Assert.Equal("Q-0001", openQuestion.QuestionId);

        var recorded = Assert.Single(AssertParseSuccess(CardStore.ReadCard(sectionPath)).Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.Equal(openQuestion.RefusingRule, recorded.Rule);
        Assert.Equal(openQuestion.Remedy, recorded.Remedy);
    }

    // process-enforcement: "Section close settles its questions" — a question deferred to a named
    // target permits the close, and stays open against that target (register: "the close proceeds
    // and the question remains open against its target").
    [Fact]
    public void CloseSection_ADeferredQuestionRaisedInTheSection_DoesNotRefuse()
    {
        var sectionPath = WriteInitialSectionCard("s-0019", "S-0019");
        WriteQuestionCard("q-0002", "Q-0002", "S-0019", QuestionStatus.Deferred);

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        AssertClosed(outcome);
    }

    // §9 remediation round three, F2 — promoting a comment on the *section card itself* (9.6's
    // first arm) must raise a question 9.5's open-question gate can actually see: proof of the
    // link, not just the mechanism (CardCommentPromoteTests covers the mechanism directly).
    [Fact]
    public void CloseSection_AQuestionPromotedFromACommentOnTheSectionCardItself_Refuses_NamingThatQuestion()
    {
        var sectionPath = WriteSectionCardWithComment("s-0018a", "S-0018A", "thread-1", CardOwner.Reviewer);
        var registerDirectory = Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(registerDirectory);
        var raisedPath = Path.Combine(registerDirectory, "q-0300.md");

        var promoteOutcome = CardStore.PromoteComment(
            _root, sectionPath, "thread-1", raisedPath, CardKind.Question, "Should we ship X?", CardOwner.Reviewer,
            CardOwner.ProductOwner, "Raised while resolving a thread.", ChangeName, Created, TimeSpan.FromSeconds(5));
        var promoted = Assert.IsType<CardCommentPromoteOutcome.Promoted>(promoteOutcome);
        Assert.Equal("S-0018A", promoted.RaisedCard.Frontmatter.Section);

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var openQuestion = Assert.IsType<CardSectionCloseOutcome.OpenUndeferredQuestion>(outcome);
        Assert.Equal("S-0018A", openQuestion.SectionId);
        Assert.Equal(promoted.RaisedCard.Frontmatter.Id, openQuestion.QuestionId);
    }

    // process-enforcement: "Section close settles its addressed threads" (9.6, the refusal half) —
    // an unresolved comment addressed to a role, on the section card itself, refuses the close and
    // records against it.
    [Fact]
    public void CloseSection_AnUnresolvedAddressedThreadOnTheSectionItself_Refuses_AndRecordsTheRefusal()
    {
        var path = Path.Combine(_directory, "s-0020.md");
        var frontmatter = new CardFrontmatter(
            "S-0020", CardKind.Section, "Title", "open", CardOwner.Architect, CardScope.Change, string.Empty, Created, Created);
        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "please confirm", null, To: CardOwner.Reviewer, null, []);
        var card = new CardFile(frontmatter, "Body.", [comment], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.CloseSection(_root, path, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var unresolvedThread = Assert.IsType<CardSectionCloseOutcome.UnresolvedAddressedThread>(outcome);
        Assert.Equal("S-0020", unresolvedThread.CardId);
        Assert.Equal(["C-0001"], unresolvedThread.ThreadIds);

        var recorded = Assert.Single(AssertParseSuccess(CardStore.ReadCard(path)).Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.Equal(unresolvedThread.RefusingRule, recorded.Rule);
        Assert.Equal(unresolvedThread.Remedy, recorded.Remedy);
    }

    // process-enforcement: "Section close settles its addressed threads" — a *resolved* addressed
    // comment does not refuse.
    [Fact]
    public void CloseSection_AResolvedAddressedThreadOnTheSectionItself_DoesNotRefuse()
    {
        var path = Path.Combine(_directory, "s-0021.md");
        var frontmatter = new CardFrontmatter(
            "S-0021", CardKind.Section, "Title", "open", CardOwner.Architect, CardScope.Change, string.Empty, Created, Created);
        var raised = new CardComment("C-0002", CardOwner.Worker, Created, "please confirm", null, To: CardOwner.Reviewer, null, []);
        var resolved = new CardComment("C-0003", CardOwner.Reviewer, Created.AddHours(1), "confirmed", null, To: null, Resolves: "C-0002", []);
        var card = new CardFile(frontmatter, "Body.", [raised, resolved], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.CloseSection(_root, path, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        AssertClosed(outcome);
    }

    // process-enforcement: "Work cannot proceed past a stop-and-ask" (9.8's carried arm) — an
    // approved block blocked by an open Product Owner question cannot land by its section closing,
    // and the refusal records against the block.
    [Fact]
    public void CloseSection_AnApprovedBlockBlockedByAnOpenProductOwnerQuestion_Refuses_AndRecordsTheRefusal()
    {
        var sectionPath = WriteInitialSectionCard("s-0022", "S-0022");
        WriteOpenProductOwnerQuestion("q-0003", "Q-0003");
        var blockPath = Path.Combine(_directory, "b-0012.md");
        var blockFrontmatter = new CardFrontmatter(
            "B-0012", CardKind.Block, "A block", "approved", CardOwner.Architect, CardScope.Change, "S-0022", Created, Created);
        var blockFields = new BlockCardFields(Base: "base-commit", ReviewedState: "reviewed-state", Tasks: ["5.1"], Round: null, BlockedBy: ["Q-0003"], GateResults: []);
        var blockCard = new CardFile(blockFrontmatter, "Body.", [], [], [], blockFields, []);
        File.WriteAllText(blockPath, CardFileWriter.Serialize(blockCard), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var blocked = Assert.IsType<CardSectionCloseOutcome.BlockedByOpenProductOwnerQuestion>(outcome);
        Assert.Equal("B-0012", blocked.BlockId);
        Assert.Equal("Q-0003", blocked.QuestionId);

        var recorded = Assert.Single(AssertParseSuccess(CardStore.ReadCard(blockPath)).Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.Equal(blocked.RefusingRule, recorded.Rule);
        Assert.Equal(blocked.Remedy, recorded.Remedy);
    }

    // §10 remediation, round two, S2: a deferred product-owner question blocks section-driven
    // landing exactly as an open one does — deferring does not lift the halt (Product Owner
    // ruling). Same shape as the open-question test above, deferred rather than open.
    [Fact]
    public void CloseSection_AnApprovedBlockBlockedByADeferredProductOwnerQuestion_Refuses_AndRecordsTheRefusal()
    {
        var sectionPath = WriteInitialSectionCard("s-0027", "S-0027");
        WriteDeferredProductOwnerQuestion("q-0006", "Q-0006");
        var blockPath = Path.Combine(_directory, "b-0025.md");
        var blockFrontmatter = new CardFrontmatter(
            "B-0025", CardKind.Block, "A block", "approved", CardOwner.Architect, CardScope.Change, "S-0027", Created, Created);
        var blockFields = new BlockCardFields(Base: "base-commit", ReviewedState: "reviewed-state", Tasks: ["5.1"], Round: null, BlockedBy: ["Q-0006"], GateResults: []);
        var blockCard = new CardFile(blockFrontmatter, "Body.", [], [], [], blockFields, []);
        File.WriteAllText(blockPath, CardFileWriter.Serialize(blockCard), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var blocked = Assert.IsType<CardSectionCloseOutcome.BlockedByOpenProductOwnerQuestion>(outcome);
        Assert.Equal("B-0025", blocked.BlockId);
        Assert.Equal("Q-0006", blocked.QuestionId);

        var recorded = Assert.Single(AssertParseSuccess(CardStore.ReadCard(blockPath)).Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.Equal(blocked.RefusingRule, recorded.Rule);
        Assert.Equal(blocked.Remedy, recorded.Remedy);
    }

    // process-enforcement: "Section close settles its addressed threads" — a fresh unresolved
    // addressed thread (never survived a round boundary) refuses the close.
    [Fact]
    public void CloseSection_AnAddressedCommentRaisedThisRound_Refuses()
    {
        var sectionPath = WriteInitialSectionCard("s-0023", "S-0023");
        var blockPath = Path.Combine(_directory, "b-0013.md");
        var comment = new CardComment("C-0004", CardOwner.Worker, Created, "a question", null, To: CardOwner.Reviewer, null, []);
        var blockFrontmatter = new CardFrontmatter(
            "B-0013", CardKind.Block, "A block", "approved", CardOwner.Architect, CardScope.Change, "S-0023", Created, Created);
        var blockFields = new BlockCardFields(Base: "base-commit", ReviewedState: "reviewed-state", Tasks: ["5.1"], Round: null, BlockedBy: [], GateResults: []);
        var blockCard = new CardFile(blockFrontmatter, "Body.", [comment], [], [], blockFields, []);
        File.WriteAllText(blockPath, CardFileWriter.Serialize(blockCard), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var unresolvedThread = Assert.IsType<CardSectionCloseOutcome.UnresolvedAddressedThread>(outcome);
        Assert.Equal("B-0013", unresolvedThread.CardId);
        Assert.Equal(["C-0004"], unresolvedThread.ThreadIds);
    }

    // process-enforcement: "Section close settles its addressed threads" (§9 block E, architect
    // ruling) — the refusal is absolute, with no age qualifier. An addressed thread that has
    // survived a round boundary on its own block still refuses the close exactly like a fresh one —
    // ageing never exempts a thread from this gate, only adds it to the separate, non-refusing
    // 'section status' prompt (CloseSection_..._IsSurfacedAsAgeing_ByFindAgeingAddressedThreads,
    // below, proves that half).
    [Fact]
    public void CloseSection_AnAddressedCommentThatSurvivedARoundBoundary_StillRefuses()
    {
        var sectionPath = WriteInitialSectionCard("s-0024", "S-0024");
        var blockPath = Path.Combine(_directory, "b-0014.md");
        var comment = new CardComment("C-0005", CardOwner.Worker, Created, "a question", null, To: CardOwner.Reviewer, null, []);
        var changesRequested = new CardBlockTransitionEntry(
            CardOwner.Reviewer, "changes-requested", BlockFlowState.InReview, BlockFlowState.Briefed, Created.AddHours(1), []);
        var blockFrontmatter = new CardFrontmatter(
            "B-0014", CardKind.Block, "A block", "approved", CardOwner.Architect, CardScope.Change, "S-0024", Created, Created);
        var blockFields = new BlockCardFields(Base: "base-commit", ReviewedState: "reviewed-state", Tasks: ["5.1"], Round: 2, BlockedBy: [], GateResults: []);
        var blockCard = new CardFile(blockFrontmatter, "Body.", [comment], [], [], blockFields, [changesRequested]);
        File.WriteAllText(blockPath, CardFileWriter.Serialize(blockCard), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var unresolvedThread = Assert.IsType<CardSectionCloseOutcome.UnresolvedAddressedThread>(outcome);
        Assert.Equal("B-0014", unresolvedThread.CardId);
        Assert.Equal(["C-0005"], unresolvedThread.ThreadIds);
    }

    // process-enforcement: "the system SHALL surface addressed comments left unresolved for longer
    // than one round, as a prompt rather than a constraint" (9.6, the prompt half — architect
    // ruling: read from 'section status', not from a close attempt). Same fixture shape as the
    // refusal test above, read directly through the finder 'section status' calls.
    [Fact]
    public void FindAgeingAddressedThreads_AnAddressedCommentThatSurvivedARoundBoundary_IsSurfacedAsAgeing()
    {
        var blockPath = Path.Combine(_directory, "b-0015.md");
        var comment = new CardComment("C-0006", CardOwner.Worker, Created, "a question", null, To: CardOwner.Reviewer, null, []);
        var changesRequested = new CardBlockTransitionEntry(
            CardOwner.Reviewer, "changes-requested", BlockFlowState.InReview, BlockFlowState.Briefed, Created.AddHours(1), []);
        var blockFrontmatter = new CardFrontmatter(
            "B-0015", CardKind.Block, "A block", "approved", CardOwner.Architect, CardScope.Change, "S-0025", Created, Created);
        var blockFields = new BlockCardFields(Base: "base-commit", ReviewedState: "reviewed-state", Tasks: ["5.1"], Round: 2, BlockedBy: [], GateResults: []);
        var blockCard = new CardFile(blockFrontmatter, "Body.", [comment], [], [], blockFields, [changesRequested]);
        File.WriteAllText(blockPath, CardFileWriter.Serialize(blockCard), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var ageing = Assert.Single(CardStore.FindAgeingAddressedThreads(_directory, "S-0025"));

        Assert.Equal("B-0015", ageing.CardId);
        Assert.Equal(blockPath, ageing.CardFilePath);
        Assert.Equal("C-0006", ageing.ThreadId);
        Assert.Equal(CardOwner.Reviewer, ageing.AddressedTo);
    }

    // A fresh unresolved thread (never survived a round boundary) is not reported as ageing.
    [Fact]
    public void FindAgeingAddressedThreads_AnAddressedCommentRaisedThisRound_IsNotSurfaced()
    {
        var blockPath = Path.Combine(_directory, "b-0016.md");
        var comment = new CardComment("C-0007", CardOwner.Worker, Created, "a question", null, To: CardOwner.Reviewer, null, []);
        var blockFrontmatter = new CardFrontmatter(
            "B-0016", CardKind.Block, "A block", "approved", CardOwner.Architect, CardScope.Change, "S-0026", Created, Created);
        var blockFields = new BlockCardFields(Base: "base-commit", ReviewedState: "reviewed-state", Tasks: ["5.1"], Round: null, BlockedBy: [], GateResults: []);
        var blockCard = new CardFile(blockFrontmatter, "Body.", [comment], [], [], blockFields, []);
        File.WriteAllText(blockPath, CardFileWriter.Serialize(blockCard), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        Assert.Empty(CardStore.FindAgeingAddressedThreads(_directory, "S-0026"));
    }

    private void WriteQuestionCard(string fileStem, string id, string sectionId, QuestionStatus status)
    {
        var registerDirectory = Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(registerDirectory);
        var path = Path.Combine(registerDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Question, "A question", QuestionStatusWireFormat.ToWireString(status), CardOwner.Worker, CardScope.Repository, sectionId, Created, Created);
        var card = new CardFile(
            frontmatter, "Body.", [], [],
            QuestionFields: status == QuestionStatus.Deferred
                ? new QuestionCardFields { DeferredBy = CardOwner.Worker, DeferredAt = Created, DeferredTarget = "a later change" }
                : QuestionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private void WriteOpenProductOwnerQuestion(string fileStem, string id)
    {
        var registerDirectory = Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(registerDirectory);
        var path = Path.Combine(registerDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Question, "Should we ship X?", QuestionStatus.Open.ToWireString(), CardOwner.ProductOwner, CardScope.Repository, string.Empty, Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    // §10 remediation, round two, S2: a deferred question owned by the product owner — deferring
    // does not lift the halt (Product Owner ruling).
    private void WriteDeferredProductOwnerQuestion(string fileStem, string id)
    {
        var registerDirectory = Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(registerDirectory);
        var path = Path.Combine(registerDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Question, "Should we ship X?", QuestionStatus.Deferred.ToWireString(), CardOwner.ProductOwner, CardScope.Repository, string.Empty, Created, Created);
        var card = new CardFile(
            frontmatter, "Body.", [], [],
            QuestionFields: new QuestionCardFields { DeferredBy = CardOwner.Worker, DeferredAt = Created, DeferredTarget = "a later change" });
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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

    private string WriteSectionCardWithComment(string fileStem, string id, string commentId, CardOwner addressedTo)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "Title", "open", CardOwner.Architect, CardScope.Change, string.Empty, Created, Created);
        var comment = new CardComment(commentId, CardOwner.Worker, Created, "please confirm", null, To: addressedTo, null, []);
        var card = new CardFile(frontmatter, "Body.", [comment], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private string WriteApprovedBlockCard(
        string fileStem, string id, string sectionId, int? round = null, IReadOnlyList<GateResult>? gateResults = null) =>
        WriteBlockCard(fileStem, id, sectionId, "approved", "reviewed-state", round, gateResults);

    private string WriteLandedBlockCard(string fileStem, string id, string sectionId) =>
        WriteBlockCard(fileStem, id, sectionId, "landed", "reviewed-state", round: null, gateResults: null);

    private string WriteBlockCardInState(string fileStem, string id, string sectionId, string status) =>
        WriteBlockCard(fileStem, id, sectionId, status, reviewedState: null, round: null, gateResults: null);

    private string WriteBlockCard(
        string fileStem, string id, string sectionId, string status, string? reviewedState, int? round, IReadOnlyList<GateResult>? gateResults)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "A block", status, CardOwner.Architect, CardScope.Change, sectionId, Created, Created);
        var blockFields = new BlockCardFields(
            Base: "base-commit", ReviewedState: reviewedState, Tasks: ["5.1"], Round: round, BlockedBy: [], GateResults: gateResults ?? []);
        // 8a.17, "Stored round agrees with the transition history" — CardStore now refuses to act
        // on a block card whose stored round disagrees with its own history, so a fixture asking
        // for round > 1 has to carry matching synthetic changes-requested transitions too.
        var transitions = round is > 1
            ? Enumerable.Range(0, round.Value - 1)
                .Select(_ => new CardBlockTransitionEntry(CardOwner.Reviewer, "changes-requested", BlockFlowState.InReview, BlockFlowState.Briefed, Created, []))
                .ToList()
            : [];
        var card = new CardFile(frontmatter, "Body.", [], [], [], blockFields, transitions, SectionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static CardSectionCloseOutcome.Closed AssertClosed(CardSectionCloseOutcome outcome) =>
        outcome.Match(
            onClosed: static closed => closed,
            onAlreadyClosed: static already => throw new Xunit.Sdk.XunitException($"expected Closed, got AlreadyClosed: '{already.FilePath}'"),
            onNotASectionCard: static n => throw new Xunit.Sdk.XunitException($"expected Closed, got NotASectionCard({n.Kind.ToWireString()})"),
            onBlockNotApproved: static notApproved => throw new Xunit.Sdk.XunitException(
                $"expected Closed, got BlockNotApproved({notApproved.BlockId}, {notApproved.ActualState})"),
            onBlockGateFailed: static failed => throw new Xunit.Sdk.XunitException(
                $"expected Closed, got BlockGateFailed({failed.BlockId}, {failed.GateLabel}={failed.ExitCode})"),
            onBlockGateAbsent: static absent => throw new Xunit.Sdk.XunitException(
                $"expected Closed, got BlockGateAbsent({absent.BlockId}, {absent.GateLabel})"),
            onOpenObligations: static o => throw new Xunit.Sdk.XunitException($"expected Closed, got OpenObligations({o.SectionId})"),
            onOpenUndeferredQuestion: static q => throw new Xunit.Sdk.XunitException($"expected Closed, got OpenUndeferredQuestion({q.QuestionId})"),
            onUnresolvedAddressedThread: static t => throw new Xunit.Sdk.XunitException($"expected Closed, got UnresolvedAddressedThread({t.CardId}, {string.Join(", ", t.ThreadIds)})"),
            onBlockedByOpenProductOwnerQuestion: static b => throw new Xunit.Sdk.XunitException($"expected Closed, got BlockedByOpenProductOwnerQuestion({b.BlockId}, {b.QuestionId})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Closed, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Closed, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Closed, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Closed, got ToolFailure: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: static disagreement => throw new Xunit.Sdk.XunitException($"expected Closed, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"),
            onHandEnteredDerivedState: static handEntered => throw new Xunit.Sdk.XunitException($"expected Closed, got HandEnteredDerivedState: '{handEntered.Key}'"));

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
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected write success, got ToolFailure: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: disagreement => throw new Xunit.Sdk.XunitException($"expected write success, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected write success, got HandEnteredDerivedState: '{handEntered.Key}'"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
