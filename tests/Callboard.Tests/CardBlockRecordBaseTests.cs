using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §13 block 13.3 — recording a base under lock (work-lifecycle: "Blocks carry their brief
/// context"), the same read-decide-write shape §5 block D's <see cref="CardStore.
/// RecordGateResult"/> established for a single-field recorder with its own outcome type.
/// </summary>
public sealed class CardBlockRecordBaseTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-block-base-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _directory;

    public CardBlockRecordBaseTests()
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
    public void RecordBase_AtDrafting_WithNoneRecordedYet_Records()
    {
        var path = WriteBlockCard("b-0001", "B-0001", BlockFlowState.Drafting, round: null, baseCommit: null, transitions: []);

        var outcome = CardStore.RecordBase(_root, path, "commit-abc", CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var recorded = AssertRecorded(outcome);
        Assert.Equal("commit-abc", recorded.Base);
        Assert.Equal(CardOwner.Architect, recorded.ActingRole);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("commit-abc", read.BlockFields.Base);
        Assert.Equal(Created, read.Frontmatter.Updated);
    }

    // work-lifecycle: "Recording the same base again is not a change" (Product Owner ruling,
    // remediation on 13.3) — retry-safe: an agent that cannot tell whether an earlier call landed
    // can resupply the identical value and succeed, with the recorded value unchanged and, just as
    // importantly, nothing refused or recorded against the card — the old, punitive behaviour this
    // replaces left a permanent refusal entry indistinguishable from a genuine attempted overwrite.
    [Fact]
    public void RecordBase_AlreadyRecorded_SameValue_Succeeds_AndRecordsNoRefusal()
    {
        var path = WriteBlockCard("b-0002", "B-0002", BlockFlowState.Drafting, round: null, baseCommit: "commit-abc", transitions: []);

        var outcome = CardStore.RecordBase(_root, path, "commit-abc", CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var recorded = AssertRecorded(outcome);
        Assert.Equal("commit-abc", recorded.Base);
        Assert.Equal(CardOwner.Architect, recorded.ActingRole);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("commit-abc", read.BlockFields.Base);
        Assert.Empty(read.Refusals);
    }

    // work-lifecycle: "Base does not change once recorded" — a genuine mismatch still refuses.
    [Fact]
    public void RecordBase_AlreadyRecorded_DifferentValue_Refuses_NamesBoth_AndRecordsTheRefusal()
    {
        var path = WriteBlockCard("b-0003", "B-0003", BlockFlowState.Drafting, round: null, baseCommit: "commit-abc", transitions: []);

        var outcome = CardStore.RecordBase(_root, path, "commit-xyz", CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var immutable = Assert.IsType<CardBlockRecordBaseOutcome.BaseImmutable>(outcome);
        Assert.Equal("commit-abc", immutable.RecordedBase);
        Assert.Equal("commit-xyz", immutable.AttemptedBase);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("commit-abc", read.BlockFields.Base);
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(immutable.RefusingRule, refusal.Rule);
        Assert.Equal(immutable.Remedy, refusal.Remedy);
    }

    // work-lifecycle: "Blocks carry their brief context" (Architect ruling item 3) — recording is
    // allowed only while the card is at 'drafting'.
    [Fact]
    public void RecordBase_CardAlreadyBriefed_Refuses_NamesTheState_AndRecordsTheRefusal()
    {
        var path = WriteBlockCard("b-0004", "B-0004", BlockFlowState.Briefed, round: 1, baseCommit: "commit-abc", transitions: []);

        var outcome = CardStore.RecordBase(_root, path, "commit-xyz", CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notAtDrafting = Assert.IsType<CardBlockRecordBaseOutcome.NotAtDrafting>(outcome);
        Assert.Equal(BlockFlowState.Briefed, notAtDrafting.CurrentState);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("commit-abc", read.BlockFields.Base);
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, refusal.By);
        Assert.Equal(notAtDrafting.RefusingRule, refusal.Rule);
        Assert.Equal(notAtDrafting.Remedy, refusal.Remedy);
    }

    [Fact]
    public void RecordBase_NonBlockCard_Refuses_AndRecordsTheRefusal()
    {
        var path = Path.Combine(_directory, "s-0001.md");
        var frontmatter = new CardFrontmatter("S-0001", CardKind.Section, "A section", "open", CardOwner.Architect, CardScope.Change, string.Empty, Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.RecordBase(_root, path, "commit-abc", CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        var notABlock = Assert.IsType<CardBlockRecordBaseOutcome.NotABlockCard>(outcome);
        Assert.Equal(CardKind.Section, notABlock.Kind);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var refusal = Assert.Single(read.Refusals);
        Assert.Equal(notABlock.RefusingRule, refusal.Rule);
        Assert.Equal(notABlock.Remedy, refusal.Remedy);
    }

    private string WriteBlockCard(string fileStem, string id, BlockFlowState status, int? round, string? baseCommit, IReadOnlyList<CardBlockTransitionEntry> transitions)
    {
        var path = Path.Combine(_directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "Title", status.ToWireString(), CardOwner.Architect, CardScope.Change, "13", Created, Created);
        var blockFields = new BlockCardFields(Base: baseCommit, ReviewedState: null, Tasks: [], Round: round, BlockedBy: [], GateResults: []);
        var card = new CardFile(frontmatter, "Body.", [], [], [], blockFields, transitions);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static CardBlockRecordBaseOutcome.Recorded AssertRecorded(CardBlockRecordBaseOutcome outcome) =>
        outcome.Match(
            onRecorded: static recorded => recorded,
            onNotABlockCard: static notABlock => throw new Xunit.Sdk.XunitException($"expected Recorded, got NotABlockCard: '{notABlock.Kind}'"),
            onNotAtDrafting: static notAtDrafting => throw new Xunit.Sdk.XunitException($"expected Recorded, got NotAtDrafting: '{notAtDrafting.CurrentState}'"),
            onBaseImmutable: static immutable => throw new Xunit.Sdk.XunitException($"expected Recorded, got BaseImmutable: recorded '{immutable.RecordedBase}', attempted '{immutable.AttemptedBase}'"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Recorded, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Recorded, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Recorded, got CardCorrupt: {corrupt.Reason}"),
            onRoundDisagreesWithHistory: static disagreement => throw new Xunit.Sdk.XunitException($"expected Recorded, got RoundDisagreesWithHistory: stored {disagreement.StoredRound}, expected {disagreement.ExpectedRound}"),
            onHandEnteredDerivedState: static handEntered => throw new Xunit.Sdk.XunitException($"expected Recorded, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Recorded, got ToolFailure: {toolFailure.Reason}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
