using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 7.1 — <see cref="CardStore.CreateCard"/>: the shared creation path for the four register kinds
/// (<c>rule</c>, <c>hazard</c>, <c>obligation</c>, <c>decision</c>) and <c>section</c>. Identity
/// comes from <see cref="CardIdentityAllocator"/>, scope is validated through
/// <see cref="CardScopeRules.Validate"/>, and every card lands through <see cref="CardStore.WriteCard"/>'s
/// existing locked, create-only, atomic-rename path. 14.5: <see cref="CardStore.CreateCard"/> no
/// longer takes a <c>filePath</c> parameter — every assertion here checks the path the card actually
/// landed at (<see cref="CardCreateOutcome.Created.FilePath"/>), never one this test supplied.
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
        var outcome = CardStore.CreateCard(
            _root, CardKind.Rule, CardScope.Change, "Never trust a path string as file identity",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), ChangeName);

        var created = AssertCreated(outcome);
        Assert.Equal("R-0001", created.Card.Frontmatter.Id);
        Assert.Equal(CardKind.Rule, created.Card.Frontmatter.Kind);
        Assert.Equal(CardScope.Change, created.Card.Frontmatter.Scope);
        Assert.Equal("open", created.Card.Frontmatter.Status);

        // 14.5: the file is named for the identity the system issued, not for anything a caller
        // supplied — this test supplied no path at all.
        var expectedPath = Path.Combine(_root, "callboard", "changes", ChangeName, "R-0001.md");
        Assert.Equal(expectedPath, created.FilePath);

        var read = AssertParseSuccess(CardStore.ReadCard(created.FilePath));
        Assert.Equal("R-0001", read.Frontmatter.Id);
        Assert.True(RegisterLifecycleStateWireFormat.TryParse(read.Frontmatter.Status, out var state));
        Assert.Equal(RegisterLifecycleState.Open, state);
    }

    [Fact]
    public void CreateCard_Rule_RepositoryScoped_Succeeds()
    {
        var outcome = CardStore.CreateCard(
            _root, CardKind.Rule, CardScope.Repository, "A repository-wide rule",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), changeName: null);

        var created = AssertCreated(outcome);
        Assert.Equal(CardScope.Repository, created.Card.Frontmatter.Scope);
        Assert.Equal(Path.Combine(_root, "callboard", "register", "R-0001.md"), created.FilePath);
    }

    [Fact]
    public void CreateCard_Rule_SectionScoped_RefusesWithTheSpecsExactWording()
    {
        var outcome = CardStore.CreateCard(
            _root, CardKind.Rule, CardScope.Section, "A constraint in a brief",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), ChangeName);

        var refused = AssertScopeRefused(outcome);
        Assert.Contains("a rule applying to one section is a constraint in a brief", refused.Reason, StringComparison.Ordinal);
        Assert.False(Directory.Exists(_root) && Directory.EnumerateFiles(_root, "*.md", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void CreateCard_Hazard_CarriesConditionAndCadence()
    {
        var registerFields = new RegisterCardFields("The API key rotates every 90 days", "monthly", null, null);

        var outcome = CardStore.CreateCard(
            _root, CardKind.Hazard, CardScope.Repository, "Rotating API key",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.Worker, "Body.", registerFields,
            Created, TimeSpan.FromSeconds(5), changeName: null);

        var created = AssertCreated(outcome);
        Assert.Equal("The API key rotates every 90 days", created.Card.RegisterFields.Condition);
        Assert.Equal("monthly", created.Card.RegisterFields.Cadence);
        Assert.Equal(Path.Combine(_root, "callboard", "register", "H-0001.md"), created.FilePath);

        var read = AssertParseSuccess(CardStore.ReadCard(created.FilePath));
        Assert.Equal("The API key rotates every 90 days", read.RegisterFields.Condition);
        Assert.Equal("monthly", read.RegisterFields.Cadence);
    }

    [Fact]
    public void CreateCard_Obligation_FixedChangeScope_Succeeds()
    {
        var outcome = CardStore.CreateCard(
            _root, CardKind.Obligation, CardScope.Change, "Settle the migration",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), ChangeName);

        var created = AssertCreated(outcome);
        Assert.Equal(CardKind.Obligation, created.Card.Frontmatter.Kind);
        Assert.Equal(CardScope.Change, created.Card.Frontmatter.Scope);
        Assert.Equal(Path.Combine(_root, "callboard", "changes", ChangeName, "O-0001.md"), created.FilePath);
    }

    [Fact]
    public void CreateCard_Decision_FixedCapabilityScope_Succeeds()
    {
        var outcome = CardStore.CreateCard(
            _root, CardKind.Decision, CardScope.Capability, "Adopt option A",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.ProductOwner, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), changeName: null);

        var created = AssertCreated(outcome);
        Assert.Equal(CardKind.Decision, created.Card.Frontmatter.Kind);
        Assert.Equal(CardScope.Capability, created.Card.Frontmatter.Scope);
        Assert.Equal(Path.Combine(_root, "callboard", "decisions", "D-0001.md"), created.FilePath);
    }

    [Fact]
    public void CreateCard_Section_FixedChangeScope_Succeeds_AndReadsBackAsSectionFlowStateOpen()
    {
        var outcome = CardStore.CreateCard(
            _root, CardKind.Section, CardScope.Change, "8. Review",
            SectionFlowState.Open.ToWireString(), CardOwner.Architect, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), ChangeName);

        var created = AssertCreated(outcome);
        Assert.True(SectionFlowStateWireFormat.TryParse(created.Card.Frontmatter.Status, out var state));
        Assert.Equal(SectionFlowState.Open, state);
        Assert.Equal(Path.Combine(_root, "callboard", "changes", ChangeName, "S-0001.md"), created.FilePath);
    }

    // 14.5: the caller can no longer name a path at all, so a target-already-exists refusal is now
    // reachable only when something else already occupies the exact name the allocator's next
    // identity resolves to — a hand-authored file sitting at that name, unindexed (this is exactly
    // the shape 14.5's brief names: a hand-authored card can still carry a name the tool would have
    // chosen; the tool itself can no longer produce the collision). Simulated by pre-creating the
    // file the first allocation for this fresh counter resolves to ('D-0001') before ever calling
    // CreateCard, rather than by supplying a caller path that collides with an earlier creation's
    // own path — that shape is no longer expressible.
    [Fact]
    public void CreateCard_TargetAlreadyExists_Refuses()
    {
        // The hand-authored file must itself be a valid, parseable card (§13 ruling 3:
        // CardIdentityAllocator fails shut on any unreadable file in the record, before this
        // scenario's AlreadyExists path is even reached) — and, per 14.5's own brief, its own id
        // must NOT be 'D-0001' (that would make it the exact B-0099-holding-B-0001 shape, which
        // resolves as IdentityAlreadyBorne, a different outcome this test does not exercise).
        var conflictingPath = Path.Combine(_root, "callboard", "decisions", "D-0001.md");
        Directory.CreateDirectory(Path.GetDirectoryName(conflictingPath)!);
        var handAuthored = new CardFile(
            new CardFrontmatter("D-9999", CardKind.Decision, "Hand-authored, wrong name", "open", CardOwner.ProductOwner, CardScope.Capability, "", Created, Created),
            "Body.", [], []);
        File.WriteAllText(conflictingPath, CardFileWriter.Serialize(handAuthored));

        var outcome = CardStore.CreateCard(
            _root, CardKind.Decision, CardScope.Capability, "First",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.ProductOwner, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), changeName: null);

        outcome.Match<object?>(
            onCreated: created => throw new Xunit.Sdk.XunitException($"expected AlreadyExists, got Created: '{created.FilePath}'"),
            onScopeRefused: refused => throw new Xunit.Sdk.XunitException($"expected AlreadyExists, got ScopeRefused: {refused.Reason}"),
            onAlreadyExists: static _ => null,
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected AlreadyExists, got LayoutMismatch: {layoutMismatch.Reason}"),
            onIdentityAlreadyBorne: borne => throw new Xunit.Sdk.XunitException($"expected AlreadyExists, got IdentityAlreadyBorne: '{borne.Id}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected AlreadyExists, got ToolFailure: {toolFailure.Reason}"));
    }

    // Scope is refused before any identity is allocated — a rejected create must not burn an
    // identity number a later, legitimate create would then skip over.
    [Fact]
    public void CreateCard_ScopeRefused_NeverAllocatesAnIdentity()
    {
        AssertScopeRefused(CardStore.CreateCard(
            _root, CardKind.Rule, CardScope.Section, "Bad",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), ChangeName));

        var created = AssertCreated(CardStore.CreateCard(
            _root, CardKind.Rule, CardScope.Repository, "Good",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), changeName: null));

        Assert.Equal("R-0001", created.Card.Frontmatter.Id);
    }

    // 14.5, card-model: "a caller names the container a card belongs in, never the file itself" —
    // for Change scope, the container name is '--change'. A missing one is refused, and refused
    // before any identity is burned, the same invariant CreateCard_ScopeRefused_NeverAllocatesAn
    // Identity proves for a refused scope.
    [Fact]
    public void CreateCard_ChangeScoped_MissingChangeName_RefusesAsLayoutMismatch_AndNeverAllocatesAnIdentity()
    {
        var refused = AssertLayoutMismatch(CardStore.CreateCard(
            _root, CardKind.Rule, CardScope.Change, "Needs a change",
            RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect, "Body.", registerFields: null,
            Created, TimeSpan.FromSeconds(5), changeName: null));
        Assert.Contains("a change name is required", refused.Reason, StringComparison.Ordinal);

        var created = AssertCreated(CardStore.CreateCard(
            _root, CardKind.Rule, CardScope.Repository, "Good",
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
            onIdentityAlreadyBorne: static borne => throw new Xunit.Sdk.XunitException($"expected Created, got IdentityAlreadyBorne: '{borne.Id}'"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Created, got ToolFailure: {toolFailure.Reason}"));

    private static CardCreateOutcome.ScopeRefused AssertScopeRefused(CardCreateOutcome outcome) =>
        outcome.Match(
            onCreated: static created => throw new Xunit.Sdk.XunitException($"expected ScopeRefused, got Created: '{created.Card.Frontmatter.Id}'"),
            onScopeRefused: static refused => refused,
            onAlreadyExists: static already => throw new Xunit.Sdk.XunitException($"expected ScopeRefused, got AlreadyExists: '{already.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected ScopeRefused, got LayoutMismatch: {layoutMismatch.Reason}"),
            onIdentityAlreadyBorne: static borne => throw new Xunit.Sdk.XunitException($"expected ScopeRefused, got IdentityAlreadyBorne: '{borne.Id}'"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected ScopeRefused, got ToolFailure: {toolFailure.Reason}"));

    private static CardCreateOutcome.LayoutMismatch AssertLayoutMismatch(CardCreateOutcome outcome) =>
        outcome.Match(
            onCreated: static created => throw new Xunit.Sdk.XunitException($"expected LayoutMismatch, got Created: '{created.Card.Frontmatter.Id}'"),
            onScopeRefused: static refused => throw new Xunit.Sdk.XunitException($"expected LayoutMismatch, got ScopeRefused: {refused.Reason}"),
            onAlreadyExists: static already => throw new Xunit.Sdk.XunitException($"expected LayoutMismatch, got AlreadyExists: '{already.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => layoutMismatch,
            onIdentityAlreadyBorne: static borne => throw new Xunit.Sdk.XunitException($"expected LayoutMismatch, got IdentityAlreadyBorne: '{borne.Id}'"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected LayoutMismatch, got ToolFailure: {toolFailure.Reason}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
