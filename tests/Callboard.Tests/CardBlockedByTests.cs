using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 5.7 — adding to and removing from a block card's <c>blocked_by</c> set under lock (§5 block D,
/// the same read-decide-write shape §5 block C's <see cref="CardStore.ApplyBlockTransition"/>
/// established). The owed proposition: blocking and unblocking a <c>building</c> card leaves its
/// <see cref="CardFrontmatter.Status"/> exactly <c>building</c> throughout — not something
/// unblocking has to restore, because nothing ever wrote it in the first place.
/// </summary>
public sealed class CardBlockedByTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-blocked-by-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;

    public CardBlockedByTests()
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

    // Owed evidence 2, in full: a building block is blocked, reports blocked while blocked_by is
    // non-empty, is unblocked, reports unblocked afterwards — status is `building` at every one of
    // these three checkpoints, asserted each time, not just at the end.
    [Fact]
    public void BlockingThenUnblocking_PreservesFlowState_Throughout()
    {
        var path = WriteInitialBlockCard("b-0001", "B-0001", BlockFlowState.Building);

        var beforeBlocking = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("building", beforeBlocking.Frontmatter.Status);
        Assert.False(beforeBlocking.BlockFields.BlockedBy.Length > 0);

        var added = AssertUpdated(CardStore.AddBlockedBy(_root, path, "Q-0001", CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName));
        Assert.Equal(CardOwner.Architect, added.ActingRole);

        var whileBlocked = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("building", whileBlocked.Frontmatter.Status);
        Assert.True(whileBlocked.BlockFields.BlockedBy.Length > 0);
        Assert.Equal(["Q-0001"], whileBlocked.BlockFields.BlockedBy);

        var removed = AssertUpdated(CardStore.RemoveBlockedBy(_root, path, "Q-0001", CardOwner.Reviewer, Created.AddHours(1), TimeSpan.FromSeconds(5), ChangeName));
        Assert.Equal(CardOwner.Reviewer, removed.ActingRole);

        var afterUnblocked = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("building", afterUnblocked.Frontmatter.Status);
        Assert.False(afterUnblocked.BlockFields.BlockedBy.Length > 0);
        Assert.Empty(afterUnblocked.BlockFields.BlockedBy);
    }

    // §9 remediation S1: CardBlockedByOutcome's four card-addressed post-lock cases (NotABlockCard,
    // RoundDisagreesWithHistory, AlreadyBlockedBy, NotBlockedBy) now implement ICardRefusalReason and
    // record — so a refusal here no longer leaves the card byte-identical, it appends exactly one
    // CardRefusalEntry.
    [Fact]
    public void AddBlockedBy_AlreadyPresent_Refuses_AndRecordsTheRefusal()
    {
        var path = WriteInitialBlockCard("b-0002", "B-0002", BlockFlowState.Building);
        AssertUpdated(CardStore.AddBlockedBy(_root, path, "Q-0001", CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName));

        var outcome = CardStore.AddBlockedBy(_root, path, "Q-0001", CardOwner.Architect, Created.AddHours(1), TimeSpan.FromSeconds(5), ChangeName);

        var already = Assert.IsType<CardBlockedByOutcome.AlreadyBlockedBy>(outcome);
        Assert.Equal("Q-0001", already.BlockingCardId);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.Equal(already.RefusingRule, recorded.Rule);
        Assert.Equal(already.Remedy, recorded.Remedy);
        Assert.Equal(["Q-0001"], read.BlockFields.BlockedBy);
    }

    [Fact]
    public void RemoveBlockedBy_NotPresent_Refuses_AndRecordsTheRefusal()
    {
        var path = WriteInitialBlockCard("b-0003", "B-0003", BlockFlowState.Building);

        var outcome = CardStore.RemoveBlockedBy(_root, path, "Q-0001", CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notBlockedBy = Assert.IsType<CardBlockedByOutcome.NotBlockedBy>(outcome);
        Assert.Equal("Q-0001", notBlockedBy.BlockingCardId);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.Equal(notBlockedBy.RefusingRule, recorded.Rule);
        Assert.Equal(notBlockedBy.Remedy, recorded.Remedy);
        Assert.Empty(read.BlockFields.BlockedBy);
    }

    [Fact]
    public void AddBlockedBy_TargetIsNotABlockCard_Refuses_AndRecordsTheRefusal()
    {
        var path = Path.Combine(_directory, "q-0001.md");
        var frontmatter = new CardFrontmatter(
            "Q-0001", CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Change, "5", Created, Created);
        AssertWriteSuccess(CardStore.WriteCard(_root, path, new NewCardFile(frontmatter, "Body."), TimeSpan.FromSeconds(5), ChangeName));

        var outcome = CardStore.AddBlockedBy(_root, path, "Q-0002", CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notABlock = Assert.IsType<CardBlockedByOutcome.NotABlockCard>(outcome);
        Assert.Equal(CardKind.Question, notABlock.Kind);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.Equal(notABlock.RefusingRule, recorded.Rule);
        Assert.Equal(notABlock.Remedy, recorded.Remedy);
    }

    // work-lifecycle: "Stored round agrees with the transition history" (8a.17), applied to
    // CardBlockedByOutcome (§9 remediation S1).
    [Fact]
    public void AddBlockedBy_RoundDisagreesWithHistory_Refuses_NamesBothFigures_AndRecordsTheRefusal()
    {
        var path = Path.Combine(_directory, "b-0006.md");
        var frontmatter = new CardFrontmatter(
            "B-0006", CardKind.Block, "Title", BlockFlowState.Building.ToWireString(), CardOwner.Worker, CardScope.Change, "5", Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty with { Round = 3 }, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.AddBlockedBy(_root, path, "Q-0001", CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var disagreement = Assert.IsType<CardBlockedByOutcome.RoundDisagreesWithHistory>(outcome);
        Assert.Equal(3, disagreement.StoredRound);
        Assert.Equal(1, disagreement.ExpectedRound);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.Equal(disagreement.RefusingRule, recorded.Rule);
        Assert.Equal(disagreement.Remedy, recorded.Remedy);
        Assert.Empty(read.BlockFields.BlockedBy);
    }

    [Fact]
    public void AddBlockedBy_WhenNoCardExistsAtThatPath_Fails()
    {
        var path = Path.Combine(_directory, "missing.md");

        var outcome = CardStore.AddBlockedBy(_root, path, "Q-0001", CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notFound = Assert.IsType<CardBlockedByOutcome.CardNotFound>(outcome);
        Assert.Equal(path, notFound.FilePath);
    }

    [Fact]
    public void AddBlockedBy_LayoutMismatch_ReturnsLayoutMismatch()
    {
        var path = WriteInitialBlockCard("b-0004", "B-0004", BlockFlowState.Building);

        var outcome = CardStore.AddBlockedBy(_root, path, "Q-0001", CardOwner.Architect, Created, TimeSpan.FromSeconds(5), "a-different-change");

        Assert.IsType<CardBlockedByOutcome.LayoutMismatch>(outcome);
    }

    [Fact]
    public void AddBlockedBy_WhenTheCardFileIsCorrupt_ReturnsCardCorrupt_NotARefusalShapedOutcome()
    {
        var path = Path.Combine(_directory, "corrupt.md");
        File.WriteAllText(path, "not a card file at all");

        var outcome = CardStore.AddBlockedBy(_root, path, "Q-0001", CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var corrupt = Assert.IsType<CardBlockedByOutcome.CardCorrupt>(outcome);
        Assert.Equal(path, corrupt.FilePath);
    }

    [Fact]
    public void AddBlockedBy_WhenTheLockIsHeldByAnotherCaller_ReturnsToolFailure_NotARefusalShapedOutcome()
    {
        var path = WriteInitialBlockCard("b-0005", "B-0005", BlockFlowState.Building);
        var holder = AssertAcquired(CardLock.Acquire(path, TimeSpan.FromSeconds(5)));

        try
        {
            var outcome = CardStore.AddBlockedBy(_root, path, "Q-0001", CardOwner.Architect, Created, TimeSpan.FromMilliseconds(200), ChangeName);

            Assert.IsType<CardBlockedByOutcome.ToolFailure>(outcome);
        }
        finally
        {
            holder.Dispose();
        }
    }

    private string WriteInitialBlockCard(string fileStem, string id, BlockFlowState status)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", status.ToWireString(), CardOwner.Worker, CardScope.Change, "5", Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static CardBlockedByOutcome.Updated AssertUpdated(CardBlockedByOutcome outcome) =>
        outcome.Match(
            onUpdated: static updated => updated,
            onAlreadyBlockedBy: static a => throw new Xunit.Sdk.XunitException($"expected Updated, got AlreadyBlockedBy({a.BlockingCardId})"),
            onNotBlockedBy: static n => throw new Xunit.Sdk.XunitException($"expected Updated, got NotBlockedBy({n.BlockingCardId})"),
            onNotABlockCard: static n => throw new Xunit.Sdk.XunitException($"expected Updated, got NotABlockCard({n.Kind.ToWireString()})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Updated, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Updated, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Updated, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Updated, got ToolFailure: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: static disagreement => throw new Xunit.Sdk.XunitException($"expected Updated, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"),
            onHandEnteredDerivedState: static handEntered => throw new Xunit.Sdk.XunitException($"expected Updated, got HandEnteredDerivedState: '{handEntered.Key}'"));

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
