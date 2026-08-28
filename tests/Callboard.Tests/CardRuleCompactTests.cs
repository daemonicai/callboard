using System.Reflection;
using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 7.7 — <see cref="CardStore.CompactRules"/>: the N+1-card write behind <c>rule compact</c>
/// (register: "The system SHALL support compacting several rules into a family rule stating what
/// they share. A family rule SHALL record the rules it absorbs, and every absorbed rule SHALL
/// remain retrievable"). Covers the happy path (family gets <c>absorbs</c>, every member gets
/// <c>discharged</c>/<c>superseded_by</c>), retrievability by id afterwards, every refusal
/// (empty set, self-absorption, duplicate member, both already-discharged directions — the pair
/// that rules out a cycle, mirroring <see cref="CardDecisionSupersedeTests"/> — wrong-kind,
/// repository-scoped, a different change), and the restore helpers' own mechanics directly
/// (reflection — the same established pattern <c>CardFindingRecordTests</c> already uses for
/// <see cref="CardStore.RollbackRaisedCard"/>).
/// </summary>
public sealed class CardRuleCompactTests : IDisposable
{
    private const string ChangeName = "establish-callboard";
    private const string OtherChangeName = "some-other-change";
    private static readonly DateTimeOffset Created = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CompactedAt = Created.AddDays(2);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-rule-compact-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _changeDirectory;

