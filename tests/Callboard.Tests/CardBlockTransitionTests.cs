using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 5.2/5.3/5.5 — applying a block flow transition under lock (§5 block C, the first verb whose
/// side effect writes a card). Every transition records the acting role and timestamp
/// (work-lifecycle: "Every transition SHALL record the acting role and the time it occurred") as
/// an appended <see cref="CardBlockTransitionEntry"/>; an undefined transition is refused, naming
/// the transitions available from the card's current state by reading
/// <see cref="BlockFlowTransitions.AvailableFrom"/> directly rather than a second hand-maintained
/// list; a remediation round returns the same card to <c>briefed</c> with <c>round</c> incremented
/// and ticks no task; <c>base</c> must be recorded before a block reaches <c>briefed</c> and can
/// never change once recorded.
/// </summary>
public sealed class CardBlockTransitionTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-block-transition-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;

    public CardBlockTransitionTests()
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
    public void ApplyBlockTransition_LegalTransition_ChangesStatus_AndRecordsActingRoleAndTimestamp()
    {
        var path = WriteInitialBlockCard("b-0001", "B-0001", BlockFlowState.Briefed, baseCommit: "abc123");
        var when = Created.AddHours(1);

        var outcome = CardStore.ApplyBlockTransition(_root, path, "claim", CardOwner.Worker, when, baseCommit: null, TimeSpan.FromSeconds(5), ChangeName);

        var applied = AssertApplied(outcome);
        Assert.Equal("claim", applied.Transition.Name);
        Assert.Equal(BlockFlowState.Briefed, applied.Transition.From);
        Assert.Equal(BlockFlowState.Building, applied.Transition.To);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("building", read.Frontmatter.Status);
        Assert.Equal(when, read.Frontmatter.Updated);

        var entry = Assert.Single(read.Transitions);
        Assert.Equal(CardOwner.Worker, entry.By);
        Assert.Equal("claim", entry.Name);
        Assert.Equal(BlockFlowState.Briefed, entry.From);
        Assert.Equal(BlockFlowState.Building, entry.To);
        Assert.Equal(when, entry.Timestamp);
    }

    // Owed evidence 1: a refusal of an undefined transition names the available ones, read from
    // BlockFlowTransitions.AvailableFrom — not a second hand-maintained list.
    [Fact]
    public void ApplyBlockTransition_UndefinedTransition_NamesTheTransitionsAvailableFromCurrentState()
    {
        var path = WriteInitialBlockCard("b-0002", "B-0002", BlockFlowState.Drafting);

        var outcome = CardStore.ApplyBlockTransition(_root, path, "approve", CardOwner.Architect, Created, baseCommit: null, TimeSpan.FromSeconds(5), ChangeName);

        var undefined = outcome.Match(
            onApplied: static applied => throw new Xunit.Sdk.XunitException($"expected UndefinedTransition, got Applied({applied.Transition.Name})"),
            onUndefinedTransition: static u => u,
            onBaseNotRecorded: static _ => throw new Xunit.Sdk.XunitException("expected UndefinedTransition, got BaseNotRecorded"),
            onBaseImmutable: static _ => throw new Xunit.Sdk.XunitException("expected UndefinedTransition, got BaseImmutable"),
            onUndispositionedNits: static _ => throw new Xunit.Sdk.XunitException("expected UndefinedTransition, got UndispositionedNits"),
            onNotABlockCard: static _ => throw new Xunit.Sdk.XunitException("expected UndefinedTransition, got NotABlockCard"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected UndefinedTransition, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected UndefinedTransition, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected UndefinedTransition, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected UndefinedTransition, got ToolFailure: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: static disagreement => throw new Xunit.Sdk.XunitException($"expected UndefinedTransition, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"),
            onUnresolvedThreadsAddressedToActor: static unresolved => throw new Xunit.Sdk.XunitException($"expected UndefinedTransition, got UnresolvedThreadsAddressedToActor({string.Join(", ", unresolved.ThreadIds)})"),
            onBlockedByOpenProductOwnerQuestion: static blocked => throw new Xunit.Sdk.XunitException($"expected UndefinedTransition, got BlockedByOpenProductOwnerQuestion({blocked.QuestionId})"));

        Assert.Equal(BlockFlowState.Drafting, undefined.CurrentState);
        var available = Assert.Single(undefined.Available);
        Assert.Equal("brief", available.Name);
    }

    // Owed evidence 3, for the undefined-transition refusal specifically: process-enforcement
    // (§9 block A) now records the refusal against the card, so the file is no longer
    // byte-identical — the assertion moves to "gained exactly one CardRefusalEntry naming this
    // rule, and nothing else changed", which is the stronger of the two claims the old
    // byte-identical assertion was standing in for.
    [Fact]
    public void ApplyBlockTransition_UndefinedTransition_RecordsExactlyOneRefusal_AndChangesNothingElse()
    {
        var path = WriteInitialBlockCard("b-0003", "B-0003", BlockFlowState.Drafting);
        var before = AssertParseSuccess(CardStore.ReadCard(path));

        var outcome = CardStore.ApplyBlockTransition(_root, path, "land", CardOwner.Architect, Created, baseCommit: null, TimeSpan.FromSeconds(5), ChangeName);

        var undefined = Assert.IsType<CardBlockTransitionOutcome.UndefinedTransition>(outcome);
        var after = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(after.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(undefined.RefusingRule, refusal.Rule);
        Assert.Equal(undefined.Remedy, refusal.Remedy);
        Assert.Equal(Created, refusal.Timestamp);
        Assert.Equal(before.Frontmatter, after.Frontmatter);
        Assert.Equal(before.Transitions, after.Transitions);
        Assert.Equal(before.BlockFields, after.BlockFields);
    }

    // Owed evidence 2, first half: refuse briefing a block with no base recorded.
    [Fact]
    public void ApplyBlockTransition_BriefWithNoBaseRecordedAndNoneSupplied_Refuses()
    {
        var path = WriteInitialBlockCard("b-0004", "B-0004", BlockFlowState.Drafting);

        var outcome = CardStore.ApplyBlockTransition(_root, path, "brief", CardOwner.Architect, Created, baseCommit: null, TimeSpan.FromSeconds(5), ChangeName);

        var baseNotRecorded = Assert.IsType<CardBlockTransitionOutcome.BaseNotRecorded>(outcome);
        var after = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(after.Refusals);
        Assert.Equal(baseNotRecorded.RefusingRule, refusal.Rule);
        Assert.Equal(baseNotRecorded.Remedy, refusal.Remedy);
        Assert.Equal("drafting", after.Frontmatter.Status);
        Assert.Null(after.BlockFields.Base);
    }

    [Fact]
    public void ApplyBlockTransition_BriefWithBaseSupplied_RecordsBase_AndStartsRoundAtOne()
    {
        var path = WriteInitialBlockCard("b-0005", "B-0005", BlockFlowState.Drafting);

        var outcome = CardStore.ApplyBlockTransition(_root, path, "brief", CardOwner.Architect, Created, baseCommit: "commit-abc", TimeSpan.FromSeconds(5), ChangeName);

        AssertApplied(outcome);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("commit-abc", read.BlockFields.Base);
        Assert.Equal(1, read.BlockFields.Round);
        Assert.Equal("briefed", read.Frontmatter.Status);
    }

    // Owed evidence 2, second half: base cannot change across rounds — driven through a full
    // brief -> claim -> submit-for-review -> changes-requested cycle, the same one work-lifecycle's
    // own scenario ("reviewer requests changes on a block at round 1") describes.
    [Fact]
    public void ApplyBlockTransition_BaseCannotChangeAcrossRemediationRounds()
    {
        var path = WriteInitialBlockCard("b-0006", "B-0006", BlockFlowState.Drafting);

        AssertApplied(CardStore.ApplyBlockTransition(_root, path, "brief", CardOwner.Architect, Created, "commit-abc", TimeSpan.FromSeconds(5), ChangeName));
        AssertApplied(CardStore.ApplyBlockTransition(_root, path, "claim", CardOwner.Worker, Created.AddHours(1), null, TimeSpan.FromSeconds(5), ChangeName));
        AssertApplied(CardStore.ApplyBlockTransition(_root, path, "submit-for-review", CardOwner.Worker, Created.AddHours(2), null, TimeSpan.FromSeconds(5), ChangeName));

        // Supplying the same base again is not a change and must not be refused.
        AssertApplied(CardStore.ApplyBlockTransition(
            _root, path, "changes-requested", CardOwner.Reviewer, Created.AddHours(3), "commit-abc", TimeSpan.FromSeconds(5), ChangeName));

        var afterRound1 = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("commit-abc", afterRound1.BlockFields.Base);
        Assert.Equal(2, afterRound1.BlockFields.Round);
        Assert.Equal("briefed", afterRound1.Frontmatter.Status);

        // A different base is refused, and its base/round/status are untouched — base still
        // "commit-abc" — but the refusal itself is now recorded against the card (§9 block A).
        var refused = CardStore.ApplyBlockTransition(
            _root, path, "claim", CardOwner.Worker, Created.AddHours(4), "a-different-commit", TimeSpan.FromSeconds(5), ChangeName);

        var immutable = Assert.IsType<CardBlockTransitionOutcome.BaseImmutable>(refused);
        Assert.Equal("commit-abc", immutable.Recorded);
        Assert.Equal("a-different-commit", immutable.Attempted);

        var stillRecorded = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("commit-abc", stillRecorded.BlockFields.Base);
        Assert.Equal(2, stillRecorded.BlockFields.Round);
        Assert.Equal("briefed", stillRecorded.Frontmatter.Status);
        var refusal = Assert.Single(stillRecorded.Refusals);
        Assert.Equal(CardOwner.Worker, refusal.By);
        Assert.Equal(immutable.RefusingRule, refusal.Rule);
        Assert.Equal(immutable.Remedy, refusal.Remedy);
    }

    // 5.3 / §8a block A (work-lifecycle: "Reviewer remediation is the same card at a higher
    // round" — "This governs the block-level review loop only"): remediation is the same card at
    // an incremented round, on the same identity, ticks no task — there is no task-completion
    // field on BlockCardFields at all for this to flip, so this asserts the one thing that could
    // otherwise silently regress: Tasks survives unchanged — and it creates no second card, the
    // §8a block A boundary a reviewer's own changes-requested must not cross into.
    [Fact]
    public void ApplyBlockTransition_ChangesRequested_ReturnsToBriefed_IncrementsRound_LeavesTasksUntouched_AndCreatesNoCard()
    {
        var path = WriteInitialBlockCard("b-0007", "B-0007", BlockFlowState.Drafting, tasks: ["5.2", "5.3", "5.5"]);
        var originalId = AssertParseSuccess(CardStore.ReadCard(path)).Frontmatter.Id;
        var cardFilesBefore = Directory.EnumerateFiles(_directory, "*.md").ToArray();

        AssertApplied(CardStore.ApplyBlockTransition(_root, path, "brief", CardOwner.Architect, Created, "commit-abc", TimeSpan.FromSeconds(5), ChangeName));
        AssertApplied(CardStore.ApplyBlockTransition(_root, path, "claim", CardOwner.Worker, Created.AddHours(1), null, TimeSpan.FromSeconds(5), ChangeName));
        AssertApplied(CardStore.ApplyBlockTransition(_root, path, "submit-for-review", CardOwner.Worker, Created.AddHours(2), null, TimeSpan.FromSeconds(5), ChangeName));

        var outcome = CardStore.ApplyBlockTransition(
            _root, path, "changes-requested", CardOwner.Reviewer, Created.AddHours(3), null, TimeSpan.FromSeconds(5), ChangeName);

        AssertApplied(outcome);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", read.Frontmatter.Status);
        Assert.Equal(2, read.BlockFields.Round);
        Assert.Equal(originalId, read.Frontmatter.Id);
        Assert.Equal(["5.2", "5.3", "5.5"], read.BlockFields.Tasks);
        Assert.Equal(4, read.Transitions.Count);

        // §8a block A: no second card was created as a side effect of the reviewer's own return.
        Assert.Equal(cardFilesBefore, Directory.EnumerateFiles(_directory, "*.md").ToArray());
    }

    [Fact]
    public void ApplyBlockTransition_TargetIsNotABlockCard_Refuses()
    {
        var path = Path.Combine(_directory, "q-0001.md");
        var frontmatter = new CardFrontmatter(
            "Q-0001", CardKind.Question, "A question", "open", CardOwner.Architect, CardScope.Change, "5", Created, Created);
        AssertWriteSuccess(CardStore.WriteCard(_root, path, new NewCardFile(frontmatter, "Body."), TimeSpan.FromSeconds(5), ChangeName));

        var outcome = CardStore.ApplyBlockTransition(_root, path, "brief", CardOwner.Architect, Created, "commit-abc", TimeSpan.FromSeconds(5), ChangeName);

        var notABlock = Assert.IsType<CardBlockTransitionOutcome.NotABlockCard>(outcome);
        Assert.Equal(CardKind.Question, notABlock.Kind);

        // 9.10 coverage gate: the outcome type alone was previously asserted here, never the
        // recorded CardRefusalEntry (§9 block C finding).
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(notABlock.RefusingRule, refusal.Rule);
        Assert.Equal(notABlock.Remedy, refusal.Remedy);
    }

    [Fact]
    public void ApplyBlockTransition_WhenNoCardExistsAtThatPath_Fails()
    {
        var path = Path.Combine(_directory, "missing.md");

        var outcome = CardStore.ApplyBlockTransition(_root, path, "brief", CardOwner.Architect, Created, "commit-abc", TimeSpan.FromSeconds(5), ChangeName);

        var notFound = Assert.IsType<CardBlockTransitionOutcome.CardNotFound>(outcome);
        Assert.Equal(path, notFound.FilePath);
    }

    // Reviewer finding (first remediation round): CardNotFound/LayoutMismatch are refusal-shaped
    // (caller-correctable); CardCorrupt/ToolFailure are not. One test per branch below proves the
    // disposition is the right one — not just that a failure of some kind occurred.
    [Fact]
    public void ApplyBlockTransition_WhenThePathDoesNotLiveInTheDeclaredChangesDirectory_ReturnsLayoutMismatch_NotARefusalToBeConfusedWithCardNotFound()
    {
        var path = WriteInitialBlockCard("b-0008", "B-0008", BlockFlowState.Drafting);

        var outcome = CardStore.ApplyBlockTransition(
            _root, path, "brief", CardOwner.Architect, Created, "commit-abc", TimeSpan.FromSeconds(5), "a-different-change");

        var mismatch = Assert.IsType<CardBlockTransitionOutcome.LayoutMismatch>(outcome);
        Assert.Contains("does not live in the directory", mismatch.Reason, StringComparison.Ordinal);
    }

    // Neither refusal-shaped: a corrupt card is a reported problem with the record's content, not
    // the caller being wrong (record-retrieval's degraded-mode requirement).
    [Fact]
    public void ApplyBlockTransition_WhenTheCardFileIsCorrupt_ReturnsCardCorrupt_NotARefusalShapedOutcome()
    {
        var path = Path.Combine(_directory, "corrupt.md");
        File.WriteAllText(path, "not a card file at all");

        var outcome = CardStore.ApplyBlockTransition(_root, path, "brief", CardOwner.Architect, Created, "commit-abc", TimeSpan.FromSeconds(5), ChangeName);

        var corrupt = Assert.IsType<CardBlockTransitionOutcome.CardCorrupt>(outcome);
        Assert.Equal(path, corrupt.FilePath);
        Assert.Equal("not a card file at all", File.ReadAllText(path));
    }

    // Neither refusal-shaped: enforcement itself is unavailable (the lock is held by another
    // caller), not the caller being wrong.
    [Fact]
    public void ApplyBlockTransition_WhenTheLockIsHeldByAnotherCaller_ReturnsToolFailure_NotARefusalShapedOutcome()
    {
        var path = WriteInitialBlockCard("b-0009", "B-0009", BlockFlowState.Drafting);
        var holder = AssertAcquired(CardLock.Acquire(path, TimeSpan.FromSeconds(5)));

        try
        {
            var outcome = CardStore.ApplyBlockTransition(
                _root, path, "brief", CardOwner.Architect, Created, "commit-abc", TimeSpan.FromMilliseconds(200), ChangeName);

            Assert.IsType<CardBlockTransitionOutcome.ToolFailure>(outcome);
        }
        finally
        {
            holder.Dispose();
        }
    }

    // Written directly via CardFileWriter, not CardStore.WriteCard — this is the only way to set
    // up a card that already has base/tasks recorded, since WriteCard's own NewCardFile input
    // carries no BlockCardFields at all (§4 remediation R3): the only production path that can set
    // one is CardStore.ApplyBlockTransition itself, which is what the tests above exercise.
    private string WriteInitialBlockCard(string fileStem, string id, BlockFlowState status, string? baseCommit = null, IReadOnlyList<string>? tasks = null)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", status.ToWireString(), CardOwner.Architect, CardScope.Change, "5", Created, Created);
        var blockFields = new BlockCardFields(baseCommit, null, tasks ?? [], null, [], []);
        var card = new CardFile(frontmatter, "Body.", [], [], [], blockFields, []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static CardBlockTransitionOutcome.Applied AssertApplied(CardBlockTransitionOutcome outcome) =>
        outcome.Match(
            onApplied: static applied => applied,
            onUndefinedTransition: static u => throw new Xunit.Sdk.XunitException(
                $"expected Applied, got UndefinedTransition from '{u.CurrentState.ToWireString()}' (available: {string.Join(", ", u.Available.Select(t => t.Name))})"),
            onBaseNotRecorded: static _ => throw new Xunit.Sdk.XunitException("expected Applied, got BaseNotRecorded"),
            onBaseImmutable: static i => throw new Xunit.Sdk.XunitException($"expected Applied, got BaseImmutable(recorded: {i.Recorded}, attempted: {i.Attempted})"),
            onUndispositionedNits: static u => throw new Xunit.Sdk.XunitException($"expected Applied, got UndispositionedNits({string.Join(", ", u.NitIds)})"),
            onNotABlockCard: static n => throw new Xunit.Sdk.XunitException($"expected Applied, got NotABlockCard({n.Kind.ToWireString()})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Applied, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Applied, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Applied, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Applied, got ToolFailure: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: static disagreement => throw new Xunit.Sdk.XunitException($"expected Applied, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"),
            onUnresolvedThreadsAddressedToActor: static unresolved => throw new Xunit.Sdk.XunitException($"expected Applied, got UnresolvedThreadsAddressedToActor({string.Join(", ", unresolved.ThreadIds)})"),
            onBlockedByOpenProductOwnerQuestion: static blocked => throw new Xunit.Sdk.XunitException($"expected Applied, got BlockedByOpenProductOwnerQuestion({blocked.QuestionId})"));

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
            onRoundDisagreesWithHistory: disagreement => throw new Xunit.Sdk.XunitException($"expected write success, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
