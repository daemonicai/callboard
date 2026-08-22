using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 5.6 — recording a gate result under lock (§5 block D, the same read-decide-write shape §5
/// block C's <see cref="CardStore.ApplyBlockTransition"/> established). The owed proposition: a
/// narrative comment claiming a gate passed cannot make <see cref="BlockCardFields.GateStatusOf"/>
/// report anything other than <see cref="GateStatus.Absent"/> for that label — proven here
/// structurally, by appending such a comment and reading the card back, not by inspecting the
/// comment's own text.
/// </summary>
public sealed class CardGateResultTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-gate-result-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;

    public CardGateResultTests()
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
    public void RecordGateResult_FirstRecording_RecordsLabelAndExitCode()
    {
        var path = WriteInitialBlockCard("b-0001", "B-0001");

        var outcome = CardStore.RecordGateResult(_root, path, "build", 0, CardOwner.Worker, Created, TimeSpan.FromSeconds(5), ChangeName);

        var recorded = AssertRecorded(outcome);
        Assert.Equal("build", recorded.Result.Label);
        Assert.Equal(0, recorded.Result.ExitCode);
        Assert.Equal(1, recorded.Result.Round);
        Assert.Equal(CardOwner.Worker, recorded.ActingRole);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.True(read.BlockFields.GateStatusOf("build").Passed);
        Assert.Equal(Created, read.Frontmatter.Updated);
    }

    // Owed evidence 1: absent is a different answer from failed — a gate never recorded reports
    // Absent, not "failed".
    [Fact]
    public void GateStatusOf_LabelNeverRecorded_ReportsAbsent_NotFailed()
    {
        var path = WriteInitialBlockCard("b-0002", "B-0002");
        var read = AssertParseSuccess(CardStore.ReadCard(path));

        var status = read.BlockFields.GateStatusOf("build");

        Assert.Same(GateStatus.Absent, status);
        Assert.False(status.Passed);
    }

    // The proposition this test exists to falsify: a comment body being readable as evidence.
    // What would have to break for this to go red — GateStatusOf reading Comments instead of
    // (or in addition to) GateResults, or AppendComment routing its argument onto BlockFields.
    [Fact]
    public void NarrativeCommentClaimingAGatePassed_LeavesThatGateAbsent_AssertedOnTheCardNotTheComment()
    {
        var path = WriteInitialBlockCard("b-0003", "B-0003");
        var comment = new CardComment(
            "C-0001", CardOwner.Worker, Created, "build passed, all green.", null, null, null, []);

        AssertWriteSuccess(CardStore.AppendComment(_root, path, comment, TimeSpan.FromSeconds(5), ChangeName));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Single(read.Comments);
        Assert.Same(GateStatus.Absent, read.BlockFields.GateStatusOf("build"));
        Assert.False(read.BlockFields.GateStatusOf("build").Passed);
        Assert.Empty(read.BlockFields.GateResults);
    }

    // A second recording for the same label replaces it — the label is upserted, not appended a
    // second time (BlockCardFields would otherwise refuse a duplicate label at construction) —
    // as long as it is the *same round*. See RecordGateResult_ForADifferentRound_... below for the
    // other half (§5 remediation, DEVLOG §5 finding B2).
    [Fact]
    public void RecordGateResult_SecondRecordingForSameLabelInTheSameRound_ReplacesTheFirst()
    {
        var path = WriteInitialBlockCard("b-0004", "B-0004");

        AssertRecorded(CardStore.RecordGateResult(_root, path, "build", 1, CardOwner.Worker, Created, TimeSpan.FromSeconds(5), ChangeName));
        AssertRecorded(CardStore.RecordGateResult(_root, path, "build", 0, CardOwner.Worker, Created.AddMinutes(1), TimeSpan.FromSeconds(5), ChangeName));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var only = Assert.Single(read.BlockFields.GateResults);
        Assert.Equal("build", only.Label);
        Assert.Equal(0, only.ExitCode);
        Assert.Equal(1, only.Round);
    }

    /// <summary>
    /// The proposition B2 exists to prove: a passing gate recorded in round 1 does not go on
    /// counting as evidence once the block has moved to round 2, but it is not destroyed either.
    /// What would have to break for this to go red — <see cref="CardStore.
    /// RecordGateResultUnderExistingLock"/> upserting by label alone again (losing round 1's
    /// entry), or <see cref="BlockCardFields.GateStatusOf"/> ignoring <see cref="GateResult.
    /// Round"/> and reporting round 1's stale pass as current.
    /// </summary>
    [Fact]
    public void RecordGateResult_ForADifferentRound_RetainsTheEarlierRoundsEntry_ButOnlyTheCurrentRoundIsEvidence()
    {
        var path = WriteInitialBlockCard("b-0007", "B-0007");

        AssertRecorded(CardStore.RecordGateResult(_root, path, "build", 0, CardOwner.Worker, Created, TimeSpan.FromSeconds(5), ChangeName));
        Assert.True(AssertParseSuccess(CardStore.ReadCard(path)).BlockFields.GateStatusOf("build").Passed);

        SetRoundOnDisk(path, round: 2);

        AssertRecorded(CardStore.RecordGateResult(_root, path, "build", 1, CardOwner.Worker, Created.AddMinutes(5), TimeSpan.FromSeconds(5), ChangeName));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(2, read.BlockFields.GateResults.Length);
        Assert.Contains(read.BlockFields.GateResults, result => result.Round == 1 && result.ExitCode == 0);
        Assert.Contains(read.BlockFields.GateResults, result => result.Round == 2 && result.ExitCode == 1);

        // Round 1's passing build is still on the card (retained, not destroyed) but no longer
        // counts as evidence now that round 2 is current — B2's whole point.
        Assert.False(read.BlockFields.GateStatusOf("build").Passed);
    }

    [Fact]
    public void RecordGateResult_TargetIsNotABlockCard_Refuses()
    {
        var path = Path.Combine(_directory, "q-0001.md");
        var frontmatter = new CardFrontmatter(
            "Q-0001", CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Change, "5", Created, Created);
        AssertWriteSuccess(CardStore.WriteCard(_root, path, new NewCardFile(frontmatter, "Body."), TimeSpan.FromSeconds(5), ChangeName));

        var outcome = CardStore.RecordGateResult(_root, path, "build", 0, CardOwner.Worker, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notABlock = Assert.IsType<CardGateResultOutcome.NotABlockCard>(outcome);
        Assert.Equal(CardKind.Question, notABlock.Kind);
    }

    [Fact]
    public void RecordGateResult_WhenNoCardExistsAtThatPath_Fails()
    {
        var path = Path.Combine(_directory, "missing.md");

        var outcome = CardStore.RecordGateResult(_root, path, "build", 0, CardOwner.Worker, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notFound = Assert.IsType<CardGateResultOutcome.CardNotFound>(outcome);
        Assert.Equal(path, notFound.FilePath);
    }

    [Fact]
    public void RecordGateResult_LayoutMismatch_ReturnsLayoutMismatch_NotCardNotFound()
    {
        var path = WriteInitialBlockCard("b-0005", "B-0005");

        var outcome = CardStore.RecordGateResult(_root, path, "build", 0, CardOwner.Worker, Created, TimeSpan.FromSeconds(5), "a-different-change");

        Assert.IsType<CardGateResultOutcome.LayoutMismatch>(outcome);
    }

    [Fact]
    public void RecordGateResult_WhenTheCardFileIsCorrupt_ReturnsCardCorrupt_NotARefusalShapedOutcome()
    {
        var path = Path.Combine(_directory, "corrupt.md");
        File.WriteAllText(path, "not a card file at all");

        var outcome = CardStore.RecordGateResult(_root, path, "build", 0, CardOwner.Worker, Created, TimeSpan.FromSeconds(5), ChangeName);

        var corrupt = Assert.IsType<CardGateResultOutcome.CardCorrupt>(outcome);
        Assert.Equal(path, corrupt.FilePath);
    }

    [Fact]
    public void RecordGateResult_WhenTheLockIsHeldByAnotherCaller_ReturnsToolFailure_NotARefusalShapedOutcome()
    {
        var path = WriteInitialBlockCard("b-0006", "B-0006");
        var holder = AssertAcquired(CardLock.Acquire(path, TimeSpan.FromSeconds(5)));

        try
        {
            var outcome = CardStore.RecordGateResult(_root, path, "build", 0, CardOwner.Worker, Created, TimeSpan.FromMilliseconds(200), ChangeName);

            Assert.IsType<CardGateResultOutcome.ToolFailure>(outcome);
        }
        finally
        {
            holder.Dispose();
        }
    }

    /// <summary>Writes <paramref name="round"/> directly onto the card's <c>round</c> field via
    /// the production serializer — standing in for the round bump <see cref="CardStore.
    /// ApplyBlockTransitionUnderExistingLock"/> would perform on <c>changes-requested</c>, without
    /// this file needing to drive the whole flow-transition machinery just to get a block onto its
    /// second round.</summary>
    private void SetRoundOnDisk(string path, int round)
    {
        var card = AssertParseSuccess(CardStore.ReadCard(path));
        var updated = card with { BlockFields = card.BlockFields with { Round = round } };
        File.WriteAllText(path, CardFileWriter.Serialize(updated), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private string WriteInitialBlockCard(string fileStem, string id)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", "building", CardOwner.Worker, CardScope.Change, "5", Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static CardGateResultOutcome.Recorded AssertRecorded(CardGateResultOutcome outcome) =>
        outcome.Match(
            onRecorded: static recorded => recorded,
            onNotABlockCard: static n => throw new Xunit.Sdk.XunitException($"expected Recorded, got NotABlockCard({n.Kind.ToWireString()})"),
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
