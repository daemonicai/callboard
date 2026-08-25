using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §5 remediation (DEVLOG §5, supervisor's suggested remediation shape, item 4): §5's tests all
/// prove one edge or one field in isolation — none drives a block card through its whole flow and
/// asserts what it carries at each step, composed. Finding B2 is exactly what such a test would
/// have gone red on: <see cref="CardStore.RecordGateResult"/> upserting by label alone, with
/// <c>changes-requested</c> never touching <see cref="BlockCardFields.GateResults"/>, meant a
/// block returning to round 2 carried round 1's gate evidence verbatim. This test drives one card
/// through <c>drafting → briefed → building → in-review → changes-requested → briefed → building →
/// in-review → approved → landed → closed</c> and asserts <c>round</c>, <c>base</c> immutability,
/// gate results, <c>blocked_by</c> and flow state at every step of that run, not just the edges
/// individually already covered elsewhere.
/// </summary>
public sealed class BlockLifecycleIntegrationTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";
    private const string Base = "commit-abc";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-block-lifecycle-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;
    private readonly string _path;
    private readonly string _sectionPath;

    public BlockLifecycleIntegrationTests()
    {
        _directory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_directory);

        var frontmatter = new CardFrontmatter(
            "B-0900", CardKind.Block, "Whole-lifecycle proof", BlockFlowState.Drafting.ToWireString(),
            CardOwner.Architect, CardScope.Change, "S-0900", T0, T0);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, []);
        File.WriteAllText(_path = Path.Combine(_directory, "b-0900.md"), CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        // §8a block A: landing is section-driven — the section this block belongs to, so the run's
        // own "land" step below can go through CloseSection, the one door that remains.
        var sectionFrontmatter = new CardFrontmatter(
            "S-0900", CardKind.Section, "Whole-lifecycle proof's section", SectionFlowState.Open.ToWireString(),
            CardOwner.Architect, CardScope.Change, string.Empty, T0, T0);
        var sectionCard = new CardFile(sectionFrontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
        File.WriteAllText(_sectionPath = Path.Combine(_directory, "s-0900.md"), CardFileWriter.Serialize(sectionCard), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void OneCard_DrivenThroughTheWholeFlow_CarriesRoundBaseGatesAndBlockedByCorrectlyAtEveryStep()
    {
        var t = T0;

        // drafting -> briefed. base is recorded for the first time; round starts at 1.
        var briefed = AssertApplied(CardStore.ApplyBlockTransition(
            _root, _path, "brief", CardOwner.Architect, t = t.AddMinutes(1), Base, TimeSpan.FromSeconds(5), ChangeName));
        Assert.Equal("briefed", briefed.Frontmatter.Status);
        Assert.Equal(Base, briefed.BlockFields.Base);
        Assert.Equal(1, briefed.BlockFields.Round);

        // base is immutable from here on — an attempt to change it mid-flow is refused, and the
        // refusal prevents the transition too (O-3): status stays briefed, not building.
        var immutableAttempt = CardStore.ApplyBlockTransition(
            _root, _path, "claim", CardOwner.Worker, t.AddMinutes(1), "a-different-commit", TimeSpan.FromSeconds(5), ChangeName);
        var baseImmutable = Assert.IsType<CardBlockTransitionOutcome.BaseImmutable>(immutableAttempt);
        Assert.Equal(Base, baseImmutable.Recorded);
        Assert.Equal("briefed", AssertParseSuccess(CardStore.ReadCard(_path)).Frontmatter.Status);

        // briefed -> building.
        var building = AssertApplied(CardStore.ApplyBlockTransition(
            _root, _path, "claim", CardOwner.Worker, t = t.AddMinutes(1), null, TimeSpan.FromSeconds(5), ChangeName));
        Assert.Equal("building", building.Frontmatter.Status);
        Assert.Equal(Base, building.BlockFields.Base);
        Assert.Equal(1, building.BlockFields.Round);

        // Round 1's gates: build passes.
        var round1Gate = AssertRecorded(CardStore.RecordGateResult(
            _root, _path, "build", 0, CardOwner.Worker, t = t.AddMinutes(1), TimeSpan.FromSeconds(5), ChangeName));
        Assert.Equal(1, round1Gate.Result.Round);
        Assert.True(round1Gate.Card.BlockFields.GateStatusOf("build").Passed);

        // building -> in-review.
        var inReview = AssertApplied(CardStore.ApplyBlockTransition(
            _root, _path, "submit-for-review", CardOwner.Worker, t = t.AddMinutes(1), null, TimeSpan.FromSeconds(5), ChangeName));
        Assert.Equal("in-review", inReview.Frontmatter.Status);

        // The reviewer blocks it on an open question. Blocking never touches flow state.
        var blocked = AssertUpdated(CardStore.AddBlockedBy(
            _root, _path, "Q-0001", CardOwner.Reviewer, t = t.AddMinutes(1), TimeSpan.FromSeconds(5), ChangeName));
        Assert.Equal("in-review", blocked.Card.Frontmatter.Status);
        Assert.Equal(["Q-0001"], blocked.Card.BlockFields.BlockedBy);

        // in-review -> briefed (changes-requested). round increments; base is carried, not reset;
        // round 1's gate result is retained but no longer counts as evidence — B2, the whole point
        // of this test.
        var changesRequested = AssertApplied(CardStore.ApplyBlockTransition(
            _root, _path, "changes-requested", CardOwner.Reviewer, t = t.AddMinutes(1), null, TimeSpan.FromSeconds(5), ChangeName));
        Assert.Equal("briefed", changesRequested.Frontmatter.Status);
        Assert.Equal(Base, changesRequested.BlockFields.Base);
        Assert.Equal(2, changesRequested.BlockFields.Round);
        Assert.Single(changesRequested.BlockFields.GateResults);
        Assert.Equal(1, changesRequested.BlockFields.GateResults[0].Round);
        Assert.False(changesRequested.BlockFields.GateStatusOf("build").Passed, "round 1's passing build must not read as current evidence in round 2.");
        // blocked_by survives a flow transition unchanged — it is orthogonal state, per
        // work-lifecycle's "Blocked is derived, not stored".
        Assert.Equal(["Q-0001"], changesRequested.BlockFields.BlockedBy);

        // The question is answered; unblock.
        var unblocked = AssertUpdated(CardStore.RemoveBlockedBy(
            _root, _path, "Q-0001", CardOwner.Worker, t = t.AddMinutes(1), TimeSpan.FromSeconds(5), ChangeName));
        Assert.Empty(unblocked.Card.BlockFields.BlockedBy);

        // briefed -> building, round 2.
        var building2 = AssertApplied(CardStore.ApplyBlockTransition(
            _root, _path, "claim", CardOwner.Worker, t = t.AddMinutes(1), null, TimeSpan.FromSeconds(5), ChangeName));
        Assert.Equal("building", building2.Frontmatter.Status);
        Assert.Equal(2, building2.BlockFields.Round);

        // Round 2's build gate is recorded as its own entry — round 1's stays on the card.
        var round2Gate = AssertRecorded(CardStore.RecordGateResult(
            _root, _path, "build", 0, CardOwner.Worker, t = t.AddMinutes(1), TimeSpan.FromSeconds(5), ChangeName));
        Assert.Equal(2, round2Gate.Result.Round);
        Assert.Equal(2, round2Gate.Card.BlockFields.GateResults.Length);
        Assert.True(round2Gate.Card.BlockFields.GateStatusOf("build").Passed);

        // building -> in-review -> approved -> landed -> closed. round and base stay exactly as
        // recorded at round 2's brief throughout the rest of the run.
        var inReview2 = AssertApplied(CardStore.ApplyBlockTransition(
            _root, _path, "submit-for-review", CardOwner.Worker, t = t.AddMinutes(1), null, TimeSpan.FromSeconds(5), ChangeName));
        Assert.Equal("in-review", inReview2.Frontmatter.Status);

        // approved via the one real door, RecordApproval — the generic ApplyBlockTransition path
        // never stamps reviewed_state, and §8a block A's landing check needs one recorded.
        var approved = AssertApproved(CardStore.RecordApproval(
            _root, _path, "final-reviewed-state", ["Behaves correctly end to end."], [], CardOwner.Reviewer, t = t.AddMinutes(1), TimeSpan.FromSeconds(5), ChangeName)).Card;
        Assert.Equal("approved", approved.Frontmatter.Status);
        Assert.Equal(Base, approved.BlockFields.Base);
        Assert.Equal(2, approved.BlockFields.Round);
        Assert.Equal("final-reviewed-state", approved.BlockFields.ReviewedState);

        // landed via the one door that remains, §8a block A: the section closing. §8a block A's
        // revision removed the reviewed_state comparison entirely, so no --state-equivalent value
        // is needed here any more.
        var sectionClosed = AssertClosed(CardStore.CloseSection(
            _root, _sectionPath, CardOwner.Architect, t = t.AddMinutes(1), TimeSpan.FromSeconds(5), ChangeName));
        var landed = Assert.Single(sectionClosed.LandedBlocks);
        Assert.Equal("landed", landed.Frontmatter.Status);

        var closed = AssertApplied(CardStore.ApplyBlockTransition(
            _root, _path, "close", CardOwner.Architect, t = t.AddMinutes(1), null, TimeSpan.FromSeconds(5), ChangeName));
        Assert.Equal("closed", closed.Frontmatter.Status);
        Assert.Equal(Base, closed.BlockFields.Base);
        Assert.Equal(2, closed.BlockFields.Round);

        // The full transition history is the complete, attributed audit trail across every round —
        // work-lifecycle's own requirement, checked here as the composed result of the whole run.
        Assert.Equal(
            ["brief", "claim", "submit-for-review", "changes-requested", "claim", "submit-for-review", "approve", "land", "close"],
            closed.Transitions.Select(entry => entry.Name));
        Assert.Equal(
            [CardOwner.Architect, CardOwner.Worker, CardOwner.Worker, CardOwner.Reviewer, CardOwner.Worker, CardOwner.Worker, CardOwner.Reviewer, CardOwner.Architect, CardOwner.Architect],
            closed.Transitions.Select(entry => entry.By));

        // Both rounds' gate evidence is still on the card at close — retained, not destroyed.
        Assert.Equal(2, closed.BlockFields.GateResults.Length);
        Assert.Contains(closed.BlockFields.GateResults, result => result.Round == 1 && result.Label == "build" && result.ExitCode == 0);
        Assert.Contains(closed.BlockFields.GateResults, result => result.Round == 2 && result.Label == "build" && result.ExitCode == 0);
    }

    private static CardFile AssertApplied(CardBlockTransitionOutcome outcome) =>
        outcome.Match(
            onApplied: static applied => applied.Card,
            onUndefinedTransition: static u => throw new Xunit.Sdk.XunitException($"expected Applied, got UndefinedTransition from {u.CurrentState.ToWireString()}"),
            onBaseNotRecorded: static _ => throw new Xunit.Sdk.XunitException("expected Applied, got BaseNotRecorded"),
            onBaseImmutable: static b => throw new Xunit.Sdk.XunitException($"expected Applied, got BaseImmutable(recorded: {b.Recorded}, attempted: {b.Attempted})"),
            onUndispositionedNits: static u => throw new Xunit.Sdk.XunitException($"expected Applied, got UndispositionedNits({string.Join(", ", u.NitIds)})"),
            onNotABlockCard: static n => throw new Xunit.Sdk.XunitException($"expected Applied, got NotABlockCard({n.Kind.ToWireString()})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Applied, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Applied, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Applied, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Applied, got ToolFailure: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: static disagreement => throw new Xunit.Sdk.XunitException($"expected Applied, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"));

    private static CardGateResultOutcome.Recorded AssertRecorded(CardGateResultOutcome outcome) =>
        outcome.Match(
            onRecorded: static recorded => recorded,
            onNotABlockCard: static n => throw new Xunit.Sdk.XunitException($"expected Recorded, got NotABlockCard({n.Kind.ToWireString()})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Recorded, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Recorded, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Recorded, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Recorded, got ToolFailure: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: static disagreement => throw new Xunit.Sdk.XunitException($"expected Recorded, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"));

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
            onRoundDisagreesWithHistory: static disagreement => throw new Xunit.Sdk.XunitException($"expected Updated, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));

    private static CardApprovalOutcome.Approved AssertApproved(CardApprovalOutcome outcome) =>
        outcome.Match(
            onApproved: static approved => approved,
            onRoleNotPermitted: static r => throw new Xunit.Sdk.XunitException($"expected Approved, got RoleNotPermitted({r.AttemptedRole})"),
            onUndefinedTransition: static u => throw new Xunit.Sdk.XunitException($"expected Approved, got UndefinedTransition from {u.CurrentState.ToWireString()}"),
            onUndispositionedNits: static u => throw new Xunit.Sdk.XunitException($"expected Approved, got UndispositionedNits({string.Join(", ", u.NitIds)})"),
            onNotABlockCard: static n => throw new Xunit.Sdk.XunitException($"expected Approved, got NotABlockCard({n.Kind.ToWireString()})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Approved, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Approved, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Approved, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Approved, got ToolFailure: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: static disagreement => throw new Xunit.Sdk.XunitException($"expected Approved, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"));

    private static CardSectionCloseOutcome.Closed AssertClosed(CardSectionCloseOutcome outcome) =>
        outcome.Match(
            onClosed: static closed => closed,
            onAlreadyClosed: static already => throw new Xunit.Sdk.XunitException($"expected Closed, got AlreadyClosed: '{already.FilePath}'"),
            onNotASectionCard: static n => throw new Xunit.Sdk.XunitException($"expected Closed, got NotASectionCard({n.Kind.ToWireString()})"),
            onBlockNotApproved: static n => throw new Xunit.Sdk.XunitException($"expected Closed, got BlockNotApproved({n.BlockId}, {n.ActualState})"),
            onBlockGateFailed: static f => throw new Xunit.Sdk.XunitException($"expected Closed, got BlockGateFailed({f.BlockId}, {f.GateLabel}={f.ExitCode})"),
            onBlockGateAbsent: static a => throw new Xunit.Sdk.XunitException($"expected Closed, got BlockGateAbsent({a.BlockId}, {a.GateLabel})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Closed, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Closed, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Closed, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Closed, got ToolFailure: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: static disagreement => throw new Xunit.Sdk.XunitException($"expected Closed, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"));
}
