using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 7.1/7.11 — <see cref="CardStore.DischargeRegisterCard"/>: the discharge half of the register's
/// two-state lifecycle, and the hazard-specific "condition lapsed" scenario, which is just this
/// same general verb applied to a hazard (register: "A hazard whose condition no longer holds SHALL
/// be discharged").
/// </summary>
public sealed class CardRegisterDischargeTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-register-discharge-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _registerDirectory;
    private readonly string _changeDirectory;

    public CardRegisterDischargeTests()
    {
        _registerDirectory = Path.Combine(_root, "callboard", "register");
        _changeDirectory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_registerDirectory);
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
    public void DischargeRegisterCard_OpenRule_RecordsActingRoleAndTime_AndFlipsStatus()
    {
        var path = WriteRegisterCard("r-0001", "R-0001", CardKind.Rule, CardScope.Repository, RegisterCardFields.Empty);

        var outcome = CardStore.DischargeRegisterCard(_root, path, CardOwner.Architect, Created.AddDays(5), TimeSpan.FromSeconds(5));

        var discharged = AssertDischarged(outcome);
        Assert.Equal("discharged", discharged.Card.Frontmatter.Status);
        Assert.Equal(CardOwner.Architect, discharged.Card.RegisterFields.DischargedBy);
        Assert.Equal(Created.AddDays(5), discharged.Card.RegisterFields.DischargedAt);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("discharged", read.Frontmatter.Status);
        Assert.Equal(CardOwner.Architect, read.RegisterFields.DischargedBy);
    }

    // register: "A hazard whose condition no longer holds SHALL be discharged" — the discharge verb
    // is kind-agnostic; this is the hazard case of the same general path proven above for a rule.
    [Fact]
    public void DischargeRegisterCard_HazardWhoseConditionHasLapsed_Discharges()
    {
        var fields = new RegisterCardFields("The staging key never rotates", "weekly", null, null);
        var path = WriteRegisterCard("h-0001", "H-0001", CardKind.Hazard, CardScope.Repository, fields);

        var outcome = CardStore.DischargeRegisterCard(_root, path, CardOwner.Worker, Created.AddDays(7), TimeSpan.FromSeconds(5));

        var discharged = AssertDischarged(outcome);
        Assert.Equal("discharged", discharged.Card.Frontmatter.Status);
        // The condition/cadence themselves are untouched by discharge — only the lifecycle fields change.
        Assert.Equal("The staging key never rotates", discharged.Card.RegisterFields.Condition);
        Assert.Equal("weekly", discharged.Card.RegisterFields.Cadence);
    }

    [Fact]
    public void DischargeRegisterCard_AlreadyDischarged_Refuses_AndDoesNotOverwriteTheFirstDischarge()
    {
        var path = WriteRegisterCard("o-0001", "O-0001", CardKind.Obligation, CardScope.Change, RegisterCardFields.Empty);
        AssertDischarged(CardStore.DischargeRegisterCard(_root, path, CardOwner.Architect, Created.AddDays(1), TimeSpan.FromSeconds(5), ChangeName));

        var second = CardStore.DischargeRegisterCard(_root, path, CardOwner.Supervisor, Created.AddDays(2), TimeSpan.FromSeconds(5), ChangeName);

        second.Match<object?>(
            onDischarged: discharged => throw new Xunit.Sdk.XunitException("expected AlreadyDischarged, got Discharged"),
            onAlreadyDischarged: static _ => null,
            onInvalidStatus: invalid => throw new Xunit.Sdk.XunitException($"expected AlreadyDischarged, got InvalidStatus: {invalid.Status}"),
            onNotARegisterCard: notARegister => throw new Xunit.Sdk.XunitException($"expected AlreadyDischarged, got NotARegisterCard({notARegister.Kind.ToWireString()})"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected AlreadyDischarged, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected AlreadyDischarged, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected AlreadyDischarged, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected AlreadyDischarged, got ToolFailure: {toolFailure.Reason}"));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(CardOwner.Architect, read.RegisterFields.DischargedBy);
        Assert.Equal(Created.AddDays(1), read.RegisterFields.DischargedAt);
    }

    [Fact]
    public void DischargeRegisterCard_TargetIsNotARegisterCard_Refuses()
    {
        var path = Path.Combine(_changeDirectory, "b-0001.md");
        var frontmatter = new CardFrontmatter(
            "B-0001", CardKind.Block, "Title", "drafting", CardOwner.Worker, CardScope.Change, "4", Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.DischargeRegisterCard(_root, path, CardOwner.Architect, Created, TimeSpan.FromSeconds(5), ChangeName);

        outcome.Match<object?>(
            onDischarged: discharged => throw new Xunit.Sdk.XunitException("expected NotARegisterCard, got Discharged"),
            onAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected NotARegisterCard, got AlreadyDischarged: '{already.FilePath}'"),
            onInvalidStatus: invalid => throw new Xunit.Sdk.XunitException($"expected NotARegisterCard, got InvalidStatus: {invalid.Status}"),
            onNotARegisterCard: static n => { Assert.Equal(CardKind.Block, n.Kind); return null; },
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected NotARegisterCard, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected NotARegisterCard, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected NotARegisterCard, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected NotARegisterCard, got ToolFailure: {toolFailure.Reason}"));
    }

    // register: "SHALL NOT occupy flow states" — a hand-edited register card carrying a
    // BlockFlowState value must not be silently treated as open (or discharged); it is reported,
    // not swallowed. What would have to break for this to go red: DischargeRegisterCardUnderExistingLock
    // reading card.Frontmatter.Status through anything other than RegisterLifecycleStateWireFormat.TryParse.
    [Fact]
    public void DischargeRegisterCard_StatusIsAFlowState_RefusesRatherThanTreatingItAsOpen()
    {
        var path = Path.Combine(_registerDirectory, "r-0002.md");
        var frontmatter = new CardFrontmatter(
            "R-0002", CardKind.Rule, "Title", "briefed", CardOwner.Architect, CardScope.Repository, string.Empty, Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.DischargeRegisterCard(_root, path, CardOwner.Architect, Created, TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onDischarged: discharged => throw new Xunit.Sdk.XunitException("expected InvalidStatus, got Discharged"),
            onAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected InvalidStatus, got AlreadyDischarged: '{already.FilePath}'"),
            onInvalidStatus: static invalid => { Assert.Equal("briefed", invalid.Status); return null; },
            onNotARegisterCard: notARegister => throw new Xunit.Sdk.XunitException($"expected InvalidStatus, got NotARegisterCard({notARegister.Kind.ToWireString()})"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected InvalidStatus, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected InvalidStatus, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected InvalidStatus, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected InvalidStatus, got ToolFailure: {toolFailure.Reason}"));

        // Never rewritten — the file on disk is exactly what was there before the refused attempt.
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("briefed", read.Frontmatter.Status);
    }

    private string WriteRegisterCard(string fileStem, string id, CardKind kind, CardScope scope, RegisterCardFields fields)
    {
        var directory = scope.Match(
            onSection: () => _changeDirectory,
            onChange: () => _changeDirectory,
            onCapability: () => _changeDirectory,
            onRepository: () => _registerDirectory);
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, kind, "Title", RegisterLifecycleState.Open.ToWireString(), CardOwner.Worker, scope, string.Empty, Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: fields);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static CardRegisterDischargeOutcome.Discharged AssertDischarged(CardRegisterDischargeOutcome outcome) =>
        outcome.Match(
            onDischarged: static discharged => discharged,
            onAlreadyDischarged: static already => throw new Xunit.Sdk.XunitException($"expected Discharged, got AlreadyDischarged: '{already.FilePath}'"),
            onInvalidStatus: static invalid => throw new Xunit.Sdk.XunitException($"expected Discharged, got InvalidStatus: {invalid.Status}"),
            onNotARegisterCard: static n => throw new Xunit.Sdk.XunitException($"expected Discharged, got NotARegisterCard({n.Kind.ToWireString()})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Discharged, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Discharged, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Discharged, got CardCorrupt: {corrupt.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Discharged, got ToolFailure: {toolFailure.Reason}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
