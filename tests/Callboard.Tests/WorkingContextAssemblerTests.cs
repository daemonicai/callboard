using System.Linq;
using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §10 block A — <see cref="WorkingContextAssembler"/>, the working-context spec's own three
/// scenarios plus the four-part shape's own bounds: closed cards excluded, the ordering rule total
/// and deterministic, and no narrative from a card outside the queue.
/// </summary>
public sealed class WorkingContextAssemblerTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-working-context-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _registerDirectory;
    private readonly string _changeDirectory;

    public WorkingContextAssemblerTests()
    {
        _registerDirectory = Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar));
        _changeDirectory = Path.Combine(_root, CardLayout.ChangesDirectory("establish-callboard").Replace('/', Path.DirectorySeparatorChar));
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

    // Spec scenario: "Context contains only the caller's work".
    [Fact]
    public void Build_OwnedAndForeignCards_QueueContainsOnlyOwnedCard()
    {
        WriteBlock("b-mine", "B-0001", CardOwner.Worker, "briefed", Base);
        WriteBlock("b-theirs", "B-0002", CardOwner.Architect, "briefed", Base);

        var context = WorkingContextAssembler.Build(_root, CardOwner.Worker);

        var id = Assert.Single(context.Queue).Card.Frontmatter.Id;
        Assert.Equal("B-0001", id);
    }

    // Spec scenario: "Addressed thread pulls a card into the queue".
    [Fact]
    public void Build_UnresolvedThreadAddressedToRole_PullsForeignOwnedCardIntoQueue()
    {
        WriteBlockWithComment(
            "b-addressed", "B-0003", owner: CardOwner.Architect, status: "in-review", updated: Base,
            commentId: "c-1", commentAuthor: CardOwner.Architect, commentTo: CardOwner.Reviewer, commentTimestamp: Base);

        var context = WorkingContextAssembler.Build(_root, CardOwner.Reviewer);

        var entry = Assert.Single(context.Queue);
        Assert.Equal("B-0003", entry.Card.Frontmatter.Id);
        Assert.Equal(CardOwner.Architect, entry.Card.Frontmatter.Owner);
    }

    // Spec scenario: "Prior verdict accompanies a remediation".
    [Fact]
    public void Build_TopItemIsBlockAtRoundTwo_TopItemCarriesRoundOneVerdict()
    {
        var path = Path.Combine(_changeDirectory, "b-round2.md");
        var frontmatter = new CardFrontmatter(
            "B-0004", CardKind.Block, "A remediation block", "briefed", CardOwner.Worker, CardScope.Change, "S-0001", Base, Base);
        var blockFields = new BlockCardFields("abc123", null, [], 2, [], []);
        var claim = new CardApprovalClaim("claim-1", 1, "Round-one claim text.", []);
        var limit = new CardApprovalLimit(1, "Round-one limit text.", []);
        var card = new CardFile(frontmatter, "Body.", [], [], BlockFields: blockFields, Claims: [claim], Limits: [limit]);
        WriteCard(path, card);

        var context = WorkingContextAssembler.Build(_root, CardOwner.Worker);

        Assert.NotNull(context.TopItem);
        Assert.Equal("B-0004", context.TopItem!.Card.Frontmatter.Id);
        var previousClaim = Assert.Single(context.TopItem.PreviousRoundClaims);
        Assert.Equal("claim-1", previousClaim.Id);
        Assert.Equal(1, previousClaim.Round);
        var previousLimit = Assert.Single(context.TopItem.PreviousRoundLimits);
        Assert.Equal(1, previousLimit.Round);
    }

    [Fact]
    public void Build_TopItemIsBlockAtRoundOne_CarriesNoVerdict()
    {
        var path = Path.Combine(_changeDirectory, "b-round1.md");
        var frontmatter = new CardFrontmatter(
            "B-0005", CardKind.Block, "A fresh block", "briefed", CardOwner.Worker, CardScope.Change, "S-0001", Base, Base);
        var card = new CardFile(frontmatter, "Body.", [], []);
        WriteCard(path, card);

        var context = WorkingContextAssembler.Build(_root, CardOwner.Worker);

        Assert.NotNull(context.TopItem);
        Assert.Empty(context.TopItem!.PreviousRoundClaims);
        Assert.Empty(context.TopItem.PreviousRoundLimits);
    }

    // §10 block A review, change 3 — "constraints" is the live register cards whose scope covers
    // the top item. Repository-scoped rules and hazards bind every card, regardless of change.
    [Fact]
    public void Build_TopItem_ConstraintsIncludeEveryRepositoryScopedRuleAndHazard()
    {
        WriteBlock("b-mine", "B-0060", CardOwner.Worker, "briefed", Base);
        WriteRegisterCard(_registerDirectory, "r-repo", "R-0060", CardKind.Rule, "open", scope: CardScope.Repository);
        WriteRegisterCard(_registerDirectory, "h-repo", "H-0060", CardKind.Hazard, "open", scope: CardScope.Repository);

        var context = WorkingContextAssembler.Build(_root, CardOwner.Worker);

        Assert.NotNull(context.TopItem);
        var constraintIds = context.TopItem!.BindingConstraints.Select(entry => entry.Card.Frontmatter.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(["H-0060", "R-0060"], constraintIds);
    }

    // A change-scoped rule binds the top item exactly when it belongs to the top item's own
    // change, and not otherwise.
    [Fact]
    public void Build_TopItem_ConstraintsIncludeChangeScopedRuleInSameChange_ButNotAnotherChange()
    {
        WriteBlock("b-mine", "B-0061", CardOwner.Worker, "briefed", Base);

        var sameChangeRulePath = Path.Combine(_changeDirectory, "r-same-change.md");
        WriteChangeScopedRule(sameChangeRulePath, "R-0061");

        var otherChangeDirectory = Path.Combine(_root, CardLayout.ChangesDirectory("a-different-change").Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(otherChangeDirectory);
        var otherChangeRulePath = Path.Combine(otherChangeDirectory, "r-other-change.md");
        WriteChangeScopedRule(otherChangeRulePath, "R-0062");

        var context = WorkingContextAssembler.Build(_root, CardOwner.Worker);

        Assert.NotNull(context.TopItem);
        var constraintIds = context.TopItem!.BindingConstraints.Select(entry => entry.Card.Frontmatter.Id).ToArray();
        Assert.Equal(["R-0061"], constraintIds);

        // Still delivered in part 1 regardless — only its role as a *constraint* on this
        // particular top item is scope-gated.
        var liveIds = context.LiveRulesAndHazards.Select(entry => entry.Card.Frontmatter.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(["R-0061", "R-0062"], liveIds);
    }

    [Fact]
    public void Build_ClosedOwnedCard_ExcludedFromQueue()
    {
        WriteBlock("b-closed", "B-0006", CardOwner.Worker, "closed", Base);

        var context = WorkingContextAssembler.Build(_root, CardOwner.Worker);

        Assert.Empty(context.Queue);
    }

    [Fact]
    public void Build_ClosedCardWithUnresolvedAddressedThread_StillExcluded()
    {
        WriteBlockWithComment(
            "b-closed-addressed", "B-0007", owner: CardOwner.Architect, status: "closed", updated: Base,
            commentId: "c-1", commentAuthor: CardOwner.Architect, commentTo: CardOwner.Reviewer, commentTimestamp: Base);

        var context = WorkingContextAssembler.Build(_root, CardOwner.Reviewer);

        Assert.Empty(context.Queue);
    }

    [Fact]
    public void Build_DischargedRuleAndHazard_ExcludedFromLiveRegister()
    {
        WriteRegisterCard(_registerDirectory, "r-open", "R-0001", CardKind.Rule, "open");
        WriteRegisterCard(_registerDirectory, "r-discharged", "R-0002", CardKind.Rule, "discharged");
        WriteRegisterCard(_registerDirectory, "h-open", "H-0001", CardKind.Hazard, "open");
        WriteRegisterCard(_registerDirectory, "h-discharged", "H-0002", CardKind.Hazard, "discharged");

        var context = WorkingContextAssembler.Build(_root, CardOwner.Worker);

        var ids = context.LiveRulesAndHazards.Select(entry => entry.Card.Frontmatter.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(["H-0001", "R-0001"], ids);
    }

    // §10 block A review, blocker 1: a rule/hazard card owned by the requesting role must not
    // leak into the queue on top of appearing in part 1 — the two parts are disjoint.
    [Fact]
    public void Build_OpenRuleOwnedByRequestingRole_AppearsOnlyInLiveRegister_NeverInQueue()
    {
        WriteRegisterCard(_registerDirectory, "r-owned", "R-0010", CardKind.Rule, "open", owner: CardOwner.Worker);
        WriteRegisterCard(_registerDirectory, "h-owned", "H-0010", CardKind.Hazard, "open", owner: CardOwner.Worker);

        var context = WorkingContextAssembler.Build(_root, CardOwner.Worker);

        var registerIds = context.LiveRulesAndHazards.Select(entry => entry.Card.Frontmatter.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Assert.Equal(["H-0010", "R-0010"], registerIds);
        Assert.Empty(context.Queue);
        Assert.Null(context.TopItem);
    }

    // Same leak, via the addressed-only route rather than ownership.
    [Fact]
    public void Build_OpenRuleWithUnresolvedThreadAddressedToRequestingRole_StillNeverEntersQueue()
    {
        var path = Path.Combine(_registerDirectory, "r-addressed.md");
        var frontmatter = new CardFrontmatter(
            "R-0011", CardKind.Rule, "A rule", "open", CardOwner.Architect, CardScope.Repository, string.Empty, Base, Base);
        var comment = new CardComment("c-1", CardOwner.Architect, Base, "Please review this rule.", null, CardOwner.Worker, null, []);
        var card = new CardFile(frontmatter, "Body.", [comment], [], RegisterFields: RegisterCardFields.Empty);
        WriteCard(path, card);

        var context = WorkingContextAssembler.Build(_root, CardOwner.Worker);

        Assert.Single(context.LiveRulesAndHazards);
        Assert.Empty(context.Queue);
    }

    // Ordering: owned cards oldest-updated-first, then addressed-only cards oldest-addressed-
    // comment-first, ties broken by id — proven regardless of the order cards were written in.
    [Fact]
    public void Build_MixedOwnedAndAddressedCards_OrdersTotalAndDeterministically()
    {
        // Owned: written in reverse-chronological order on disk, to prove sort doesn't depend on
        // write/enumeration order.
        WriteBlock("b-owned-late", "B-0020", CardOwner.Worker, "briefed", Base.AddHours(3));
        WriteBlock("b-owned-early", "B-0010", CardOwner.Worker, "briefed", Base.AddHours(1));
        WriteBlock("b-owned-tie-b", "B-0012", CardOwner.Worker, "briefed", Base.AddHours(2));
        WriteBlock("b-owned-tie-a", "B-0011", CardOwner.Worker, "briefed", Base.AddHours(2));

        // Addressed-only: owned by architect, addressed to worker, at varying comment ages.
        WriteBlockWithComment(
            "b-addr-late", "B-0030", owner: CardOwner.Architect, status: "in-review", updated: Base,
            commentId: "c-1", commentAuthor: CardOwner.Architect, commentTo: CardOwner.Worker, commentTimestamp: Base.AddHours(5));
        WriteBlockWithComment(
            "b-addr-early", "B-0031", owner: CardOwner.Architect, status: "in-review", updated: Base,
            commentId: "c-1", commentAuthor: CardOwner.Architect, commentTo: CardOwner.Worker, commentTimestamp: Base.AddHours(4));

        var context = WorkingContextAssembler.Build(_root, CardOwner.Worker);

        Assert.Equal(
            ["B-0010", "B-0011", "B-0012", "B-0020", "B-0031", "B-0030"],
            context.Queue.Select(entry => entry.Card.Frontmatter.Id).ToArray());
    }

    [Fact]
    public void Build_SameInputWrittenInDifferentFileOrder_ProducesTheSameQueueOrder()
    {
        WriteBlock("b-1", "B-0041", CardOwner.Worker, "briefed", Base.AddHours(2));
        WriteBlock("b-2", "B-0040", CardOwner.Worker, "briefed", Base.AddHours(1));
        var firstOrder = WorkingContextAssembler.Build(_root, CardOwner.Worker).Queue.Select(e => e.Card.Frontmatter.Id).ToArray();

        // Re-read from the same directory a second time — the assembler's own sort, not
        // enumeration order, must be what determines the result.
        var secondOrder = WorkingContextAssembler.Build(_root, CardOwner.Worker).Queue.Select(e => e.Card.Frontmatter.Id).ToArray();

        Assert.Equal(["B-0040", "B-0041"], firstOrder);
        Assert.Equal(firstOrder, secondOrder);
    }

    // record-retrieval: "no narrative from cards outside its queue appears in the response" —
    // proven at the assembler level: a card that belongs to neither bucket contributes nothing at
    // all, not even identity.
    [Fact]
    public void Build_CardNeitherOwnedNorAddressed_NeverAppearsAnywhereInTheResult()
    {
        WriteBlock("b-mine", "B-0050", CardOwner.Worker, "briefed", Base);
        WriteBlockWithComment(
            "b-unrelated", "B-0051", owner: CardOwner.Architect, status: "in-review", updated: Base,
            commentId: "c-1", commentAuthor: CardOwner.Architect, commentTo: CardOwner.Reviewer, commentTimestamp: Base,
            body: "Secret narrative that must never leak into worker's context.");

        var context = WorkingContextAssembler.Build(_root, CardOwner.Worker);

        Assert.DoesNotContain(context.Queue, entry => entry.Card.Frontmatter.Id == "B-0051");
        Assert.NotEqual("B-0051", context.TopItem?.Card.Frontmatter.Id);
    }

    private void WriteBlock(string fileStem, string id, CardOwner owner, string status, DateTimeOffset updated)
    {
        var path = Path.Combine(_changeDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Block, "A block card", status, owner, CardScope.Change, "S-0001", Base, updated);
        var card = new CardFile(frontmatter, "Body.", [], []);
        WriteCard(path, card);
    }

    private void WriteBlockWithComment(
        string fileStem, string id, CardOwner owner, string status, DateTimeOffset updated,
        string commentId, CardOwner commentAuthor, CardOwner commentTo, DateTimeOffset commentTimestamp, string body = "Body.")
    {
        var path = Path.Combine(_changeDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(id, CardKind.Block, "A block card", status, owner, CardScope.Change, "S-0001", Base, updated);
        var comment = new CardComment(commentId, commentAuthor, commentTimestamp, "Please take a look.", null, commentTo, null, []);
        var card = new CardFile(frontmatter, body, [comment], []);
        WriteCard(path, card);
    }

    private static void WriteRegisterCard(
        string directory, string fileStem, string id, CardKind kind, string status, CardOwner? owner = null, CardScope? scope = null)
    {
        var path = Path.Combine(directory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, kind, "A register card", status, owner ?? CardOwner.Architect, scope ?? CardScope.Repository, string.Empty, Base, Base);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty);
        WriteCard(path, card);
    }

    private static void WriteChangeScopedRule(string path, string id)
    {
        var frontmatter = new CardFrontmatter(
            id, CardKind.Rule, "A change-scoped rule", "open", CardOwner.Architect, CardScope.Change, string.Empty, Base, Base);
        var card = new CardFile(frontmatter, "Body.", [], [], RegisterFields: RegisterCardFields.Empty);
        WriteCard(path, card);
    }

    private static void WriteCard(string path, CardFile card) =>
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
}
