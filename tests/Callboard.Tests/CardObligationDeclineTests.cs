using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 9.9 (block F) — <see cref="CardStore.DeclineObligation"/> (register: "An obligation that will
/// not be met SHALL be closable by declining it with a recorded reason, and the record SHALL
/// distinguish that from an obligation that was discharged"). Same shape as
/// <see cref="CardRegisterDischargeTests"/>'s coverage, plus <see cref="CardObligationDeclineOutcome.
/// ReasonRequired"/>, the one disposition-specific refusal discharge has no counterpart for.
/// </summary>
public sealed class CardObligationDeclineTests : IDisposable
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset Created = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DeclinedAt = Created.AddDays(2);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-obligation-decline-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _changeDirectory;

    public CardObligationDeclineTests()
    {
        _changeDirectory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
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
    public void DeclineObligation_OpenObligationWithAReason_Declines_RecordingTheReason_DistinctFromAnOrdinaryDischarge()
    {
        var path = WriteObligationCard("o-0001", "O-0001", RegisterLifecycleState.Open);

        var outcome = CardStore.DeclineObligation(
            _root, path, CardOwner.Architect, "the migration this obligation named is no longer happening.", DeclinedAt, TimeSpan.FromSeconds(5), ChangeName);

        var declined = AssertDeclined(outcome);
        Assert.Equal(RegisterLifecycleState.Discharged.ToWireString(), declined.Card.Frontmatter.Status);
        Assert.Equal(CardOwner.Architect, declined.Card.RegisterFields.DischargedBy);
        Assert.Equal(DeclinedAt, declined.Card.RegisterFields.DischargedAt);
        Assert.Equal("the migration this obligation named is no longer happening.", declined.Card.RegisterFields.DeclinedReason);

        var onDisk = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("the migration this obligation named is no longer happening.", onDisk.RegisterFields.DeclinedReason);
    }

    [Fact]
    public void DeclineObligation_NoReason_Refuses_AndRecordsTheRefusal_AndDoesNotDischarge()
    {
        var path = WriteObligationCard("o-0002", "O-0002", RegisterLifecycleState.Open);

        var outcome = CardStore.DeclineObligation(_root, path, CardOwner.Architect, "   ", DeclinedAt, TimeSpan.FromSeconds(5), ChangeName);

        Assert.IsType<CardObligationDeclineOutcome.ReasonRequired>(outcome);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(RegisterLifecycleState.Open.ToWireString(), read.Frontmatter.Status);
        Assert.Null(read.RegisterFields.DeclinedReason);
        var recorded = Assert.Single(read.Refusals);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    [Fact]
    public void DeclineObligation_AlreadyDischarged_Refuses_AndDoesNotOverwriteTheFirstDisposition()
    {
        var path = WriteObligationCard("o-0003", "O-0003", RegisterLifecycleState.Discharged);

        var outcome = CardStore.DeclineObligation(_root, path, CardOwner.Architect, "too late now.", DeclinedAt, TimeSpan.FromSeconds(5), ChangeName);

        Assert.IsType<CardObligationDeclineOutcome.AlreadyDischarged>(outcome);
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Null(read.RegisterFields.DeclinedReason);
        var recorded = Assert.Single(read.Refusals);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    [Fact]
    public void DeclineObligation_StatusIsAFlowState_Refuses_AndRecordsTheRefusal()
    {
        var path = Path.Combine(_changeDirectory, "o-0004.md");
        var frontmatter = new CardFrontmatter(
            "O-0004", CardKind.Obligation, "Title", "briefed", CardOwner.Architect, CardScope.Change, string.Empty, Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: new RegisterCardFields(null, null, null, null, OwedBy: "S-0001"));
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.DeclineObligation(_root, path, CardOwner.Architect, "reason.", DeclinedAt, TimeSpan.FromSeconds(5), ChangeName);

        var refusal = Assert.IsType<CardObligationDeclineOutcome.InvalidStatus>(outcome);
        Assert.Equal("briefed", refusal.Status);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    [Fact]
    public void DeclineObligation_NonObligationCard_Refuses_AndRecordsTheRefusal()
    {
        var path = Path.Combine(_changeDirectory, "r-0001.md");
        var frontmatter = new CardFrontmatter(
            "R-0001", CardKind.Rule, "Title", RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect,
            CardScope.Change, string.Empty, Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.DeclineObligation(_root, path, CardOwner.Architect, "reason.", DeclinedAt, TimeSpan.FromSeconds(5), ChangeName);

        var refusal = Assert.IsType<CardObligationDeclineOutcome.NotAnObligationCard>(outcome);
        Assert.Equal(CardKind.Rule, refusal.Kind);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    private string WriteObligationCard(string fileStem, string id, RegisterLifecycleState state)
    {
        var path = Path.Combine(_changeDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Obligation, "Settle the migration", state.ToWireString(), CardOwner.Architect, CardScope.Change, string.Empty, Created, Created);
        var fields = state == RegisterLifecycleState.Discharged
            ? new RegisterCardFields(null, null, CardOwner.Architect, Created.AddHours(1), OwedBy: "S-0001")
            : new RegisterCardFields(null, null, null, null, OwedBy: "S-0001");
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: fields);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static CardObligationDeclineOutcome.Declined AssertDeclined(CardObligationDeclineOutcome outcome) =>
        outcome.Match(
            onDeclined: static declined => declined,
            onReasonRequired: static reasonRequired => throw new Xunit.Sdk.XunitException($"expected Declined, got ReasonRequired: '{reasonRequired.FilePath}'"),
            onAlreadyDischarged: static already => throw new Xunit.Sdk.XunitException($"expected Declined, got AlreadyDischarged: '{already.FilePath}'"),
            onInvalidStatus: static invalid => throw new Xunit.Sdk.XunitException($"expected Declined, got InvalidStatus: {invalid.Status}"),
            onNotAnObligationCard: static n => throw new Xunit.Sdk.XunitException($"expected Declined, got NotAnObligationCard({n.Kind.ToWireString()})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Declined, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Declined, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Declined, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected Declined, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Declined, got ToolFailure: {toolFailure.Reason}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
