using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 7.1 — <see cref="CardStore.CreateCard"/>: the shared creation path for the four register kinds
/// (<c>rule</c>, <c>hazard</c>, <c>obligation</c>, <c>decision</c>) and <c>section</c>. Identity
/// comes from <see cref="CardIdentityAllocator"/>, scope is validated through
/// <see cref="CardScopeRules.Validate"/>, and every card lands through <see cref="CardStore.WriteCard"/>'s
/// existing locked, create-only, atomic-rename path.
/// </summary>
public sealed class CardCreateTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);

    private const string ChangeName = "establish-callboard";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-card-create-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void CreateCard_Rule_ChangeScoped_Succeeds_AndAllocatesIdentityFromTheCounter()
    {
        var path = Path.Combine(_root, "callboard", "changes", ChangeName, "r-0001.md");

        var outcome = CardStore.CreateCard(
            _root, path, CardKind.Rule, CardScope.Change, "Never trust a path string as file identity",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), ChangeName);

        var created = AssertCreated(outcome);
        Assert.Equal("R-0001", created.Card.Frontmatter.Id);
        Assert.Equal(CardKind.Rule, created.Card.Frontmatter.Kind);
        Assert.Equal(CardScope.Change, created.Card.Frontmatter.Scope);
        Assert.Equal("open", created.Card.Frontmatter.Status);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("R-0001", read.Frontmatter.Id);
        Assert.True(RegisterLifecycleStateWireFormat.TryParse(read.Frontmatter.Status, out var state));
        Assert.Equal(RegisterLifecycleState.Open, state);
    }

    [Fact]
    public void CreateCard_Rule_RepositoryScoped_Succeeds()
    {
        var path = Path.Combine(_root, "callboard", "register", "r-0001.md");

        var outcome = CardStore.CreateCard(
            _root, path, CardKind.Rule, CardScope.Repository, "A repository-wide rule",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), changeName: null);

        var created = AssertCreated(outcome);
        Assert.Equal(CardScope.Repository, created.Card.Frontmatter.Scope);
    }

    [Fact]
    public void CreateCard_Rule_SectionScoped_RefusesWithTheSpecsExactWording()
    {
        var path = Path.Combine(_root, "callboard", "changes", ChangeName, "r-0001.md");

        var outcome = CardStore.CreateCard(
            _root, path, CardKind.Rule, CardScope.Section, "A constraint in a brief",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), ChangeName);

        var refused = AssertScopeRefused(outcome);
        Assert.Contains("a rule applying to one section is a constraint in a brief", refused.Reason, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CreateCard_Hazard_CarriesConditionAndCadence()
    {
        var path = Path.Combine(_root, "callboard", "register", "h-0001.md");
        var registerFields = new RegisterCardFields("The API key rotates every 90 days", "monthly", null, null);

        var outcome = CardStore.CreateCard(
            _root, path, CardKind.Hazard, CardScope.Repository, "Rotating API key",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.Worker, "Body.", registerFields,
            Created, TimeSpan.FromSeconds(5), changeName: null);

        var created = AssertCreated(outcome);
        Assert.Equal("The API key rotates every 90 days", created.Card.RegisterFields.Condition);
        Assert.Equal("monthly", created.Card.RegisterFields.Cadence);

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal("The API key rotates every 90 days", read.RegisterFields.Condition);
        Assert.Equal("monthly", read.RegisterFields.Cadence);
    }

    [Fact]
    public void CreateCard_Obligation_FixedChangeScope_Succeeds()
    {
        var path = Path.Combine(_root, "callboard", "changes", ChangeName, "o-0001.md");

        var outcome = CardStore.CreateCard(
            _root, path, CardKind.Obligation, CardScope.Change, "Settle the migration",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), ChangeName);

        var created = AssertCreated(outcome);
        Assert.Equal(CardKind.Obligation, created.Card.Frontmatter.Kind);
        Assert.Equal(CardScope.Change, created.Card.Frontmatter.Scope);
    }

    [Fact]
    public void CreateCard_Decision_FixedCapabilityScope_Succeeds()
    {
        var path = Path.Combine(_root, "callboard", "decisions", "d-0001.md");

        var outcome = CardStore.CreateCard(
            _root, path, CardKind.Decision, CardScope.Capability, "Adopt option A",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.ProductOwner, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), changeName: null);

        var created = AssertCreated(outcome);
        Assert.Equal(CardKind.Decision, created.Card.Frontmatter.Kind);
        Assert.Equal(CardScope.Capability, created.Card.Frontmatter.Scope);
    }

    [Fact]
    public void CreateCard_Section_FixedChangeScope_Succeeds_AndReadsBackAsSectionFlowStateOpen()
    {
        var path = Path.Combine(_root, "callboard", "changes", ChangeName, "s-0001.md");

        var outcome = CardStore.CreateCard(
            _root, path, CardKind.Section, CardScope.Change, "8. Review",
            SectionFlowState.Open.ToWireString(), CardOwner.Architect, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), ChangeName);

        var created = AssertCreated(outcome);
        Assert.True(SectionFlowStateWireFormat.TryParse(created.Card.Frontmatter.Status, out var state));
        Assert.Equal(SectionFlowState.Open, state);
    }

    [Fact]
    public void CreateCard_TargetAlreadyExists_Refuses()
    {
        var path = Path.Combine(_root, "callboard", "decisions", "d-0002.md");

        AssertCreated(CardStore.CreateCard(
            _root, path, CardKind.Decision, CardScope.Capability, "First",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.ProductOwner, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), changeName: null));

        var second = CardStore.CreateCard(
            _root, path, CardKind.Decision, CardScope.Capability, "Second",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.ProductOwner, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), changeName: null);

        second.Match<object?>(
            onCreated: created => throw new Xunit.Sdk.XunitException("expected AlreadyExists, got Created"),
            onScopeRefused: refused => throw new Xunit.Sdk.XunitException($"expected AlreadyExists, got ScopeRefused: {refused.Reason}"),
            onAlreadyExists: static _ => null,
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected AlreadyExists, got LayoutMismatch: {layoutMismatch.Reason}"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected AlreadyExists, got ToolFailure: {toolFailure.Reason}"));
    }

    // Scope is refused before any identity is allocated — a rejected create must not burn an
    // identity number a later, legitimate create would then skip over.
    [Fact]
    public void CreateCard_ScopeRefused_NeverAllocatesAnIdentity()
    {
        var badPath = Path.Combine(_root, "callboard", "changes", ChangeName, "r-bad.md");
        AssertScopeRefused(CardStore.CreateCard(
            _root, badPath, CardKind.Rule, CardScope.Section, "Bad",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), ChangeName));

        var goodPath = Path.Combine(_root, "callboard", "register", "r-good.md");
        var created = AssertCreated(CardStore.CreateCard(
            _root, goodPath, CardKind.Rule, CardScope.Repository, "Good",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), changeName: null));

        Assert.Equal("R-0001", created.Card.Frontmatter.Id);
    }

    private static CardCreateOutcome.Created AssertCreated(CardCreateOutcome outcome) =>
        outcome.Match(
            onCreated: static created => created,
            onScopeRefused: static refused => throw new Xunit.Sdk.XunitException($"expected Created, got ScopeRefused: {refused.Reason}"),
            onAlreadyExists: static already => throw new Xunit.Sdk.XunitException($"expected Created, got AlreadyExists: '{already.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Created, got LayoutMismatch: {layoutMismatch.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Created, got ToolFailure: {toolFailure.Reason}"));

    private static CardCreateOutcome.ScopeRefused AssertScopeRefused(CardCreateOutcome outcome) =>
        outcome.Match(
            onCreated: static created => throw new Xunit.Sdk.XunitException($"expected ScopeRefused, got Created: '{created.Card.Frontmatter.Id}'"),
            onScopeRefused: static refused => refused,
            onAlreadyExists: static already => throw new Xunit.Sdk.XunitException($"expected ScopeRefused, got AlreadyExists: '{already.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected ScopeRefused, got LayoutMismatch: {layoutMismatch.Reason}"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected ScopeRefused, got ToolFailure: {toolFailure.Reason}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
