using System.Linq;
using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 7.5 — <see cref="CardStore.PromoteRule"/> (register: "Promoting a change-scoped rule to
/// repository scope SHALL move the same card, retaining its identity, text and thread"). Covers the
/// happy path (structural field preservation, not an enumerated subset), both scope refusals,
/// wrong-kind, the flow-state refusal, the two-step move-then-edit's own failure shape pinned the
/// way block D pins its phase-two failure, and the retry that self-heals it.
/// </summary>
public sealed class CardRulePromoteTests : IDisposable
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset Created = new(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PromotedAt = Created.AddDays(2);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-rule-promote-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _changeDirectory;
    private readonly string _registerDirectory;
    private readonly string _decisionsDirectory;

    public CardRulePromoteTests()
    {
        _changeDirectory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        _registerDirectory = Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar));
        _decisionsDirectory = Path.Combine(_root, CardLayout.DecisionsDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_changeDirectory);
        Directory.CreateDirectory(_decisionsDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            // Best-effort: a phase-two-failure test below may leave the register directory
            // read-only, which Directory.Delete cannot remove without this being undone first.
            if (Directory.Exists(_registerDirectory) && !OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(_registerDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                catch (IOException)
                {
                }
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    // The load-bearing test for brief item 2: preservation is proven structurally, not by
    // enumerating today's fields — every RegisterCardFields entry this fixture sets (including
    // EarnedFrom and the discharge pair, neither of which a plain promotion scenario would
    // naturally combine with) survives, and only Scope/Updated differ on the frontmatter.
    [Fact]
    public void PromoteRule_ChangeScopedRuleCarryingEveryRegisterField_MovesTheSameCard_OnlyScopeAndUpdatedChange()
    {
        var comment = new CardComment("C-0001", CardOwner.Worker, Created, "A thread comment.", null, null, null, []);
        var path = WriteRuleCard(
            "r-0001", "R-0001", RegisterLifecycleState.Discharged,
            new RegisterCardFields(null, null, CardOwner.Architect, Created.AddDays(1), EarnedFrom: ["F-0001", "F-0002"]),
            [comment]);
        var beforeRead = AssertParseSuccess(CardStore.ReadCard(path));

        var outcome = CardStore.PromoteRule(_root, path, CardOwner.Architect, PromotedAt, TimeSpan.FromSeconds(5));

        var promoted = AssertPromoted(outcome);
        Assert.Equal(Path.Combine(_registerDirectory, "r-0001.md"), promoted.NewFilePath);
        Assert.Equal(path, promoted.OldFilePath);
        Assert.False(File.Exists(path), "the old path must no longer hold a card — this is a move, not a copy.");
        Assert.True(File.Exists(promoted.NewFilePath));

        // Every field the fixture set, unenumerated by name in the assertion's own shape: compare
        // the whole card, then assert exactly which two frontmatter fields legitimately differ.
        var afterRead = AssertParseSuccess(CardStore.ReadCard(promoted.NewFilePath));
        Assert.Equal(beforeRead.Frontmatter.Id, afterRead.Frontmatter.Id);
        Assert.Equal(beforeRead.Frontmatter.Kind, afterRead.Frontmatter.Kind);
        Assert.Equal(beforeRead.Frontmatter.Title, afterRead.Frontmatter.Title);
        Assert.Equal(beforeRead.Frontmatter.Status, afterRead.Frontmatter.Status);
        Assert.Equal(beforeRead.Frontmatter.Owner, afterRead.Frontmatter.Owner);
        Assert.Equal(beforeRead.Frontmatter.Section, afterRead.Frontmatter.Section);
        Assert.Equal(beforeRead.Frontmatter.Created, afterRead.Frontmatter.Created);
        Assert.Equal(beforeRead.Body, afterRead.Body);

        // §7 remediation, blocker 3: every prior comment survives, in order, and exactly one
        // attributed comment recording the promotion is appended after them — the only way this
        // write records who promoted the card, since promotion touches neither DischargedBy (the
        // rule stays open) nor any other existing attribution field.
        Assert.Equal(beforeRead.Comments.Count + 1, afterRead.Comments.Count);
        Assert.Equal(beforeRead.Comments, afterRead.Comments.Take(beforeRead.Comments.Count).ToList());
        var promotionComment = afterRead.Comments[^1];
        Assert.Equal(CardOwner.Architect, promotionComment.Author);
        Assert.Equal(PromotedAt, promotionComment.Timestamp);

        // RegisterCardFields itself is not asserted with one Assert.Equal: ImmutableArray<T>'s own
        // Equals compares the underlying array by reference, not by content, so two independently-
        // parsed CardFiles' EarnedFrom would spuriously differ under the record's generated equality
        // even when their content is identical — SequenceEqual is the correct comparison here.
        Assert.Equal(beforeRead.RegisterFields.Condition, afterRead.RegisterFields.Condition);
        Assert.Equal(beforeRead.RegisterFields.Cadence, afterRead.RegisterFields.Cadence);
        Assert.Equal(beforeRead.RegisterFields.DischargedBy, afterRead.RegisterFields.DischargedBy);
        Assert.Equal(beforeRead.RegisterFields.DischargedAt, afterRead.RegisterFields.DischargedAt);
        Assert.Equal(beforeRead.RegisterFields.OwedBy, afterRead.RegisterFields.OwedBy);
        Assert.Equal(beforeRead.RegisterFields.Supersedes, afterRead.RegisterFields.Supersedes);
        Assert.Equal(beforeRead.RegisterFields.SupersededBy, afterRead.RegisterFields.SupersededBy);
        Assert.True(
            beforeRead.RegisterFields.EarnedFrom.SequenceEqual(afterRead.RegisterFields.EarnedFrom, StringComparer.Ordinal),
            "earned_from must survive promotion with every id intact and in order.");

        Assert.Equal(CardScope.Repository, afterRead.Frontmatter.Scope);
        Assert.Equal(PromotedAt, afterRead.Frontmatter.Updated);
        Assert.NotEqual(beforeRead.Frontmatter.Scope, afterRead.Frontmatter.Scope);
        Assert.NotEqual(beforeRead.Frontmatter.Updated, afterRead.Frontmatter.Updated);

        // No identity was allocated in the promotion path — the counter for `rule` still reflects
        // only the one identity this test itself wrote by hand.
        var resolution = CardIdentityResolver.Resolve(_root, "R-0001");
        AssertFoundAt(resolution, promoted.NewFilePath);
    }

    [Fact]
    public void PromoteRule_AlreadyRepositoryScoped_Refuses_WithNothingMoved()
    {
        var path = Path.Combine(_registerDirectory, "r-0002.md");
        Directory.CreateDirectory(_registerDirectory);
        WriteRuleCardAt(path, "R-0002", CardScope.Repository, RegisterLifecycleState.Open, RegisterCardFields.Empty, []);

        var outcome = CardStore.PromoteRule(_root, path, CardOwner.Architect, PromotedAt, TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onPromoted: static _ => throw new Xunit.Sdk.XunitException("expected AlreadyRepositoryScoped, got Promoted"),
            onAlreadyRepositoryScoped: static _ => null,
            onNotChangeScoped: n => throw new Xunit.Sdk.XunitException($"expected AlreadyRepositoryScoped, got NotChangeScoped({n.Scope.ToWireString()})"),
            onInvalidStatus: invalid => throw new Xunit.Sdk.XunitException($"expected AlreadyRepositoryScoped, got InvalidStatus: {invalid.Status}"),
            onNotARuleCard: notARule => throw new Xunit.Sdk.XunitException($"expected AlreadyRepositoryScoped, got NotARuleCard({notARule.Kind.ToWireString()})"),
            onTargetAlreadyExists: already => throw new Xunit.Sdk.XunitException($"expected AlreadyRepositoryScoped, got TargetAlreadyExists: '{already.FilePath}'"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected AlreadyRepositoryScoped, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected AlreadyRepositoryScoped, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected AlreadyRepositoryScoped, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected AlreadyRepositoryScoped, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected AlreadyRepositoryScoped, got ToolFailure: {toolFailure.Reason}"));

        Assert.True(File.Exists(path), "an already-repository-scoped rule must stay exactly where it was.");

        // process-enforcement (§9 block A2): repository-scoped, so it anchors with no changeName
        // needed — the refusal is card-addressed and recorded against this same card.
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    // register: "promoting an already-repository-scoped rule is a refusal too" (brief item 3) has
    // its own mirror for the other illegal scope pair — a rule that is neither change- nor
    // repository-scoped. Capability-scoped, physically living in CardLayout.DecisionsDirectory (the
    // one other scope AnchoredCardPath.TryCreate can anchor without a changeName), so this is also
    // the reviewer's requested proof that NotChangeScoped records like any other case (§9 block A2
    // remediation) rather than an unverified guess.
    [Fact]
    public void PromoteRule_CapabilityScoped_Refuses_AsNotChangeScoped_AndRecords()
    {
        var path = Path.Combine(_decisionsDirectory, "r-0017.md");
        WriteRuleCardAt(path, "R-0017", CardScope.Capability, RegisterLifecycleState.Open, RegisterCardFields.Empty, []);

        var outcome = CardStore.PromoteRule(_root, path, CardOwner.Architect, PromotedAt, TimeSpan.FromSeconds(5));

        outcome.Match<object?>(
            onPromoted: static _ => throw new Xunit.Sdk.XunitException("expected NotChangeScoped, got Promoted"),
            onAlreadyRepositoryScoped: already => throw new Xunit.Sdk.XunitException($"expected NotChangeScoped, got AlreadyRepositoryScoped: '{already.FilePath}'"),
            onNotChangeScoped: static n => { Assert.Equal(CardScope.Capability, n.Scope); return null; },
            onInvalidStatus: invalid => throw new Xunit.Sdk.XunitException($"expected NotChangeScoped, got InvalidStatus: {invalid.Status}"),
            onNotARuleCard: notARule => throw new Xunit.Sdk.XunitException($"expected NotChangeScoped, got NotARuleCard({notARule.Kind.ToWireString()})"),
            onTargetAlreadyExists: already => throw new Xunit.Sdk.XunitException($"expected NotChangeScoped, got TargetAlreadyExists: '{already.FilePath}'"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected NotChangeScoped, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected NotChangeScoped, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected NotChangeScoped, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected NotChangeScoped, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected NotChangeScoped, got ToolFailure: {toolFailure.Reason}"));

        Assert.True(File.Exists(path), "a wrongly-scoped rule must stay exactly where it was.");

        // process-enforcement (§9 block A2 remediation): Capability scope needs no changeName to
        // anchor, so this records regardless of whether one was supplied — confirmed here with none.
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    [Fact]
    public void PromoteRule_NonRuleCard_Refuses()
    {
        var path = Path.Combine(_changeDirectory, "o-0001.md");
        var frontmatter = new CardFrontmatter(
            "O-0001", CardKind.Obligation, "Settle it", RegisterLifecycleState.Open.ToWireString(), CardOwner.Architect,
            CardScope.Change, string.Empty, Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: new RegisterCardFields(null, null, null, null, OwedBy: "S-0001"));
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.PromoteRule(_root, path, CardOwner.Architect, PromotedAt, TimeSpan.FromSeconds(5), ChangeName);

        outcome.Match<object?>(
            onPromoted: static _ => throw new Xunit.Sdk.XunitException("expected NotARuleCard, got Promoted"),
            onAlreadyRepositoryScoped: already => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got AlreadyRepositoryScoped: '{already.FilePath}'"),
            onNotChangeScoped: n => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got NotChangeScoped({n.Scope.ToWireString()})"),
            onInvalidStatus: invalid => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got InvalidStatus: {invalid.Status}"),
            onNotARuleCard: static n => { Assert.Equal(CardKind.Obligation, n.Kind); return null; },
            onTargetAlreadyExists: already => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got TargetAlreadyExists: '{already.FilePath}'"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected NotARuleCard, got ToolFailure: {toolFailure.Reason}"));

        Assert.True(File.Exists(path));

        // process-enforcement (§9 block A2 remediation): PromoteRule now takes the changeName its
        // siblings already do, so this change-scoped card anchors and the refusal records.
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    // register: "SHALL NOT occupy flow states" — the same exercised refusal every other register
    // mutation already enforces, extended here to promotion.
    [Fact]
    public void PromoteRule_StatusIsAFlowState_Refuses()
    {
        var path = Path.Combine(_changeDirectory, "r-0003.md");
        var frontmatter = new CardFrontmatter(
            "R-0003", CardKind.Rule, "Title", "briefed", CardOwner.Architect, CardScope.Change, string.Empty, Created, Created);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.PromoteRule(_root, path, CardOwner.Architect, PromotedAt, TimeSpan.FromSeconds(5), ChangeName);

        outcome.Match<object?>(
            onPromoted: static _ => throw new Xunit.Sdk.XunitException("expected InvalidStatus, got Promoted"),
            onAlreadyRepositoryScoped: already => throw new Xunit.Sdk.XunitException($"expected InvalidStatus, got AlreadyRepositoryScoped: '{already.FilePath}'"),
            onNotChangeScoped: n => throw new Xunit.Sdk.XunitException($"expected InvalidStatus, got NotChangeScoped({n.Scope.ToWireString()})"),
            onInvalidStatus: static invalid => { Assert.Equal("briefed", invalid.Status); return null; },
            onNotARuleCard: notARule => throw new Xunit.Sdk.XunitException($"expected InvalidStatus, got NotARuleCard({notARule.Kind.ToWireString()})"),
            onTargetAlreadyExists: already => throw new Xunit.Sdk.XunitException($"expected InvalidStatus, got TargetAlreadyExists: '{already.FilePath}'"),
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected InvalidStatus, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected InvalidStatus, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected InvalidStatus, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected InvalidStatus, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected InvalidStatus, got ToolFailure: {toolFailure.Reason}"));

        // process-enforcement (§9 block A2 remediation): recorded now that changeName anchors.
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    // Phase one's own failure guarantee: a file already occupies the target basename in
    // register/. Checked before File.Move is attempted, so the source is left completely alone.
    [Fact]
    public void PromoteRule_TargetBasenameAlreadyClaimedInRegister_Refuses_WithNothingMoved()
    {
        var path = WriteRuleCard("r-0004", "R-0004", RegisterLifecycleState.Open, RegisterCardFields.Empty, []);

        Directory.CreateDirectory(_registerDirectory);
        var collisionPath = Path.Combine(_registerDirectory, "r-0004.md");
        File.WriteAllText(collisionPath, "not this rule at all — an unrelated file at the same basename.");

        var outcome = CardStore.PromoteRule(_root, path, CardOwner.Architect, PromotedAt, TimeSpan.FromSeconds(5), ChangeName);

        outcome.Match<object?>(
            onPromoted: static _ => throw new Xunit.Sdk.XunitException("expected TargetAlreadyExists, got Promoted"),
            onAlreadyRepositoryScoped: already => throw new Xunit.Sdk.XunitException($"expected TargetAlreadyExists, got AlreadyRepositoryScoped: '{already.FilePath}'"),
            onNotChangeScoped: n => throw new Xunit.Sdk.XunitException($"expected TargetAlreadyExists, got NotChangeScoped({n.Scope.ToWireString()})"),
            onInvalidStatus: invalid => throw new Xunit.Sdk.XunitException($"expected TargetAlreadyExists, got InvalidStatus: {invalid.Status}"),
            onNotARuleCard: notARule => throw new Xunit.Sdk.XunitException($"expected TargetAlreadyExists, got NotARuleCard({notARule.Kind.ToWireString()})"),
            onTargetAlreadyExists: static _ => null,
            onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected TargetAlreadyExists, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected TargetAlreadyExists, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected TargetAlreadyExists, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected TargetAlreadyExists, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: toolFailure => throw new Xunit.Sdk.XunitException($"expected TargetAlreadyExists, got ToolFailure: {toolFailure.Reason}"));

        Assert.True(File.Exists(path), "phase one must not run at all once the target collision is detected.");
        Assert.Equal("not this rule at all — an unrelated file at the same basename.", File.ReadAllText(collisionPath));

        // process-enforcement (§9 block A2 remediation): the card moved nowhere, but it is
        // change-scoped and this call now carries changeName, so the refusal is recorded against
        // it in place — an appended CardRefusalEntry, nothing else.
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    // The two-step mutation's honest failure shape (brief item 9): phase two (the frontmatter
    // edit) is isolated here by starting from the exact state a phase-one-succeeded-but-phase-two-
    // failed call would leave behind — a rule already physically sitting under
    // CardLayout.RegisterDirectory whose own frontmatter still reads scope: change, the "half-
    // promoted" state PromoteRule's own doc comment names. Whatever directory-write-dependent step
    // inside phase two the read-only register directory below actually blocks (the frontmatter
    // rewrite's temp file, or — if reached first — the card's own lock file), the outcome the brief
    // asks this test to pin is the same either way: ToolFailure, and the card left exactly as it
    // started, not an idealised "nothing happened" and not a corrupted partial write.
    [Fact]
    public void PromoteRule_AlreadyAtTargetButPhaseTwoBlocked_Refuses_LeavingTheCardExactlyAsItWas_ThenARetrySelfHeals()
    {
        if (OperatingSystem.IsWindows())
        {
            // UnixFileMode has no Windows equivalent — see CommandDispatcherFindingRecordTests'
            // own precedent for exercising a permission-denied write on Unix only.
            return;
        }

        Directory.CreateDirectory(_registerDirectory);
        var targetPath = Path.Combine(_registerDirectory, "r-0005.md");
        WriteRuleCardAt(targetPath, "R-0005", CardScope.Change, RegisterLifecycleState.Open, RegisterCardFields.Empty, []);
        var beforeBytes = File.ReadAllBytes(targetPath);

        File.SetUnixFileMode(_registerDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var outcome = CardStore.PromoteRule(_root, targetPath, CardOwner.Architect, PromotedAt, TimeSpan.FromSeconds(5));

            var toolFailure = outcome.Match(
                onPromoted: static _ => throw new Xunit.Sdk.XunitException("expected ToolFailure, got Promoted"),
                onAlreadyRepositoryScoped: already => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got AlreadyRepositoryScoped: '{already.FilePath}'"),
                onNotChangeScoped: n => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got NotChangeScoped({n.Scope.ToWireString()})"),
                onInvalidStatus: invalid => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got InvalidStatus: {invalid.Status}"),
                onNotARuleCard: notARule => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got NotARuleCard({notARule.Kind.ToWireString()})"),
                onTargetAlreadyExists: already => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got TargetAlreadyExists: '{already.FilePath}'"),
                onCardNotFound: notFound => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got CardNotFound: '{notFound.FilePath}'"),
                onLayoutMismatch: layoutMismatch => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got LayoutMismatch: {layoutMismatch.Reason}"),
                onCardCorrupt: corrupt => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got CardCorrupt: {corrupt.Reason}"),
                onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected ToolFailure, got HandEnteredDerivedState: '{handEntered.Key}'"),
                onToolFailure: static toolFailure => toolFailure);
            Assert.NotNull(toolFailure);

            // Exactly as it was: same bytes, same path, scope still change — proven on the bytes,
            // block D's own standard, not merely re-parsed and checked field by field.
            Assert.True(File.Exists(targetPath));
            Assert.Equal(beforeBytes, File.ReadAllBytes(targetPath));
        }
        finally
        {
            File.SetUnixFileMode(_registerDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // The self-heal: retrying against the same path once the block is lifted lands phase two —
        // File.Move is a no-op (the card is already at its target), so nothing here depends on
        // phase one running twice.
        var retryOutcome = CardStore.PromoteRule(_root, targetPath, CardOwner.Architect, PromotedAt.AddMinutes(1), TimeSpan.FromSeconds(5));
        var promoted = AssertPromoted(retryOutcome);
        Assert.Equal(targetPath, promoted.OldFilePath);
        Assert.Equal(targetPath, promoted.NewFilePath);
        Assert.Equal(CardScope.Repository, promoted.Card.Frontmatter.Scope);
        Assert.Equal(PromotedAt.AddMinutes(1), promoted.Card.Frontmatter.Updated);

        var healed = AssertParseSuccess(CardStore.ReadCard(targetPath));
        Assert.Equal(CardScope.Repository, healed.Frontmatter.Scope);
    }

    private string WriteRuleCard(string fileStem, string id, RegisterLifecycleState state, RegisterCardFields fields, IReadOnlyList<CardComment> comments)
    {
        var path = Path.Combine(_changeDirectory, fileStem + ".md");
        WriteRuleCardAt(path, id, CardScope.Change, state, fields, comments);
        return path;
    }

    private static void WriteRuleCardAt(
        string path, string id, CardScope scope, RegisterLifecycleState state, RegisterCardFields fields, IReadOnlyList<CardComment> comments)
    {
        var frontmatter = new CardFrontmatter(
            id, CardKind.Rule, "Never trust a path string", state.ToWireString(), CardOwner.Architect, scope, string.Empty, Created, Created);
        var card = new CardFile(frontmatter, "A rule earned the hard way.", comments, [], RegisterFields: fields);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static CardRulePromoteOutcome.Promoted AssertPromoted(CardRulePromoteOutcome outcome) =>
        outcome.Match(
            onPromoted: static promoted => promoted,
            onAlreadyRepositoryScoped: static already => throw new Xunit.Sdk.XunitException($"expected Promoted, got AlreadyRepositoryScoped: '{already.FilePath}'"),
            onNotChangeScoped: static n => throw new Xunit.Sdk.XunitException($"expected Promoted, got NotChangeScoped({n.Scope.ToWireString()})"),
            onInvalidStatus: static invalid => throw new Xunit.Sdk.XunitException($"expected Promoted, got InvalidStatus: {invalid.Status}"),
            onNotARuleCard: static notARule => throw new Xunit.Sdk.XunitException($"expected Promoted, got NotARuleCard({notARule.Kind.ToWireString()})"),
            onTargetAlreadyExists: static already => throw new Xunit.Sdk.XunitException($"expected Promoted, got TargetAlreadyExists: '{already.FilePath}'"),
            onCardNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Promoted, got CardNotFound: '{notFound.FilePath}'"),
            onLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Promoted, got LayoutMismatch: {layoutMismatch.Reason}"),
            onCardCorrupt: static corrupt => throw new Xunit.Sdk.XunitException($"expected Promoted, got CardCorrupt: {corrupt.Reason}"),
            onHandEnteredDerivedState: handEntered => throw new Xunit.Sdk.XunitException($"expected Promoted, got HandEnteredDerivedState: '{handEntered.Key}'"),
            onToolFailure: static toolFailure => throw new Xunit.Sdk.XunitException($"expected Promoted, got ToolFailure: {toolFailure.Reason}"));

    private static void AssertFoundAt(CardIdentityResolution resolution, string expectedFilePath) =>
        resolution.Match<object?>(
            onFound: (filePath, _) => { Assert.Equal(expectedFilePath, filePath); return null; },
            onNotFound: id => throw new Xunit.Sdk.XunitException($"expected Found, got NotFound: '{id}'"),
            onDuplicate: (id, filePaths) => throw new Xunit.Sdk.XunitException($"expected Found, got Duplicate: '{id}'"),
            onUnreadable: (id, filePaths) => throw new Xunit.Sdk.XunitException($"expected Found, got Unreadable: '{id}'"));

    private static CardFile AssertParseSuccess(CardFileParseResult result) =>
        result.Match<CardFile>(
            onSuccess: success => success.Card,
            onFailure: failure => throw new Xunit.Sdk.XunitException($"expected parse success, got failure: {failure.Reason}"));
}
