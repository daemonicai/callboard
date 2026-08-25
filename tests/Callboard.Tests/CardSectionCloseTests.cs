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
    public void CloseSection_AlreadyClosed_Refuses_AndDoesNotOverwriteTheFirstClosure()
    {
        var path = WriteInitialSectionCard("s-0002", "S-0002");
        AssertClosed(CardStore.CloseSection(_root, path, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName));

        var outcome = CardStore.CloseSection(_root, path, CardOwner.Supervisor, Created.AddDays(1), TimeSpan.FromSeconds(5), ChangeName);

        var already = Assert.IsType<CardSectionCloseOutcome.AlreadyClosed>(outcome);
        Assert.Equal(path, already.FilePath);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(CardOwner.Architect, read.SectionFields.ClosedBy);
        Assert.Equal(Created, read.SectionFields.ClosedAt);
    }

    [Fact]
    public void CloseSection_TargetIsNotASectionCard_Refuses()
    {
        var path = Path.Combine(_directory, "q-0001.md");
        var frontmatter = new CardFrontmatter(
            "Q-0001", CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Change, "5", Created, Created);
        AssertWriteSuccess(CardStore.WriteCard(_root, path, new NewCardFile(frontmatter, "Body."), TimeSpan.FromSeconds(5), ChangeName));

        var outcome = CardStore.CloseSection(_root, path, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notASection = Assert.IsType<CardSectionCloseOutcome.NotASectionCard>(outcome);
        Assert.Equal(CardKind.Question, notASection.Kind);
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

    // 8a.4 — any block not approved refuses the whole close, and leaves every card untouched.
    [Fact]
    public void CloseSection_ABlockNotApproved_RefusesTheWholeClose_LeavesEveryCardUntouched()
    {
        var sectionPath = WriteInitialSectionCard("s-0009", "S-0009");
        var approvedPath = WriteApprovedBlockCard("b-0005", "B-0005", "S-0009");
        var inReviewPath = WriteBlockCardInState("b-0006", "B-0006", "S-0009", "in-review");
        var sectionBytesBefore = File.ReadAllText(sectionPath);
        var approvedBytesBefore = File.ReadAllText(approvedPath);
        var inReviewBytesBefore = File.ReadAllText(inReviewPath);

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notApproved = Assert.IsType<CardSectionCloseOutcome.BlockNotApproved>(outcome);
        Assert.Equal("B-0006", notApproved.BlockId);
        Assert.Equal(inReviewPath, notApproved.BlockFilePath);
        Assert.Equal(BlockFlowState.InReview, notApproved.ActualState);

        Assert.Equal(sectionBytesBefore, File.ReadAllText(sectionPath));
        Assert.Equal(approvedBytesBefore, File.ReadAllText(approvedPath));
        Assert.Equal(inReviewBytesBefore, File.ReadAllText(inReviewPath));
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
    public void CloseSection_ABlockWithAFailingGate_Refuses()
    {
        var sectionPath = WriteInitialSectionCard("s-0011", "S-0011");
        var blockPath = WriteApprovedBlockCard("b-0008", "B-0008", "S-0011", gateResults: [new GateResult("build", 1, 1)]);

        var outcome = CardStore.CloseSection(_root, sectionPath, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var failed = Assert.IsType<CardSectionCloseOutcome.BlockGateFailed>(outcome);
        Assert.Equal("B-0008", failed.BlockId);
        Assert.Equal(blockPath, failed.BlockFilePath);
        Assert.Equal("build", failed.GateLabel);
        Assert.Equal(1, failed.ExitCode);
    }

    // 8a.6 — absent is a refusal in its own right, not a pass by default: a gate this block has
    // evidence for in an earlier round, with nothing recorded for the current round, still refuses.
    [Fact]
    public void CloseSection_ABlockWithAnAbsentGateThisRound_Refuses_NotAPassByDefault()
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

    private string WriteInitialSectionCard(string fileStem, string id)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "Title", "open", CardOwner.Architect, CardScope.Change, "5", Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
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
        var card = new CardFile(frontmatter, "Body.", [], [], [], blockFields, [], SectionCardFields.Empty);
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
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Closed, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Closed, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Closed, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Closed, got ToolFailure: {toolFailure.Reason}"));

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