    public CardRuleCompactTests()
    {
        _changeDirectory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_changeDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(_changeDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch (IOException)
                {
                }
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void CompactRules_TwoOpenRules_FamilyRecordsAbsorbs_MembersDischargedWithSupersededBy()
    {
        var familyPath = WriteRuleCard("r-0001", "R-0001", RegisterLifecycleState.Open, RegisterCardFields.Empty);
        var firstPath = WriteRuleCard("r-0002", "R-0002", RegisterLifecycleState.Open, RegisterCardFields.Empty);
        var secondPath = WriteRuleCard("r-0003", "R-0003", RegisterLifecycleState.Open, RegisterCardFields.Empty);

        var outcome = CardStore.CompactRules(
            _root, familyPath, [firstPath, secondPath], ChangeName, CardOwner.Architect, CompactedAt, TimeSpan.FromSeconds(5));

        var compacted = AssertCompacted(outcome);
        Assert.Equal(["R-0002", "R-0003"], compacted.FamilyCard.RegisterFields.Absorbs);
        Assert.Equal("open", compacted.FamilyCard.Frontmatter.Status);
        Assert.Equal(2, compacted.AbsorbedCards.Count);
        foreach (var absorbed in compacted.AbsorbedCards)
        {
            Assert.Equal("discharged", absorbed.Frontmatter.Status);
            Assert.Equal("R-0001", absorbed.RegisterFields.SupersededBy);
            Assert.Equal(CardOwner.Architect, absorbed.RegisterFields.DischargedBy);
            Assert.Equal(CompactedAt, absorbed.RegisterFields.DischargedAt);
        }

        var familyOnDisk = AssertParseSuccess(CardStore.ReadCard(familyPath));
        Assert.Equal(["R-0002", "R-0003"], familyOnDisk.RegisterFields.Absorbs);
        var firstOnDisk = AssertParseSuccess(CardStore.ReadCard(firstPath));
        Assert.Equal("discharged", firstOnDisk.Frontmatter.Status);
        var secondOnDisk = AssertParseSuccess(CardStore.ReadCard(secondPath));
        Assert.Equal("discharged", secondOnDisk.Frontmatter.Status);
    }

    // "every absorbed rule SHALL remain retrievable" (register scenario), proven by execution:
    // after compaction, an absorbed rule still resolves by id through the same resolver §7 block B
    // shipped — not deleted, not moved, not filtered out — the same proof CardDecisionSupersedeTests
    // gives a superseded decision.
    [Fact]
    public void CompactRules_AbsorbedRule_StillResolvesByIdAfterwards()
    {
        var familyPath = WriteRuleCard("r-0004", "R-0004", RegisterLifecycleState.Open, RegisterCardFields.Empty);
        var absorbedPath = WriteRuleCard("r-0005", "R-0005", RegisterLifecycleState.Open, RegisterCardFields.Empty);

        AssertCompacted(CardStore.CompactRules(
            _root, familyPath, [absorbedPath], ChangeName, CardOwner.Architect, CompactedAt, TimeSpan.FromSeconds(5)));

        var resolution = CardIdentityResolver.Resolve(_root, "R-0005");

        resolution.Match<object?>(
            onFound: (filePath, card) =>
            {
                Assert.Equal(absorbedPath, filePath);
                Assert.Equal("discharged", card.Frontmatter.Status);
                Assert.Equal("R-0004", card.RegisterFields.SupersededBy);
                return null;
            },
            onNotFound: id => throw new Xunit.Sdk.XunitException($"expected Found, got NotFound: '{id}' — an absorbed rule must remain retrievable by id"),
            onDuplicate: (id, filePaths) => throw new Xunit.Sdk.XunitException($"expected Found, got Duplicate: '{id}'"),
            onCorrupt: (id, files) => throw new Xunit.Sdk.XunitException($"expected Found, got Corrupt: '{id}'"),
            onUnreadable: (id, files) => throw new Xunit.Sdk.XunitException($"expected Found, got Unreadable: '{id}'"));
    }

    // Architect ruling (§7 block F remediation): enforced in CompactRules itself, ahead of every
    // other check — a non-architect role refuses even with an empty absorb set, proving the role
    // check runs first, not merely that it runs at all.
    [Fact]
    public void CompactRules_ActingRoleIsNotArchitect_Refuses_BeforeAnyOtherCheck()
    {
        var outcome = CardStore.CompactRules(
            _root, "irrelevant-family-path", [], ChangeName, CardOwner.Worker, CompactedAt, TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onCompacted: static _ => throw new Xunit.Sdk.XunitException("expected RoleNotPermitted, got Compacted"),
            onRoleNotPermitted: static roleNotPermitted =>
            {
                Assert.Equal(CardOwner.Worker, roleNotPermitted.AttemptedRole);
                Assert.Equal(CardOwner.Architect, roleNotPermitted.RequiredRole);
                return null;
            },
            onEmptyAbsorbSet: static _ => throw new Xunit.Sdk.XunitException("expected RoleNotPermitted, got EmptyAbsorbSet — the role check must run first."),
            onSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected RoleNotPermitted, got SelfAbsorption: '{id.Id}'"),
            onResolvedSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected RoleNotPermitted, got ResolvedSelfAbsorption: '{id.Id}'"),
            onDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected RoleNotPermitted, got DuplicateAbsorbedRule: '{id.Id}'"),
            onResolvedDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected RoleNotPermitted, got ResolvedDuplicateAbsorbedRule: '{id.Id}'"),
            onFamilyAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected RoleNotPermitted, got FamilyAlreadyDischarged: '{already.FilePath}'"),
            onAbsorbedAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected RoleNotPermitted, got AbsorbedAlreadyDischarged: '{already.FilePath}'"),
            onNotARuleCard: n => throw new Xunit.Sdk.XunitException($"expected RoleNotPermitted, got NotARuleCard({n.Kind.ToWireString()})"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected RoleNotPermitted, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected RoleNotPermitted, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected RoleNotPermitted, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected RoleNotPermitted, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected RoleNotPermitted, got ToolFailure: {toolFailure.Reason}"));
    }

    [Fact]
    public void CompactRules_EmptyAbsorbSet_Refuses()
    {
        var familyPath = WriteRuleCard("r-0006", "R-0006", RegisterLifecycleState.Open, RegisterCardFields.Empty);

        var outcome = CardStore.CompactRules(
            _root, familyPath, [], ChangeName, CardOwner.Architect, CompactedAt, TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onCompacted: static _ => throw new Xunit.Sdk.XunitException("expected EmptyAbsorbSet, got Compacted"),
            onRoleNotPermitted: id => throw new Xunit.Sdk.XunitException($"expected EmptyAbsorbSet, got RoleNotPermitted: '{id.AttemptedRole.ToWireString()}'"),
            onEmptyAbsorbSet: static _ => null,
            onSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected EmptyAbsorbSet, got SelfAbsorption: '{id.Id}'"),
            onResolvedSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected EmptyAbsorbSet, got ResolvedSelfAbsorption: '{id.Id}'"),
            onDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected EmptyAbsorbSet, got DuplicateAbsorbedRule: '{id.Id}'"),
            onResolvedDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected EmptyAbsorbSet, got ResolvedDuplicateAbsorbedRule: '{id.Id}'"),
            onFamilyAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected EmptyAbsorbSet, got FamilyAlreadyDischarged: '{already.FilePath}'"),
            onAbsorbedAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected EmptyAbsorbSet, got AbsorbedAlreadyDischarged: '{already.FilePath}'"),
            onNotARuleCard: n => throw new Xunit.Sdk.XunitException($"expected EmptyAbsorbSet, got NotARuleCard({n.Kind.ToWireString()})"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected EmptyAbsorbSet, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected EmptyAbsorbSet, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected EmptyAbsorbSet, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected EmptyAbsorbSet, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected EmptyAbsorbSet, got ToolFailure: {toolFailure.Reason}"));
    }

    [Fact]
    public void CompactRules_FamilyNamesItself_Refuses_WithoutHangingOnItsOwnLock()
    {
        var path = WriteRuleCard("r-0007", "R-0007", RegisterLifecycleState.Open, RegisterCardFields.Empty);

        var outcome = CardStore.CompactRules(
            _root, path, [path], ChangeName, CardOwner.Architect, CompactedAt, TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onCompacted: static _ => throw new Xunit.Sdk.XunitException("expected SelfAbsorption, got Compacted"),
            onRoleNotPermitted: id => throw new Xunit.Sdk.XunitException($"expected SelfAbsorption, got RoleNotPermitted: '{id.AttemptedRole.ToWireString()}'"),
            onEmptyAbsorbSet: static _ => throw new Xunit.Sdk.XunitException("expected SelfAbsorption, got EmptyAbsorbSet"),
            onSelfAbsorption: static _ => null,
            onResolvedSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected Compacted, got ResolvedSelfAbsorption: '{id.Id}'"),
            onDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected SelfAbsorption, got DuplicateAbsorbedRule: '{id.Id}'"),
            onResolvedDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected SelfAbsorption, got ResolvedDuplicateAbsorbedRule: '{id.Id}'"),
            onFamilyAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected SelfAbsorption, got FamilyAlreadyDischarged: '{already.FilePath}'"),
            onAbsorbedAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected SelfAbsorption, got AbsorbedAlreadyDischarged: '{already.FilePath}'"),
            onNotARuleCard: n => throw new Xunit.Sdk.XunitException($"expected SelfAbsorption, got NotARuleCard({n.Kind.ToWireString()})"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected SelfAbsorption, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected SelfAbsorption, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected SelfAbsorption, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected SelfAbsorption, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected SelfAbsorption, got ToolFailure: {toolFailure.Reason}"));
    }

    [Fact]
    public void CompactRules_SameRuleNamedTwiceInAbsorbSet_Refuses_WithoutHangingOnItsOwnLock()
    {
        var familyPath = WriteRuleCard("r-0008", "R-0008", RegisterLifecycleState.Open, RegisterCardFields.Empty);
        var absorbedPath = WriteRuleCard("r-0009", "R-0009", RegisterLifecycleState.Open, RegisterCardFields.Empty);

        var outcome = CardStore.CompactRules(
            _root, familyPath, [absorbedPath, absorbedPath], ChangeName, CardOwner.Architect, CompactedAt, TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onCompacted: static _ => throw new Xunit.Sdk.XunitException("expected DuplicateAbsorbedRule, got Compacted"),
            onRoleNotPermitted: id => throw new Xunit.Sdk.XunitException($"expected DuplicateAbsorbedRule, got RoleNotPermitted: '{id.AttemptedRole.ToWireString()}'"),
            onEmptyAbsorbSet: static _ => throw new Xunit.Sdk.XunitException("expected DuplicateAbsorbedRule, got EmptyAbsorbSet"),
            onSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected DuplicateAbsorbedRule, got SelfAbsorption: '{id.Id}'"),
            onResolvedSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected DuplicateAbsorbedRule, got ResolvedSelfAbsorption: '{id.Id}'"),
            onDuplicateAbsorbedRule: static _ => null,
            onResolvedDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected Compacted, got ResolvedDuplicateAbsorbedRule: '{id.Id}'"),
            onFamilyAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected DuplicateAbsorbedRule, got FamilyAlreadyDischarged: '{already.FilePath}'"),
            onAbsorbedAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected DuplicateAbsorbedRule, got AbsorbedAlreadyDischarged: '{already.FilePath}'"),
            onNotARuleCard: n => throw new Xunit.Sdk.XunitException($"expected DuplicateAbsorbedRule, got NotARuleCard({n.Kind.ToWireString()})"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected DuplicateAbsorbedRule, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected DuplicateAbsorbedRule, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected DuplicateAbsorbedRule, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected DuplicateAbsorbedRule, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected DuplicateAbsorbedRule, got ToolFailure: {toolFailure.Reason}"));
    }

    // §9 block A2 remediation, reviewer finding: the resolved (post-lock) branches were
    // unexercised. Two different absorbed-set path strings — CompactRules's own pre-lock
    // path-string checks cannot catch this — resolving to cards that share an id (a duplicate id
    // across two files, or one colliding with the family's own) reach the id-based recheck in
    // CompactRulesUnderLocks once every card is read and every lock is held.
    [Fact]
    public void CompactRules_AbsorbedCardSharesTheFamilysId_RefusesAsResolvedSelfAbsorption_AndRecords()
    {
        var familyPath = WriteRuleCard("r-0020", "R-0020", RegisterLifecycleState.Open, RegisterCardFields.Empty);
        var absorbedPath = Path.Combine(_changeDirectory, "r-0021.md");
        var absorbedFrontmatter = new CardFrontmatter(
            "R-0020", CardKind.Rule, "Title", RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect,
            CardScope.Change, string.Empty, Created, Created);
        File.WriteAllText(
            absorbedPath,
            CardFileWriter.Serialize(new CardFile(absorbedFrontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty)),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.CompactRules(
            _root, familyPath, [absorbedPath], ChangeName, CardOwner.Architect, CompactedAt, TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onCompacted: static _ => throw new Xunit.Sdk.XunitException("expected ResolvedSelfAbsorption, got Compacted"),
            onRoleNotPermitted: id => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfAbsorption, got RoleNotPermitted: '{id.AttemptedRole.ToWireString()}'"),
            onEmptyAbsorbSet: static _ => throw new Xunit.Sdk.XunitException("expected ResolvedSelfAbsorption, got EmptyAbsorbSet"),
            onSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfAbsorption, got SelfAbsorption: '{id.Id}'"),
            onResolvedSelfAbsorption: static id => { Assert.Equal("R-0020", id.Id); return null; },
            onDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfAbsorption, got DuplicateAbsorbedRule: '{id.Id}'"),
            onResolvedDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfAbsorption, got ResolvedDuplicateAbsorbedRule: '{id.Id}'"),
            onFamilyAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfAbsorption, got FamilyAlreadyDischarged: '{already.FilePath}'"),
            onAbsorbedAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfAbsorption, got AbsorbedAlreadyDischarged: '{already.FilePath}'"),
            onNotARuleCard: n => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfAbsorption, got NotARuleCard({n.Kind.ToWireString()})"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfAbsorption, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfAbsorption, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfAbsorption, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfAbsorption, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected ResolvedSelfAbsorption, got ToolFailure: {toolFailure.Reason}"));

        // process-enforcement (§9 block A2 remediation): recorded against the absorbed card that
        // actually collided, not the family.
        var read = AssertParseSuccess(CardStore.ReadCard(absorbedPath));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    [Fact]
    public void CompactRules_TwoAbsorbedPathsShareAnId_RefusesAsResolvedDuplicateAbsorbedRule_AndRecords()
    {
        var familyPath = WriteRuleCard("r-0022", "R-0022", RegisterLifecycleState.Open, RegisterCardFields.Empty);
        var firstAbsorbedPath = WriteRuleCard("r-0023", "R-0023", RegisterLifecycleState.Open, RegisterCardFields.Empty);
        var secondAbsorbedPath = Path.Combine(_changeDirectory, "r-0024.md");
        var secondAbsorbedFrontmatter = new CardFrontmatter(
            "R-0023", CardKind.Rule, "Title", RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect,
            CardScope.Change, string.Empty, Created, Created);
        File.WriteAllText(
            secondAbsorbedPath,
            CardFileWriter.Serialize(new CardFile(secondAbsorbedFrontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty)),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.CompactRules(
            _root, familyPath, [firstAbsorbedPath, secondAbsorbedPath], ChangeName, CardOwner.Architect, CompactedAt, TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onCompacted: static _ => throw new Xunit.Sdk.XunitException("expected ResolvedDuplicateAbsorbedRule, got Compacted"),
            onRoleNotPermitted: id => throw new Xunit.Sdk.XunitException($"expected ResolvedDuplicateAbsorbedRule, got RoleNotPermitted: '{id.AttemptedRole.ToWireString()}'"),
            onEmptyAbsorbSet: static _ => throw new Xunit.Sdk.XunitException("expected ResolvedDuplicateAbsorbedRule, got EmptyAbsorbSet"),
            onSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected ResolvedDuplicateAbsorbedRule, got SelfAbsorption: '{id.Id}'"),
            onResolvedSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected ResolvedDuplicateAbsorbedRule, got ResolvedSelfAbsorption: '{id.Id}'"),
            onDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected ResolvedDuplicateAbsorbedRule, got DuplicateAbsorbedRule: '{id.Id}'"),
            onResolvedDuplicateAbsorbedRule: static id => { Assert.Equal("R-0023", id.Id); return null; },
            onFamilyAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected ResolvedDuplicateAbsorbedRule, got FamilyAlreadyDischarged: '{already.FilePath}'"),
            onAbsorbedAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected ResolvedDuplicateAbsorbedRule, got AbsorbedAlreadyDischarged: '{already.FilePath}'"),
            onNotARuleCard: n => throw new Xunit.Sdk.XunitException($"expected ResolvedDuplicateAbsorbedRule, got NotARuleCard({n.Kind.ToWireString()})"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected ResolvedDuplicateAbsorbedRule, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected ResolvedDuplicateAbsorbedRule, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected ResolvedDuplicateAbsorbedRule, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected ResolvedDuplicateAbsorbedRule, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected ResolvedDuplicateAbsorbedRule, got ToolFailure: {toolFailure.Reason}"));

        // process-enforcement (§9 block A2 remediation): recorded against the second-seen absorbed
        // card — the one whose id collided with one already accepted into the set.
        var read = AssertParseSuccess(CardStore.ReadCard(secondAbsorbedPath));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    [Fact]
    public void CompactRules_TargetAlreadyDischarged_Refuses_NotARe_Absorption()
    {
        var firstFamilyPath = WriteRuleCard("r-0010", "R-0010", RegisterLifecycleState.Open, RegisterCardFields.Empty);
        var memberPath = WriteRuleCard("r-0011", "R-0011", RegisterLifecycleState.Open, RegisterCardFields.Empty);
        var secondFamilyPath = WriteRuleCard("r-0012", "R-0012", RegisterLifecycleState.Open, RegisterCardFields.Empty);

        AssertCompacted(CardStore.CompactRules(
            _root, firstFamilyPath, [memberPath], ChangeName, CardOwner.Architect, CompactedAt, TimeSpan.FromSeconds(5)));

        var outcome = CardStore.CompactRules(
            _root, secondFamilyPath, [memberPath], ChangeName, CardOwner.Architect, CompactedAt.AddDays(1), TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onCompacted: static _ => throw new Xunit.Sdk.XunitException("expected AbsorbedAlreadyDischarged, got Compacted"),
            onRoleNotPermitted: id => throw new Xunit.Sdk.XunitException($"expected AbsorbedAlreadyDischarged, got RoleNotPermitted: '{id.AttemptedRole.ToWireString()}'"),
            onEmptyAbsorbSet: static _ => throw new Xunit.Sdk.XunitException("expected AbsorbedAlreadyDischarged, got EmptyAbsorbSet"),
            onSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected AbsorbedAlreadyDischarged, got SelfAbsorption: '{id.Id}'"),
            onResolvedSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected AbsorbedAlreadyDischarged, got ResolvedSelfAbsorption: '{id.Id}'"),
            onDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected AbsorbedAlreadyDischarged, got DuplicateAbsorbedRule: '{id.Id}'"),
            onResolvedDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected AbsorbedAlreadyDischarged, got ResolvedDuplicateAbsorbedRule: '{id.Id}'"),
            onFamilyAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected AbsorbedAlreadyDischarged, got FamilyAlreadyDischarged: '{already.FilePath}'"),
            onAbsorbedAlreadyDischarged: static _ => null,
            onNotARuleCard: n => throw new Xunit.Sdk.XunitException($"expected AbsorbedAlreadyDischarged, got NotARuleCard({n.Kind.ToWireString()})"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected AbsorbedAlreadyDischarged, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected AbsorbedAlreadyDischarged, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected AbsorbedAlreadyDischarged, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected AbsorbedAlreadyDischarged, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected AbsorbedAlreadyDischarged, got ToolFailure: {toolFailure.Reason}"));

        // process-enforcement (§9 block A2): recorded against the member card the refusal names.
        var read = AssertParseSuccess(CardStore.ReadCard(memberPath));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    // The check that closes the cycle: node B was discharged by A's own compaction above; B cannot
    // now act as a family absorbing C, because a discharged rule cannot newly act as a family. This
    // is the "closing link" of any attempted 3-node cycle A absorbs B, B absorbs C, C absorbs A —
    // see CardStore.CompactRules's own doc comment for the general proof (item 5, "does the same
    // argument cover a family absorbing a family") this is one instance of.
    [Fact]
    public void CompactRules_ThreeNodeCycle_TheClosingLinkRefuses()
    {
        var a = WriteRuleCard("r-0013", "R-0013", RegisterLifecycleState.Open, RegisterCardFields.Empty);
        var b = WriteRuleCard("r-0014", "R-0014", RegisterLifecycleState.Open, RegisterCardFields.Empty);
        var c = WriteRuleCard("r-0015", "R-0015", RegisterLifecycleState.Open, RegisterCardFields.Empty);

        // A absorbs B: A stays open (and is itself now a family), B is discharged.
        AssertCompacted(CardStore.CompactRules(
            _root, a, [b], ChangeName, CardOwner.Architect, CompactedAt, TimeSpan.FromSeconds(5)));

        // B (already discharged, having been absorbed by A) attempts to absorb C — the acting
        // (family) side is already discharged, which must refuse regardless of C's own state.
        var outcome = CardStore.CompactRules(
            _root, b, [c], ChangeName, CardOwner.Architect, CompactedAt.AddDays(1), TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onCompacted: static _ => throw new Xunit.Sdk.XunitException("expected FamilyAlreadyDischarged, got Compacted"),
            onRoleNotPermitted: id => throw new Xunit.Sdk.XunitException($"expected FamilyAlreadyDischarged, got RoleNotPermitted: '{id.AttemptedRole.ToWireString()}'"),
            onEmptyAbsorbSet: static _ => throw new Xunit.Sdk.XunitException("expected FamilyAlreadyDischarged, got EmptyAbsorbSet"),
            onSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected FamilyAlreadyDischarged, got SelfAbsorption: '{id.Id}'"),
            onResolvedSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected FamilyAlreadyDischarged, got ResolvedSelfAbsorption: '{id.Id}'"),
            onDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected FamilyAlreadyDischarged, got DuplicateAbsorbedRule: '{id.Id}'"),
            onResolvedDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected FamilyAlreadyDischarged, got ResolvedDuplicateAbsorbedRule: '{id.Id}'"),
            onFamilyAlreadyDischarged: static _ => null,
            onAbsorbedAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected FamilyAlreadyDischarged, got AbsorbedAlreadyDischarged: '{already.FilePath}'"),
            onNotARuleCard: n => throw new Xunit.Sdk.XunitException($"expected FamilyAlreadyDischarged, got NotARuleCard({n.Kind.ToWireString()})"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected FamilyAlreadyDischarged, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected FamilyAlreadyDischarged, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected FamilyAlreadyDischarged, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected FamilyAlreadyDischarged, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected FamilyAlreadyDischarged, got ToolFailure: {toolFailure.Reason}"));

        // C was never touched — still open, still carries no absorbs/superseded_by.
        var cRead = AssertParseSuccess(CardStore.ReadCard(c));
        Assert.Equal("open", cRead.Frontmatter.Status);
        Assert.Null(cRead.RegisterFields.SupersededBy);
        Assert.Empty(cRead.Refusals);

        // process-enforcement (§9 block A2): recorded against B — the acting (already-discharged)
        // family side the refusal is actually about.
        var bRead = AssertParseSuccess(CardStore.ReadCard(b));
        var recorded = Assert.Single(bRead.Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    [Fact]
    public void CompactRules_FamilyIsNotARule_Refuses()
    {
        var obligationPath = Path.Combine(_changeDirectory, "o-0001.md");
        var obligationFrontmatter = new CardFrontmatter(
            "O-0001", CardKind.Obligation, "Settle it", RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect,
            CardScope.Change, string.Empty, Created, Created);
        File.WriteAllText(
            obligationPath,
            CardFileWriter.Serialize(new CardFile(obligationFrontmatter, "Body.", [], [], RegisterFields: new RegisterCardFields(null, null, null, null, OwedBy: "S-0001"))),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var memberPath = WriteRuleCard("r-0016", "R-0016", RegisterLifecycleState.Open, RegisterCardFields.Empty);

        var outcome = CardStore.CompactRules(
            _root, obligationPath, [memberPath], ChangeName, CardOwner.Architect, CompactedAt, TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onCompacted: static _ => throw new Xunit.Sdk.XunitException("expected NotARuleCard, got Compacted"),
            onRoleNotPermitted: id => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got RoleNotPermitted: '{id.AttemptedRole.ToWireString()}'"),
            onEmptyAbsorbSet: static _ => throw new Xunit.Sdk.XunitException("expected NotARuleCard, got EmptyAbsorbSet"),
            onSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got SelfAbsorption: '{id.Id}'"),
            onResolvedSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got ResolvedSelfAbsorption: '{id.Id}'"),
            onDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got DuplicateAbsorbedRule: '{id.Id}'"),
            onResolvedDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got ResolvedDuplicateAbsorbedRule: '{id.Id}'"),
            onFamilyAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got FamilyAlreadyDischarged: '{already.FilePath}'"),
            onAbsorbedAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got AbsorbedAlreadyDischarged: '{already.FilePath}'"),
            onNotARuleCard: static n => { Assert.Equal(CardKind.Obligation, n.Kind); return null; },
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got ToolFailure: {toolFailure.Reason}"));

        // process-enforcement (§9 block A2): recorded against the wrongly-named family side.
        var read = AssertParseSuccess(CardStore.ReadCard(obligationPath));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    // §12 block A ruling: register liveness closes at the parse door. A rule card carrying a
    // BlockFlowState value in its own status field is never constructed — CardFileParser refuses
    // it before CompactRules ever runs, so this outcome's own (now unreachable) InvalidStatus case
    // is never produced; CardCorrupt reports the parser's reason instead (§9.1: a parse-door
    // refusal reports, it does not record).
    [Fact]
    public void CompactRules_FamilyStatusIsAFlowState_ReportsCardCorrupt_WithoutRecording()
    {
        var familyPath = Path.Combine(_changeDirectory, "r-0018.md");
        var familyFrontmatter = new CardFrontmatter(
            "R-0018", CardKind.Rule, "Title", "briefed", CardOwner.Architect, CardScope.Change, string.Empty, Created, Created);
        var serialized = CardFileWriter.Serialize(new CardFile(familyFrontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty));
        File.WriteAllText(familyPath, serialized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var memberPath = WriteRuleCard("r-0019", "R-0019", RegisterLifecycleState.Open, RegisterCardFields.Empty);

        var outcome = CardStore.CompactRules(
            _root, familyPath, [memberPath], ChangeName, CardOwner.Architect, CompactedAt, TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onCompacted: static _ => throw new Xunit.Sdk.XunitException("expected CardCorrupt, got Compacted"),
            onRoleNotPermitted: id => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got RoleNotPermitted: '{id.AttemptedRole.ToWireString()}'"),
            onEmptyAbsorbSet: static _ => throw new Xunit.Sdk.XunitException("expected CardCorrupt, got EmptyAbsorbSet"),
            onSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got SelfAbsorption: '{id.Id}'"),
            onResolvedSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got ResolvedSelfAbsorption: '{id.Id}'"),
            onDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got DuplicateAbsorbedRule: '{id.Id}'"),
            onResolvedDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got ResolvedDuplicateAbsorbedRule: '{id.Id}'"),
            onFamilyAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got FamilyAlreadyDischarged: '{already.FilePath}'"),
            onAbsorbedAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got AbsorbedAlreadyDischarged: '{already.FilePath}'"),
            onNotARuleCard: n => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got NotARuleCard({n.Kind.ToWireString()})"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt =>
            {
                Assert.Contains("status", corrupt.Reason, StringComparison.Ordinal);
                Assert.Contains("'briefed'", corrupt.Reason, StringComparison.Ordinal);
                Assert.Contains("'rule'", corrupt.Reason, StringComparison.Ordinal);
                Assert.Contains(RegisterLifecycleStateWireFormat.RecognisedValues, corrupt.Reason, StringComparison.Ordinal);
                return null;
            },
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected CardCorrupt, got ToolFailure: {toolFailure.Reason}"));

        // Parse-door refusal reports; it does not record (§9.1).
        Assert.Equal(serialized, File.ReadAllText(familyPath));
    }

    // This block's own scope restriction (brief item 6/register: repository-scoped compaction is
    // proposed and decided by the Product Owner, block G's territory) — a repository-scoped rule
    // refuses before any write, surfaced as LayoutMismatch since AnchoredCardPath.TryCreate cannot
    // anchor a repository-scoped card against CardScope.Change.
    [Fact]
    public void CompactRules_RepositoryScopedFamily_Refuses()
    {
        var registerDirectory = Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(registerDirectory);
        var familyPath = Path.Combine(registerDirectory, "r-0017.md");
        WriteRuleCardAt(familyPath, "R-0017", CardScope.Repository, RegisterLifecycleState.Open, RegisterCardFields.Empty);
        var memberPath = WriteRuleCard("r-0018", "R-0018", RegisterLifecycleState.Open, RegisterCardFields.Empty);

        var outcome = CardStore.CompactRules(
            _root, familyPath, [memberPath], ChangeName, CardOwner.Architect, CompactedAt, TimeSpan.FromSeconds(5));

        AssertLayoutMismatch(outcome);
        Assert.True(File.Exists(familyPath));
        var memberOnDisk = AssertParseSuccess(CardStore.ReadCard(memberPath));
        Assert.Equal("open", memberOnDisk.Frontmatter.Status);
    }

    // A change-scoped rule genuinely belonging to a *different* change than the one named — the
    // anchor check (AnchoredCardPath.TryCreate) is what confirms every compacted rule actually
    // belongs to the named change, not merely that it is change-scoped somewhere.
    [Fact]
    public void CompactRules_MemberBelongsToADifferentChange_Refuses()
    {
        var familyPath = WriteRuleCard("r-0019", "R-0019", RegisterLifecycleState.Open, RegisterCardFields.Empty);

        var otherChangeDirectory = Path.Combine(_root, CardLayout.ChangesDirectory(OtherChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(otherChangeDirectory);
        var memberPath = Path.Combine(otherChangeDirectory, "r-0020.md");
        WriteRuleCardAt(memberPath, "R-0020", CardScope.Change, RegisterLifecycleState.Open, RegisterCardFields.Empty);

        var outcome = CardStore.CompactRules(
            _root, familyPath, [memberPath], ChangeName, CardOwner.Architect, CompactedAt, TimeSpan.FromSeconds(5));

        AssertLayoutMismatch(outcome);
        Assert.True(File.Exists(memberPath));
        var memberOnDisk = AssertParseSuccess(CardStore.ReadCard(memberPath));
        Assert.Equal("open", memberOnDisk.Frontmatter.Status);
    }

    // The failure guarantee, exercised for real (not merely argued): the change directory is made
    // read-only before any write is attempted, so the very first absorbed write fails. Nothing
    // written — the family and every member are left exactly as they were, and the caller sees
    // ToolFailure, not a corrupted partial write. Symmetric proof that a later index's write would
    // fail the identical way is the direct reflection tests on RestoreCardContent/RestoreAllAbsorbed
    // below (same discipline CardFindingRecordTests' RollbackRaisedCard tests already use).
    [Fact]
    public void CompactRules_DirectoryDeniesWriting_Refuses_LeavingEveryCardUntouched()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var familyPath = WriteRuleCard("r-0021", "R-0021", RegisterLifecycleState.Open, RegisterCardFields.Empty);
        var firstPath = WriteRuleCard("r-0022", "R-0022", RegisterLifecycleState.Open, RegisterCardFields.Empty);
        var secondPath = WriteRuleCard("r-0023", "R-0023", RegisterLifecycleState.Open, RegisterCardFields.Empty);
        var familyBytes = File.ReadAllBytes(familyPath);
        var firstBytes = File.ReadAllBytes(firstPath);
        var secondBytes = File.ReadAllBytes(secondPath);

        File.SetUnixFileMode(_changeDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var outcome = CardStore.CompactRules(
                _root, familyPath, [firstPath, secondPath], ChangeName, CardOwner.Architect, CompactedAt, TimeSpan.FromSeconds(5));

            var toolFailure = outcome.Match(
                onCompacted: static _ => throw new Xunit.Sdk.XunitException("expected ToolFailure, got Compacted"),
                onRoleNotPermitted: id => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got RoleNotPermitted: '{id.AttemptedRole.ToWireString()}'"),
                onEmptyAbsorbSet: static _ => throw new Xunit.Sdk.XunitException("expected ToolFailure, got EmptyAbsorbSet"),
                onSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got SelfAbsorption: '{id.Id}'"),
                onResolvedSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got ResolvedSelfAbsorption: '{id.Id}'"),
                onDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got DuplicateAbsorbedRule: '{id.Id}'"),
                onResolvedDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got ResolvedDuplicateAbsorbedRule: '{id.Id}'"),
                onFamilyAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got FamilyAlreadyDischarged: '{already.FilePath}'"),
                onAbsorbedAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got AbsorbedAlreadyDischarged: '{already.FilePath}'"),
                onNotARuleCard: n => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got NotARuleCard({n.Kind.ToWireString()})"),
                onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got CardNotFound: '{notFound.FilePath}'"),
                onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got LayoutMismatch: {layoutMismatch.Reason}"),
                onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got CardCorrupt: {corrupt.Reason}"),
                onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got HandEnteredDerivedState: '{handEntered.Key}'"),
                onToolFailure: static toolFailure => toolFailure);
            Assert.NotNull(toolFailure);
        }
        finally
        {
            File.SetUnixFileMode(_changeDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal(familyBytes, File.ReadAllBytes(familyPath));
        Assert.Equal(firstBytes, File.ReadAllBytes(firstPath));
        Assert.Equal(secondBytes, File.ReadAllBytes(secondPath));
    }

    // RestoreCardContent directly (reflection — CardFindingRecordTests' RollbackRaisedCard_* own
    // precedent for exercising a private multi-card-write rollback helper on its own, rather than
    // only through a call graph that may not be able to reach every branch of it).
    [Fact]
    public void RestoreCardContent_WritesTheOriginalContentBackToTheAnchoredPath()
    {
        var path = WriteRuleCard("r-0024", "R-0024", RegisterLifecycleState.Open, RegisterCardFields.Empty);
        var originalContent = File.ReadAllText(path);
        File.WriteAllText(path, "corrupted — this call should overwrite it back to the original.");

        var anchored = AnchoredCardPath.TryCreate(_root, path, CardScope.Change, ChangeName, out _)!;
        InvokeRestoreCardContent(anchored, originalContent);

        Assert.Equal(originalContent, File.ReadAllText(path));
    }

    // RestoreAllAbsorbed directly: loops RestoreCardContent over every entry given it — proven by
    // restoring two different cards to two different original contents in one call.
    [Fact]
    public void RestoreAllAbsorbed_RestoresEveryEntry()
    {
        var firstPath = WriteRuleCard("r-0025", "R-0025", RegisterLifecycleState.Open, RegisterCardFields.Empty);
        var secondPath = WriteRuleCard("r-0026", "R-0026", RegisterLifecycleState.Open, RegisterCardFields.Empty);
        var firstOriginal = File.ReadAllText(firstPath);
        var secondOriginal = File.ReadAllText(secondPath);
        File.WriteAllText(firstPath, "corrupted first.");
        File.WriteAllText(secondPath, "corrupted second.");

        var firstAnchored = AnchoredCardPath.TryCreate(_root, firstPath, CardScope.Change, ChangeName, out _)!;
        var secondAnchored = AnchoredCardPath.TryCreate(_root, secondPath, CardScope.Change, ChangeName, out _)!;
        InvokeRestoreAllAbsorbed([firstAnchored, secondAnchored], [firstOriginal, secondOriginal]);

        Assert.Equal(firstOriginal, File.ReadAllText(firstPath));
        Assert.Equal(secondOriginal, File.ReadAllText(secondPath));
    }

    private static void InvokeRestoreCardContent(AnchoredCardPath anchored, string originalContent)
    {
        var method = typeof(CardStore).GetMethod("RestoreCardContent", BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, [anchored, originalContent]);
    }

    private static void InvokeRestoreAllAbsorbed(IReadOnlyList<AnchoredCardPath> anchors, IReadOnlyList<string> originalContents)
    {
        var method = typeof(CardStore).GetMethod("RestoreAllAbsorbed", BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, [anchors, originalContents]);
    }

    private string WriteRuleCard(string fileStem, string id, RegisterLifecycleState state, RegisterCardFields fields)
    {
        var path = Path.Combine(_changeDirectory, fileStem + ".md");
        WriteRuleCardAt(path, id, CardScope.Change, state, fields);
        return path;
    }

    private static void WriteRuleCardAt(string path, string id, CardScope scope, RegisterLifecycleState state, RegisterCardFields fields)
    {
        var frontmatter = new CardFrontmatter(
            id, CardKind.Rule, "Never trust a path string", state.ToWireString(), CardOwner.Architect, scope, string.Empty, Created, Created);
        var card = new CardFile(frontmatter, "A rule earned the hard way.", [], [], RegisterFields: fields);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static CardRuleCompactOutcome.Compacted AssertCompacted(CardRuleCompactOutcome outcome) =>
        outcome.Match(
            onCompacted: static compacted => compacted,
            onRoleNotPermitted: static roleNotPermitted => throw new Xunit.Sdk.XunitException($"expected Compacted, got RoleNotPermitted: '{roleNotPermitted.AttemptedRole.ToWireString()}'"),
            onEmptyAbsorbSet: static _ => throw new Xunit.Sdk.XunitException("expected Compacted, got EmptyAbsorbSet"),
            onSelfAbsorption: static id => throw new Xunit.Sdk.XunitException($"expected Compacted, got SelfAbsorption: '{id.Id}'"),
            onResolvedSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected Compacted, got ResolvedSelfAbsorption: '{id.Id}'"),
            onDuplicateAbsorbedRule: static id => throw new Xunit.Sdk.XunitException($"expected Compacted, got DuplicateAbsorbedRule: '{id.Id}'"),
            onResolvedDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected Compacted, got ResolvedDuplicateAbsorbedRule: '{id.Id}'"),
            onFamilyAlreadyDischarged: static already => throw new Xunit.Sdk.XunitException($"expected Compacted, got FamilyAlreadyDischarged: '{already.FilePath}'"),
            onAbsorbedAlreadyDischarged: static already => throw new Xunit.Sdk.XunitException($"expected Compacted, got AbsorbedAlreadyDischarged: '{already.FilePath}'"),
            onNotARuleCard: static n => throw new Xunit.Sdk.XunitException($"expected Compacted, got NotARuleCard({n.Kind.ToWireString()})"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Compacted, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Compacted, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Compacted, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected Compacted, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Compacted, got ToolFailure: {toolFailure.Reason}"));

    private static void AssertLayoutMismatch(CardRuleCompactOutcome outcome) =>
        outcome.Match<object?>(
            onCompacted: static _ => throw new Xunit.Sdk.XunitException("expected LayoutMismatch, got Compacted"),
            onRoleNotPermitted: id => throw new Xunit.Sdk.XunitException($"expected LayoutMismatch, got RoleNotPermitted: '{id.AttemptedRole.ToWireString()}'"),
            onEmptyAbsorbSet: static _ => throw new Xunit.Sdk.XunitException("expected LayoutMismatch, got EmptyAbsorbSet"),
            onSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected LayoutMismatch, got SelfAbsorption: '{id.Id}'"),
            onResolvedSelfAbsorption: id => throw new Xunit.Sdk.XunitException($"expected LayoutMismatch, got ResolvedSelfAbsorption: '{id.Id}'"),
            onDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected LayoutMismatch, got DuplicateAbsorbedRule: '{id.Id}'"),
            onResolvedDuplicateAbsorbedRule: id => throw new Xunit.Sdk.XunitException($"expected LayoutMismatch, got ResolvedDuplicateAbsorbedRule: '{id.Id}'"),
            onFamilyAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected LayoutMismatch, got FamilyAlreadyDischarged: '{already.FilePath}'"),
            onAbsorbedAlreadyDischarged: already => throw new Xunit.Sdk.XunitException($"expected LayoutMismatch, got AbsorbedAlreadyDischarged: '{already.FilePath}'"),
            onNotARuleCard: n => throw new Xunit.Sdk.XunitException($"expected LayoutMismatch, got NotARuleCard({n.Kind.ToWireString()})"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected LayoutMismatch, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static _ => null,
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected LayoutMismatch, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected LayoutMismatch, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected LayoutMismatch, got ToolFailure: {toolFailure.Reason}"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
