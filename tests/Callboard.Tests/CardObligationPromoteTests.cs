using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 9.9 (block F) — <see cref="CardStore.PromoteObligation"/> (register: "Promotion SHALL NOT be
/// limited to rules... An <c>obligation</c> that outlives the change it was raised in SHALL be
/// promotable to a wider scope on the same terms — the same card, retaining its identity, text and
/// thread"). Exact mirror of <see cref="CardRulePromoteTests"/>'s refusal coverage, generalised to
/// <c>obligation</c> — see that suite for why each case is exercised the way it is.
/// </summary>
public sealed class CardObligationPromoteTests : IDisposable
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset Created = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PromotedAt = Created.AddDays(2);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-obligation-promote-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _changeDirectory;
    private readonly string _registerDirectory;

    public CardObligationPromoteTests()
    {
        _changeDirectory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        _registerDirectory = Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_changeDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void PromoteObligation_ChangeScopedOpenObligation_MovesTheSameCard_OnlyScopeAndUpdatedChange_AndStaysOpen()
    {
        var path = WriteObligationCard("o-0001", "O-0001", CardScope.Change, RegisterLifecycleState.Open, owedBy: "S-0001");
        var beforeRead = AssertParseSuccess(CardStore.ReadCard(path));

        var outcome = CardStore.PromoteObligation(_root, path, CardOwner.Architect, PromotedAt, TimeSpan.FromSeconds(5));

        var promoted = AssertPromoted(outcome);
        Assert.Equal(Path.Combine(_registerDirectory, "o-0001.md"), promoted.NewFilePath);
        Assert.False(File.Exists(path), "the old path must no longer hold a card — this is a move, not a copy.");

        var afterRead = AssertParseSuccess(CardStore.ReadCard(promoted.NewFilePath));
        Assert.Equal(beforeRead.Frontmatter.Id, afterRead.Frontmatter.Id);
        Assert.Equal(beforeRead.Body, afterRead.Body);
        Assert.Equal(RegisterLifecycleState.Open.ToWireString(), afterRead.Frontmatter.Status);
        Assert.Equal("S-0001", afterRead.RegisterFields.OwedBy);
        Assert.Equal(CardScope.Repository, afterRead.Frontmatter.Scope);
        Assert.Equal(PromotedAt, afterRead.Frontmatter.Updated);
        Assert.NotEqual(beforeRead.Frontmatter.Scope, afterRead.Frontmatter.Scope);

        var promotionComment = Assert.Single(afterRead.Comments);
        Assert.Equal(CardOwner.Architect, promotionComment.Author);
        Assert.Equal(PromotedAt, promotionComment.Timestamp);
    }

    [Fact]
    public void PromoteObligation_AlreadyRepositoryScoped_Refuses_AndRecordsTheRefusal()
    {
        Directory.CreateDirectory(_registerDirectory);
        var path = Path.Combine(_registerDirectory, "o-0002.md");
        WriteObligationCardAt(path, "O-0002", CardScope.Repository, RegisterLifecycleState.Open, "S-0001");

        var outcome = CardStore.PromoteObligation(_root, path, CardOwner.Architect, PromotedAt, TimeSpan.FromSeconds(5));

        Assert.IsType<CardObligationPromoteOutcome.AlreadyRepositoryScoped>(outcome);
        Assert.True(File.Exists(path));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    [Fact]
    public void PromoteObligation_SectionScoped_RefusesAsNotChangeScoped_AndRecordsTheRefusal()
    {
        var path = Path.Combine(_changeDirectory, "o-0003.md");
        WriteObligationCardAt(path, "O-0003", CardScope.Section, RegisterLifecycleState.Open, "S-0001");

        var outcome = CardStore.PromoteObligation(_root, path, CardOwner.Architect, PromotedAt, TimeSpan.FromSeconds(5), ChangeName);

        var refusal = Assert.IsType<CardObligationPromoteOutcome.NotChangeScoped>(outcome);
        Assert.Equal(CardScope.Section, refusal.Scope);
        Assert.True(File.Exists(path));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    [Fact]
    public void PromoteObligation_NonObligationCard_Refuses_AndRecordsTheRefusal()
    {
        var path = Path.Combine(_changeDirectory, "r-0001.md");
        var frontmatter = new CardFrontmatter(
            "R-0001", CardKind.Rule, "Title", RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect,
            CardScope.Change, string.Empty, Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.PromoteObligation(_root, path, CardOwner.Architect, PromotedAt, TimeSpan.FromSeconds(5), ChangeName);

        var refusal = Assert.IsType<CardObligationPromoteOutcome.NotAnObligationCard>(outcome);
        Assert.Equal(CardKind.Rule, refusal.Kind);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    [Fact]
    public void PromoteObligation_StatusIsAFlowState_Refuses_AndRecordsTheRefusal()
    {
        var path = Path.Combine(_changeDirectory, "o-0004.md");
        var frontmatter = new CardFrontmatter(
            "O-0004", CardKind.Obligation, "Title", "briefed", CardOwner.Architect, CardScope.Change, string.Empty, Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: new RegisterCardFields(null, null, null, null, OwedBy: "S-0001"));
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.PromoteObligation(_root, path, CardOwner.Architect, PromotedAt, TimeSpan.FromSeconds(5), ChangeName);

        var refusal = Assert.IsType<CardObligationPromoteOutcome.InvalidStatus>(outcome);
        Assert.Equal("briefed", refusal.Status);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    [Fact]
    public void PromoteObligation_TargetBasenameAlreadyClaimedInRegister_Refuses_WithNothingMoved_AndRecords()
    {
        var path = WriteObligationCard("o-0005", "O-0005", CardScope.Change, RegisterLifecycleState.Open, "S-0001");

        Directory.CreateDirectory(_registerDirectory);
        var collisionPath = Path.Combine(_registerDirectory, "o-0005.md");
        File.WriteAllText(collisionPath, "not this obligation at all — an unrelated file at the same basename.");

        var outcome = CardStore.PromoteObligation(_root, path, CardOwner.Architect, PromotedAt, TimeSpan.FromSeconds(5), ChangeName);

        Assert.IsType<CardObligationPromoteOutcome.TargetAlreadyExists>(outcome);
        Assert.True(File.Exists(path), "phase one must not run at all once the target collision is detected.");
        Assert.Equal("not this obligation at all — an unrelated file at the same basename.", File.ReadAllText(collisionPath));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    private string WriteObligationCard(string fileStem, string id, CardScope scope, RegisterLifecycleState state, string owedBy)
    {
        var path = Path.Combine(scope == CardScope.Repository ? _registerDirectory : _changeDirectory, fileStem + ".md");
        if (scope == CardScope.Repository)
        {
            Directory.CreateDirectory(_registerDirectory);
        }

        WriteObligationCardAt(path, id, scope, state, owedBy);
        return path;
    }

    private static void WriteObligationCardAt(string path, string id, CardScope scope, RegisterLifecycleState state, string owedBy)
    {
        var frontmatter = new CardFrontmatter(
            id, CardKind.Obligation, "Settle the migration", state.ToWireString(), CardOwner.Architect, scope, string.Empty, Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: new RegisterCardFields(null, null, null, null, OwedBy: owedBy));
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static CardObligationPromoteOutcome.Promoted AssertPromoted(CardObligationPromoteOutcome outcome) =>
        outcome.Match(
            onPromoted: static promoted => promoted,
            onAlreadyRepositoryScoped: static already => throw new Xunit.Sdk.XunitException($"expected Promoted, got AlreadyRepositoryScoped: '{already.FilePath}'"),
            onNotChangeScoped: static n => throw new Xunit.Sdk.XunitException($"expected Promoted, got NotChangeScoped({n.Scope.ToWireString()})"),
            onInvalidStatus: static invalid => throw new Xunit.Sdk.XunitException($"expected Promoted, got InvalidStatus: {invalid.Status}"),
            onNotAnObligationCard: static n => throw new Xunit.Sdk.XunitException($"expected Promoted, got NotAnObligationCard({n.Kind.ToWireString()})"),
            onTargetAlreadyExists: static already => throw new Xunit.Sdk.XunitException($"expected Promoted, got TargetAlreadyExists: '{already.FilePath}'"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Promoted, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Promoted, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Promoted, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected Promoted, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Promoted, got ToolFailure: {toolFailure.Reason}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
