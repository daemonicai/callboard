using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 7.2 — <see cref="CardStore.SupersedeDecision"/>: the two-card write behind <c>decision
/// supersede</c> (register: "A decision MAY name the decision it supersedes and the decision that
/// supersedes it"). Covers the two-sided write, self-supersession, both already-discharged
/// directions (the pair that rules out a cycle — see <see cref="SupersedeDecision_ThreeNodeCycle_
/// TheClosingLinkRefuses"/>), wrong-kind, and — the spec's own load-bearing sentence — that the
/// superseded decision "remains retrievable" by id after being superseded.
/// </summary>
public sealed class CardDecisionSupersedeTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-decision-supersede-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _decisionsDirectory;

    public CardDecisionSupersedeTests()
    {
        _decisionsDirectory = Path.Combine(_root, CardLayout.DecisionsDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_decisionsDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void SupersedeDecision_TwoOpenDecisions_LinksBothSides_AndDischargesTheSupersededOne()
    {
        var supersedingPath = WriteDecisionCard("d-0001", "D-0001");
        var supersededPath = WriteDecisionCard("d-0002", "D-0002");

        var outcome = CardStore.SupersedeDecision(
            _root, supersedingPath, supersededPath, CardOwner.ProductOwner, Created.AddDays(1), TimeSpan.FromSeconds(5));

        var superseded = AssertSuperseded(outcome);
        Assert.Equal("D-0002", superseded.SupersedingCard.RegisterFields.Supersedes);
        Assert.Equal("open", superseded.SupersedingCard.Frontmatter.Status);
        Assert.Equal("discharged", superseded.SupersededCard.Frontmatter.Status);
        Assert.Equal("D-0001", superseded.SupersededCard.RegisterFields.SupersededBy);
        Assert.Equal(CardOwner.ProductOwner, superseded.SupersededCard.RegisterFields.DischargedBy);

        var supersedingRead = AssertParseSuccess(CardStore.ReadCard(supersedingPath));
        Assert.Equal("D-0002", supersedingRead.RegisterFields.Supersedes);

        var supersededRead = AssertParseSuccess(CardStore.ReadCard(supersededPath));
        Assert.Equal("discharged", supersededRead.Frontmatter.Status);
        Assert.Equal("D-0001", supersededRead.RegisterFields.SupersededBy);
    }

    // "remains retrievable" (register scenario) proven by execution: after supersession, the
    // superseded decision still resolves by id through the same resolver §7 block B shipped —
    // not deleted, not moved, not filtered out.
    [Fact]
    public void SupersedeDecision_SupersededDecision_StillResolvesByIdAfterwards()
    {
        var supersedingPath = WriteDecisionCard("d-0003", "D-0003");
        var supersededPath = WriteDecisionCard("d-0004", "D-0004");

        AssertSuperseded(CardStore.SupersedeDecision(
            _root, supersedingPath, supersededPath, CardOwner.ProductOwner, Created.AddDays(1), TimeSpan.FromSeconds(5)));

        var resolution = CardIdentityResolver.Resolve(_root, "D-0004");

        resolution.Match<object?>(
            onFound: (filePath, card) =>
            {
                Assert.Equal(supersededPath, filePath);
                Assert.Equal("discharged", card.Frontmatter.Status);
                Assert.Equal("D-0003", card.RegisterFields.SupersededBy);
                return null;
            },
            onNotFound: id => throw new Xunit.Sdk.XunitException($"expected Found, got NotFound: '{id}' — a superseded decision must remain retrievable by id"),
            onDuplicate: (id, filePaths) => throw new Xunit.Sdk.XunitException($"expected Found, got Duplicate: '{id}'"),
            onCorrupt: (id, files) => throw new Xunit.Sdk.XunitException($"expected Found, got Corrupt: '{id}'"),
            onUnreadable: (id, files) => throw new Xunit.Sdk.XunitException($"expected Found, got Unreadable: '{id}'"));
    }

    [Fact]
    public void SupersedeDecision_SameCardOnBothSides_Refuses_WithoutHangingOnItsOwnLock()
    {
        var path = WriteDecisionCard("d-0005", "D-0005");

        var outcome = CardStore.SupersedeDecision(_root, path, path, CardOwner.ProductOwner, Created, TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onSuperseded: superseded => throw new Xunit.Sdk.XunitException("expected SelfSupersession, got Superseded"),
            onSelfSupersession: static _ => null,
            onResolvedSelfSupersession: static id => throw new Xunit.Sdk.XunitException($"expected Superseded, got ResolvedSelfSupersession: '{id.Id}'"),
            onSupersededAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected SelfSupersession, got SupersededAlreadyDischarged: '{already.FilePath}'"),
            onSupersedingAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected SelfSupersession, got SupersedingAlreadyDischarged: '{already.FilePath}'"),
            onNotADecisionCard: notADecision => throw new Xunit.Sdk.XunitException($"expected SelfSupersession, got NotADecisionCard({notADecision.Kind.ToWireString()})"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected SelfSupersession, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected SelfSupersession, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected SelfSupersession, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected SelfSupersession, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected SelfSupersession, got ToolFailure: {toolFailure.Reason}"));
    }

    // §9 block A2 remediation, reviewer finding: the resolved (post-lock) branch was unexercised.
    // Two different path strings — the pre-lock check in SupersedeDecision cannot catch this — that
    // resolve to cards sharing the same id (a duplicate id across two files) reach the id-based
    // recheck in SupersedeDecisionUnderLocks once both cards are read and both locks are held.
    [Fact]
    public void SupersedeDecision_TwoDifferentPathsResolveToTheSameId_RefusesAsResolvedSelfSupersession_AndRecords()
    {
        var supersedingPath = WriteDecisionCard("d-0017", "D-0017");
        var supersededPath = Path.Combine(_decisionsDirectory, "d-0018.md");
        var supersededFrontmatter = new CardFrontmatter(
            "D-0017", CardKind.Decision, "Title", RegisterLifecycleState.Open.ToWireString(), CardOwner.ProductOwner,
            CardScope.Capability, string.Empty, Created, Created);
        File.WriteAllText(
            supersededPath,
            CardFileWriter.Serialize(new CardFile(supersededFrontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty)),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.SupersedeDecision(_root, supersedingPath, supersededPath, CardOwner.ProductOwner, Created, TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onSuperseded: superseded => throw new Xunit.Sdk.XunitException("expected ResolvedSelfSupersession, got Superseded"),
            onSelfSupersession: id => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfSupersession, got SelfSupersession: '{id.Id}'"),
            onResolvedSelfSupersession: static id => { Assert.Equal("D-0017", id.Id); return null; },
            onSupersededAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfSupersession, got SupersededAlreadyDischarged: '{already.FilePath}'"),
            onSupersedingAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfSupersession, got SupersedingAlreadyDischarged: '{already.FilePath}'"),
            onNotADecisionCard: notADecision => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfSupersession, got NotADecisionCard({notADecision.Kind.ToWireString()})"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfSupersession, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfSupersession, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfSupersession, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfSupersession, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfSupersession, got ToolFailure: {toolFailure.Reason}"));

        // process-enforcement (§9 block A2 remediation): both cards are resolved and locked, so
        // this — unlike the pre-lock path-string check — records, against the superseding card.
        var read = AssertParseSuccess(CardStore.ReadCard(supersedingPath));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.ProductOwner, recorded.By);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    [Fact]
    public void SupersedeDecision_TargetAlreadyDischarged_Refuses_NotARe_Supersession()
    {
        var first = WriteDecisionCard("d-0006", "D-0006");
        var second = WriteDecisionCard("d-0007", "D-0007");
        var third = WriteDecisionCard("d-0008", "D-0008");

        AssertSuperseded(CardStore.SupersedeDecision(_root, first, second, CardOwner.ProductOwner, Created.AddDays(1), TimeSpan.FromSeconds(5)));

        var outcome = CardStore.SupersedeDecision(_root, third, second, CardOwner.ProductOwner, Created.AddDays(2), TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onSuperseded: superseded => throw new Xunit.Sdk.XunitException("expected SupersededAlreadyDischarged, got Superseded"),
            onSelfSupersession: id => throw new Xunit.Sdk.XunitException($"expected SupersededAlreadyDischarged, got SelfSupersession: '{id.Id}'"),
            onResolvedSelfSupersession: id => throw new Xunit.Sdk.XunitException($"expected SupersededAlreadyDischarged, got ResolvedSelfSupersession: '{id.Id}'"),
            onSupersededAlreadyDischarged: static _ => null,
            onSupersedingAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected SupersededAlreadyDischarged, got SupersedingAlreadyDischarged: '{already.FilePath}'"),
            onNotADecisionCard: notADecision => throw new Xunit.Sdk.XunitException($"expected SupersededAlreadyDischarged, got NotADecisionCard({notADecision.Kind.ToWireString()})"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected SupersededAlreadyDischarged, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected SupersededAlreadyDischarged, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected SupersededAlreadyDischarged, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected SupersededAlreadyDischarged, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected SupersededAlreadyDischarged, got ToolFailure: {toolFailure.Reason}"));

        // process-enforcement (§9 block A2): decisions are repository-scoped, no changeName needed
        // to anchor — recorded against the card the refusal actually names (the already-discharged
        // "second"), not the acting "third".
        var read = AssertParseSuccess(CardStore.ReadCard(second));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.ProductOwner, recorded.By);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    // The check that closes the cycle: node B was discharged by A's own supersession above; B
    // cannot now act as the superseder for a third node C, because a discharged decision cannot
    // newly become another's successor. This is the "closing link" of any attempted 3-node cycle
    // A→B→C→A — see CardStore.SupersedeDecision's own doc comment for the general proof this is
    // one instance of.
    [Fact]
    public void SupersedeDecision_ThreeNodeCycle_TheClosingLinkRefuses()
    {
        var a = WriteDecisionCard("d-0010", "D-0010");
        var b = WriteDecisionCard("d-0011", "D-0011");
        var c = WriteDecisionCard("d-0012", "D-0012");

        // A supersedes B: A stays open, B is discharged.
        AssertSuperseded(CardStore.SupersedeDecision(_root, a, b, CardOwner.ProductOwner, Created.AddDays(1), TimeSpan.FromSeconds(5)));

        // B (already discharged) attempts to supersede C — the acting card is already
        // discharged, which must refuse regardless of C's own state.
        var outcome = CardStore.SupersedeDecision(_root, b, c, CardOwner.ProductOwner, Created.AddDays(2), TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onSuperseded: superseded => throw new Xunit.Sdk.XunitException("expected SupersedingAlreadyDischarged, got Superseded"),
            onSelfSupersession: id => throw new Xunit.Sdk.XunitException($"expected SupersedingAlreadyDischarged, got SelfSupersession: '{id.Id}'"),
            onResolvedSelfSupersession: id => throw new Xunit.Sdk.XunitException($"expected SupersedingAlreadyDischarged, got ResolvedSelfSupersession: '{id.Id}'"),
            onSupersededAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected SupersedingAlreadyDischarged, got SupersededAlreadyDischarged: '{already.FilePath}'"),
            onSupersedingAlreadyDischarged: static _ => null,
            onNotADecisionCard: notADecision => throw new Xunit.Sdk.XunitException($"expected SupersedingAlreadyDischarged, got NotADecisionCard({notADecision.Kind.ToWireString()})"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected SupersedingAlreadyDischarged, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected SupersedingAlreadyDischarged, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected SupersedingAlreadyDischarged, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected SupersedingAlreadyDischarged, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected SupersedingAlreadyDischarged, got ToolFailure: {toolFailure.Reason}"));

        // C was never touched — still open, still carries no supersedes/superseded_by.
        var cRead = AssertParseSuccess(CardStore.ReadCard(c));
        Assert.Equal("open", cRead.Frontmatter.Status);
        Assert.Null(cRead.RegisterFields.SupersededBy);
        Assert.Empty(cRead.Refusals);

        // process-enforcement (§9 block A2): recorded against B — the acting (already-discharged)
        // card the refusal is actually about.
        var bRead = AssertParseSuccess(CardStore.ReadCard(b));
        var recorded = Assert.Single(bRead.Refusals);
        Assert.Equal(CardOwner.ProductOwner, recorded.By);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    [Fact]
    public void SupersedeDecision_SupersedingCardIsNotADecision_Refuses()
    {
        var rulePath = Path.Combine(_decisionsDirectory, "r-0001.md");
        var ruleFrontmatter = new CardFrontmatter(
            "R-0001", CardKind.Rule, "Title", RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect,
            CardScope.Repository, string.Empty, Created, Created);
        File.WriteAllText(
            rulePath,
            CardFileWriter.Serialize(new CardFile(ruleFrontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty)),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var supersededPath = WriteDecisionCard("d-0013", "D-0013");

        var outcome = CardStore.SupersedeDecision(_root, rulePath, supersededPath, CardOwner.ProductOwner, Created, TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onSuperseded: superseded => throw new Xunit.Sdk.XunitException("expected NotADecisionCard, got Superseded"),
            onSelfSupersession: id => throw new Xunit.Sdk.XunitException($"expected NotADecisionCard, got SelfSupersession: '{id.Id}'"),
            onResolvedSelfSupersession: id => throw new Xunit.Sdk.XunitException($"expected NotADecisionCard, got ResolvedSelfSupersession: '{id.Id}'"),
            onSupersededAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected NotADecisionCard, got SupersededAlreadyDischarged: '{already.FilePath}'"),
            onSupersedingAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected NotADecisionCard, got SupersedingAlreadyDischarged: '{already.FilePath}'"),
            onNotADecisionCard: static n => { Assert.Equal(CardKind.Rule, n.Kind); return null; },
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected NotADecisionCard, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected NotADecisionCard, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected NotADecisionCard, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected NotADecisionCard, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected NotADecisionCard, got ToolFailure: {toolFailure.Reason}"));

        // process-enforcement (§9 block A2): this fixture's rule card declares scope 'repository'
        // (register.DirectoryFor -> RegisterDirectory) while physically living in
        // _decisionsDirectory (CardScope.Capability's own directory) — a genuine anchor mismatch,
        // not the ordinary case, so the refusal is reported but has nothing to anchor to and
        // records nothing (Architect ruling: "only a card-addressed refusal records").
        var ruleRead = AssertParseSuccess(CardStore.ReadCard(rulePath));
        Assert.Empty(ruleRead.Refusals);
    }

    // The reviewer's requested proof that NotADecisionCard records when the anchor actually
    // succeeds — the ordinary case (§9 block A2 remediation): unlike the fixture above, this rule
    // card's own declared scope (Capability) matches the directory it physically lives in.
    [Fact]
    public void SupersedeDecision_SupersedingCardIsNotADecision_ProperlyScoped_Refuses_AndRecords()
    {
        var rulePath = Path.Combine(_decisionsDirectory, "r-0002.md");
        var ruleFrontmatter = new CardFrontmatter(
            "R-0002", CardKind.Rule, "Title", RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect,
            CardScope.Capability, string.Empty, Created, Created);
        File.WriteAllText(
            rulePath,
            CardFileWriter.Serialize(new CardFile(ruleFrontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty)),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var supersededPath = WriteDecisionCard("d-0014", "D-0014");

        var outcome = CardStore.SupersedeDecision(_root, rulePath, supersededPath, CardOwner.ProductOwner, Created, TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onSuperseded: superseded => throw new Xunit.Sdk.XunitException("expected NotADecisionCard, got Superseded"),
            onSelfSupersession: id => throw new Xunit.Sdk.XunitException($"expected NotADecisionCard, got SelfSupersession: '{id.Id}'"),
            onResolvedSelfSupersession: id => throw new Xunit.Sdk.XunitException($"expected NotADecisionCard, got ResolvedSelfSupersession: '{id.Id}'"),
            onSupersededAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected NotADecisionCard, got SupersededAlreadyDischarged: '{already.FilePath}'"),
            onSupersedingAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected NotADecisionCard, got SupersedingAlreadyDischarged: '{already.FilePath}'"),
            onNotADecisionCard: static n => { Assert.Equal(CardKind.Rule, n.Kind); return null; },
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected NotADecisionCard, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected NotADecisionCard, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected NotADecisionCard, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected NotADecisionCard, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected NotADecisionCard, got ToolFailure: {toolFailure.Reason}"));

        // process-enforcement (§9 block A2 remediation): properly anchored this time, so recorded.
        var ruleRead = AssertParseSuccess(CardStore.ReadCard(rulePath));
        var recorded = Assert.Single(ruleRead.Refusals);
        Assert.Equal(CardOwner.ProductOwner, recorded.By);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    [Fact]
    // §12 block A ruling: register liveness closes at the parse door. A decision card carrying a
    // BlockFlowState value in its own status field is never constructed — CardFileParser refuses
    // it before SupersedeDecision ever runs, so this outcome's own (now unreachable)
    // InvalidStatus case is never produced; CardCorrupt reports the parser's reason instead
    // (§9.1: a parse-door refusal reports, it does not record).
    public void SupersedeDecision_SupersedingStatusIsAFlowState_ReportsCardCorrupt_WithoutRecording()
    {
        var supersedingPath = Path.Combine(_decisionsDirectory, "d-0015.md");
        var supersedingFrontmatter = new CardFrontmatter(
            "D-0015", CardKind.Decision, "Title", "briefed", CardOwner.ProductOwner, CardScope.Capability, string.Empty, Created, Created);
        var serialized = CardFileWriter.Serialize(new CardFile(supersedingFrontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty));
        File.WriteAllText(supersedingPath, serialized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var supersededPath = WriteDecisionCard("d-0016", "D-0016");

        var outcome = CardStore.SupersedeDecision(_root, supersedingPath, supersededPath, CardOwner.ProductOwner, Created, TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onSuperseded: superseded => throw new Xunit.Sdk.XunitException("expected CardCorrupt, got Superseded"),
            onSelfSupersession: id => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got SelfSupersession: '{id.Id}'"),
            onResolvedSelfSupersession: id => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got ResolvedSelfSupersession: '{id.Id}'"),
            onSupersededAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got SupersededAlreadyDischarged: '{already.FilePath}'"),
            onSupersedingAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got SupersedingAlreadyDischarged: '{already.FilePath}'"),
            onNotADecisionCard: n => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got NotADecisionCard({n.Kind.ToWireString()})"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt =>
            {
                Assert.Contains("status", corrupt.Reason, StringComparison.Ordinal);
                Assert.Contains("'briefed'", corrupt.Reason, StringComparison.Ordinal);
                Assert.Contains("'decision'", corrupt.Reason, StringComparison.Ordinal);
                Assert.Contains(RegisterLifecycleStateWireFormat.RecognisedValues, corrupt.Reason, StringComparison.Ordinal);
                return null;
            },
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got ToolFailure: {toolFailure.Reason}"));

        // Parse-door refusal reports; it does not record (§9.1).
        Assert.Equal(serialized, File.ReadAllText(supersedingPath));
    }

    private string WriteDecisionCard(string fileStem, string id)
    {
        var path = Path.Combine(_decisionsDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Decision, "Title", RegisterLifecycleState.Open.ToWireString(), CardOwner.ProductOwner,
            CardScope.Capability, string.Empty, Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static CardDecisionSupersedeOutcome.Superseded AssertSuperseded(CardDecisionSupersedeOutcome outcome) =>
        outcome.Match(
            onSuperseded: static superseded => superseded,
            onSelfSupersession: static id => throw new Xunit.Sdk.XunitException($"expected Superseded, got SelfSupersession: '{id.Id}'"),
            onResolvedSelfSupersession: id => throw new Xunit.Sdk.XunitException($"expected Superseded, got ResolvedSelfSupersession: '{id.Id}'"),
            onSupersededAlreadyDischarged: static already => throw new Xunit.Sdk.XunitException($"expected Superseded, got SupersededAlreadyDischarged: '{already.FilePath}'"),
            onSupersedingAlreadyDischarged: static already => throw new Xunit.Sdk.XunitException($"expected Superseded, got SupersedingAlreadyDischarged: '{already.FilePath}'"),
            onNotADecisionCard: static n => throw new Xunit.Sdk.XunitException($"expected Superseded, got NotADecisionCard({n.Kind.ToWireString()})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Superseded, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Superseded, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Superseded, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected Superseded, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Superseded, got ToolFailure: {toolFailure.Reason}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
