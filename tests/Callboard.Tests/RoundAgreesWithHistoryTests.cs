using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 8a.17/8a.18 (work-lifecycle: "Stored round agrees with the transition history") — a block
/// card's stored <c>round</c> SHALL equal one plus the number of round-incrementing transitions in
/// its own history, and every transition that increments it SHALL advance the field and append to
/// the history as one write. Where the two disagree, the system SHALL refuse to act on that card,
/// naming both figures, altering neither — never guessing which side is right.
///
/// <para>
/// <b>"Act on that card" (Architect ruling, §8a block D brief) covers every writer that mutates a
/// block card</b> — proven here across three of the round-incrementing edges themselves
/// (<see cref="ApplyBlockTransition_ChangesRequested_ReturnsToBriefed_IncrementsRound_HistoryAgrees"/>,
/// <see cref="DispositionNit_FixBeforeLand_ReturnsToBriefed_IncrementsRound_HistoryAgrees"/>,
/// <see cref="RecordSectionVerdict_FindingRecurred_ReturnsOwningCardToBriefed_IncrementsRound_HistoryAgrees"/>
/// — 8a.18, one test per edge, not once generically) and across a non-round-incrementing writer
/// (<see cref="RecordGateResult_CardWithDisagreeingRound_Refuses"/>) to show the refusal is not
/// scoped to the three edges that move <c>round</c> themselves. A read is unaffected
/// (<see cref="ReadCard_CardWithDisagreeingRound_StillReads"/>) — a corrupt card the tool refuses
/// to describe is one nobody can diagnose.
/// </para>
/// </summary>
public sealed class RoundAgreesWithHistoryTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-round-agrees-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;

    public RoundAgreesWithHistoryTests()
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

    // 8a.18 — changes-requested advances round and history in the same write.
    [Fact]
    public void ApplyBlockTransition_ChangesRequested_ReturnsToBriefed_IncrementsRound_HistoryAgrees()
    {
        var path = WriteBlockCard("b-0001", "B-0001", BlockFlowState.InReview, round: 1, transitions: []);

        var outcome = CardStore.ApplyBlockTransition(
            _root, path, "changes-requested", CardOwner.Reviewer, Created.AddHours(1), baseCommit: null, TimeSpan.FromSeconds(5), ChangeName);

        var applied = AssertApplied(outcome);
        Assert.Equal(2, applied.Card.BlockFields.Round);
        var only = Assert.Single(applied.Card.Transitions);
        Assert.Equal("changes-requested", only.Name);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(2, read.BlockFields.Round);
        Assert.Single(read.Transitions);
    }

    // 8a.18 — fix-before-land advances round and history in the same write.
    [Fact]
    public void DispositionNit_FixBeforeLand_ReturnsToBriefed_IncrementsRound_HistoryAgrees()
    {
        var nit = new CardComment(
            Id: "nit-0001", Author: CardOwner.Reviewer, Timestamp: Created, Body: "Fix this.",
            ReplyTo: null, To: CardOwner.Architect, Resolves: null, UnknownHeaderFields: [],
            IsNit: true, Required: true);
        var path = WriteBlockCard("b-0002", "B-0002", BlockFlowState.InReview, round: 1, transitions: [], comments: [nit]);

        var outcome = CardStore.DispositionNit(
            _root, path, "nit-0001", NitDisposition.FixBeforeLand, "Will fix.", CardOwner.Architect, Created.AddHours(1),
            TimeSpan.FromSeconds(5), ChangeName, raiseRequest: null);

        var dispositioned = AssertDispositioned(outcome);
        Assert.True(dispositioned.Transitioned);
        Assert.Equal(2, dispositioned.Card.BlockFields.Round);
        var only = Assert.Single(dispositioned.Card.Transitions);
        Assert.Equal("fix-before-land", only.Name);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(2, read.BlockFields.Round);
        Assert.Single(read.Transitions);
    }

    // 8a.18 — finding-recurred advances round and history in the same write.
    [Fact]
    public void RecordSectionVerdict_FindingRecurred_ReturnsOwningCardToBriefed_IncrementsRound_HistoryAgrees()
    {
        var sectionPath = WriteSectionCard("s-0001", "S-0001");
        var owningPath = WriteApprovedRemediationCard("b-0003", "B-0003", "S-0001", "finding-x001", round: 1, transitions: []);

        var outcome = CardStore.RecordSectionVerdict(
            _root, sectionPath, SectionVerdict.RequestChanges, "aaa", "bbb", CardOwner.Supervisor, Created.AddHours(1),
            TimeSpan.FromSeconds(5), ChangeName, [owningPath], []);

        AssertRecorded(outcome);

        var read = AssertParseSuccess(CardStore.ReadCard(owningPath));
        Assert.Equal("briefed", read.Frontmatter.Status);
        Assert.Equal(2, read.BlockFields.Round);
        var only = Assert.Single(read.Transitions);
        Assert.Equal("finding-recurred", only.Name);
    }

    // 8a.17 — a stored round ahead of the history refuses, names both figures, and alters neither
    // side: the round-incrementing edge itself.
    [Fact]
    public void ApplyBlockTransition_StoredRoundAheadOfHistory_Refuses_NamesBothFigures_AltersNeither()
    {
        var path = WriteBlockCard("b-0004", "B-0004", BlockFlowState.InReview, round: 3, transitions: []);
        var before = File.ReadAllBytes(path);

        var outcome = CardStore.ApplyBlockTransition(
            _root, path, "changes-requested", CardOwner.Reviewer, Created.AddHours(1), baseCommit: null, TimeSpan.FromSeconds(5), ChangeName);

        var disagreement = Assert.IsType<CardBlockTransitionOutcome.RoundDisagreesWithHistory>(outcome);
        Assert.Equal(3, disagreement.StoredRound);
        Assert.Equal(1, disagreement.ExpectedRound);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // 8a.17 — the other direction: a history ahead of the stored round is an equally distinct
    // failure, refused the same way, not silently corrected to match the history.
    [Fact]
    public void ApplyBlockTransition_HistoryAheadOfStoredRound_Refuses_NamesBothFigures_AltersNeither()
    {
        var priorTransition = new CardBlockTransitionEntry(
            CardOwner.Reviewer, "changes-requested", BlockFlowState.InReview, BlockFlowState.Briefed, Created, []);
        var path = WriteBlockCard("b-0005", "B-0005", BlockFlowState.InReview, round: 1, transitions: [priorTransition]);
        var before = File.ReadAllBytes(path);

        var outcome = CardStore.ApplyBlockTransition(
            _root, path, "changes-requested", CardOwner.Reviewer, Created.AddHours(1), baseCommit: null, TimeSpan.FromSeconds(5), ChangeName);

        var disagreement = Assert.IsType<CardBlockTransitionOutcome.RoundDisagreesWithHistory>(outcome);
        Assert.Equal(1, disagreement.StoredRound);
        Assert.Equal(2, disagreement.ExpectedRound);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // 8a.17 — "act on that card" is not scoped to the round-incrementing edges themselves: a
    // writer that never touches round at all (RecordGateResult) still refuses on a mismatched
    // card, and the write it would have made (a new gate result) never lands.
    [Fact]
    public void RecordGateResult_CardWithDisagreeingRound_Refuses_NamesBothFigures_AltersNeither()
    {
        var path = WriteBlockCard("b-0006", "B-0006", BlockFlowState.Building, round: 2, transitions: []);
        var before = File.ReadAllBytes(path);

        var outcome = CardStore.RecordGateResult(_root, path, "build", 0, CardOwner.Worker, Created.AddHours(1), TimeSpan.FromSeconds(5), ChangeName);

        var disagreement = Assert.IsType<CardGateResultOutcome.RoundDisagreesWithHistory>(outcome);
        Assert.Equal(2, disagreement.StoredRound);
        Assert.Equal(1, disagreement.ExpectedRound);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // A read is unaffected by the refusal: a card the tool refuses to write to is not a card it
    // refuses to describe.
    [Fact]
    public void ReadCard_CardWithDisagreeingRound_StillReads()
    {
        var path = WriteBlockCard("b-0007", "B-0007", BlockFlowState.Building, round: 5, transitions: []);

        var read = AssertParseSuccess(CardStore.ReadCard(path));

        Assert.Equal("B-0007", read.Frontmatter.Id);
        Assert.Equal(5, read.BlockFields.Round);
    }

    private string WriteBlockCard(
        string fileStem, string id, BlockFlowState status, int? round, IReadOnlyList<CardBlockTransitionEntry> transitions, IReadOnlyList<CardComment>? comments = null)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", status.ToWireString(), CardOwner.Architect, CardScope.Change, "8a", Created, Created);
        var blockFields = new BlockCardFields(Base: "base-commit", ReviewedState: null, Tasks: [], Round: round, BlockedBy: [], GateResults: []);
        var card = new CardFile(frontmatter, "Body.", comments ?? [], [], [], blockFields, transitions);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private string WriteSectionCard(string fileStem, string id)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "Title", "open", CardOwner.Architect, CardScope.Change, "8a", Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private string WriteApprovedRemediationCard(
        string fileStem, string id, string sectionId, string findingKey, int round, IReadOnlyList<CardBlockTransitionEntry> transitions)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", "approved", CardOwner.Architect, CardScope.Change, sectionId, Created, Created);
        var blockFields = new BlockCardFields(
            Base: "base-commit", ReviewedState: "reviewed-state", Tasks: [], Round: round, BlockedBy: [], GateResults: [], FindingKey: findingKey);
        var card = new CardFile(frontmatter, "Body.", [], [], [], blockFields, transitions);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static CardBlockTransitionOutcome.Applied AssertApplied(CardBlockTransitionOutcome outcome) =>
        outcome.Match(
            onApplied: static applied => applied,
            onUndefinedTransition: static u => throw new Xunit.Sdk.XunitException(
                $"expected Applied, got UndefinedTransition from '{u.CurrentState.ToWireString()}'"),
            onBaseNotRecorded: static _ => throw new Xunit.Sdk.XunitException("expected Applied, got BaseNotRecorded"),
            onBaseImmutable: static i => throw new Xunit.Sdk.XunitException($"expected Applied, got BaseImmutable(recorded: {i.Recorded}, attempted: {i.Attempted})"),
            onUndispositionedNits: static u => throw new Xunit.Sdk.XunitException($"expected Applied, got UndispositionedNits({string.Join(", ", u.NitIds)})"),
            onNotABlockCard: static n => throw new Xunit.Sdk.XunitException($"expected Applied, got NotABlockCard({n.Kind.ToWireString()})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Applied, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Applied, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Applied, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Applied, got ToolFailure: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: static disagreement => throw new Xunit.Sdk.XunitException(
                $"expected Applied, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"));

    private static CardNitDispositionOutcome.Dispositioned AssertDispositioned(CardNitDispositionOutcome outcome) =>
        outcome.Match(
            onDispositioned: static dispositioned => dispositioned,
            onRoleNotPermitted: static r => throw new Xunit.Sdk.XunitException($"expected Dispositioned, got RoleNotPermitted({r.AttemptedRole.ToWireString()})"),
            onNotABlockCard: static n => throw new Xunit.Sdk.XunitException($"expected Dispositioned, got NotABlockCard({n.Kind.ToWireString()})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Dispositioned, got CardNotFound: '{notFound.FilePath}'"),
            onNitNotFound: static nitNotFound => throw new Xunit.Sdk.XunitException($"expected Dispositioned, got NitNotFound: '{nitNotFound.NitId}'"),
            onAlreadyDispositioned: static already => throw new Xunit.Sdk.XunitException($"expected Dispositioned, got AlreadyDispositioned: '{already.NitId}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Dispositioned, got LayoutMismatch: {layoutMismatch.Reason}"),
            onRaisedCardLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Dispositioned, got RaisedCardLayoutMismatch: {layoutMismatch.Reason}"),
            onRaisedCardAlreadyExists: static alreadyExists => throw new Xunit.Sdk.XunitException($"expected Dispositioned, got RaisedCardAlreadyExists: '{alreadyExists.FilePath}'"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Dispositioned, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Dispositioned, got ToolFailure: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: static disagreement => throw new Xunit.Sdk.XunitException(
                $"expected Dispositioned, got RoundDisagreesWithHistory: (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"));

    private static CardSectionVerdictOutcome.Recorded AssertRecorded(CardSectionVerdictOutcome outcome) =>
        outcome.Match(
            onRecorded: static recorded => recorded,
            onNotASectionCard: static n => throw new Xunit.Sdk.XunitException($"expected Recorded, got NotASectionCard({n.Kind.ToWireString()})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Recorded, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Recorded, got LayoutMismatch: {layoutMismatch.Reason}"),
            onRecurringFindingNotApproved: static n => throw new Xunit.Sdk.XunitException($"expected Recorded, got RecurringFindingNotApproved({n.CardId})"),
            onRecurringFindingTargetsTaskImplementingBlock: static n => throw new Xunit.Sdk.XunitException($"expected Recorded, got RecurringFindingTargetsTaskImplementingBlock({n.CardId})"),
            onFindingAlreadyOwned: static n => throw new Xunit.Sdk.XunitException($"expected Recorded, got FindingAlreadyOwned({n.Key})"),
            onNewFindingCardAlreadyExists: static n => throw new Xunit.Sdk.XunitException($"expected Recorded, got NewFindingCardAlreadyExists('{n.FilePath}')"),
            onRemediationBoundExceeded: static n => throw new Xunit.Sdk.XunitException("expected Recorded, got RemediationBoundExceeded"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Recorded, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Recorded, got ToolFailure: {toolFailure.Reason}"),
            onRoundDisagreesWithHistory: static disagreement => throw new Xunit.Sdk.XunitException(
                $"expected Recorded, got RoundDisagreesWithHistory: '{disagreement.FilePath}' (stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound})"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
