using System.Text;
using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §9 remediation, round two (S4) — <see cref="CardStore.PromoteComment"/>, the two-card write
/// behind <c>comment promote --to question|decision</c>. Reuses <see cref="CardStore.RecordFinding"/>'s
/// own two-card, two-lock discipline via the now-generalised <see cref="CardStore.
/// AcquireLocksAndRecord{TOutcome}"/> rather than a fourth divergent multi-card write shape.
/// </summary>
public sealed class CardCommentPromoteTests : IDisposable
{
    private const string ChangeName = "establish-callboard";
    private static readonly DateTimeOffset Created = new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset PromotedAt = Created.AddHours(3);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "callboard-comment-promote-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _changeDirectory;
    private readonly string _registerDirectory;
    private readonly string _decisionsDirectory;

    public CardCommentPromoteTests()
    {
        _changeDirectory = Path.Combine(_root, CardLayout.ChangesDirectory(ChangeName).Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_changeDirectory);
        _registerDirectory = Path.Combine(_root, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(_registerDirectory);
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
    public void PromoteComment_ToQuestion_Promotes_WritesTheQuestionCard_AndResolvesTheOriginalThread()
    {
        var path = WriteCardWithComment("b-0001", "B-0001", "thread-1", CardOwner.Reviewer);
        var raisedPath = Path.Combine(_registerDirectory, "q-0100.md");

        var outcome = CardStore.PromoteComment(
            _root, path, "thread-1", raisedPath, CardKind.Question, "Should we ship X?", CardOwner.Reviewer,
            CardOwner.ProductOwner, "Raised while resolving a thread.", ChangeName, PromotedAt, TimeSpan.FromSeconds(5));

        var promoted = AssertPromoted(outcome);
        Assert.Equal(CardKind.Question, promoted.RaisedCard.Frontmatter.Kind);
        Assert.Equal(CardOwner.ProductOwner, promoted.RaisedCard.Frontmatter.Owner);
        Assert.Equal(CardScope.Repository, promoted.RaisedCard.Frontmatter.Scope);
        Assert.Equal("open", promoted.RaisedCard.Frontmatter.Status);
        Assert.Equal("S-0001", promoted.RaisedCard.Frontmatter.Section);

        var raisedOnDisk = AssertParseSuccess(CardStore.ReadCard(raisedPath));
        Assert.Equal(promoted.RaisedCard.Frontmatter.Id, raisedOnDisk.Frontmatter.Id);

        var originalOnDisk = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Equal(2, originalOnDisk.Comments.Count);
        Assert.True(CardCommentRouting.IsResolved(originalOnDisk.Comments, 0));
        Assert.Contains(promoted.RaisedCard.Frontmatter.Id, originalOnDisk.Comments[1].Body);
        Assert.Empty(originalOnDisk.Refusals);
    }

    // §9 remediation round three, F2 — promoting a thread on a *section* card (9.6's first arm)
    // must not link the raised question via the empty CardFrontmatter.Section a section card
    // carries; it must resolve to the section card's own Id via CardStore.OwningSectionId.
    [Fact]
    public void PromoteComment_ToQuestion_OnASectionCard_LinksTheRaisedQuestionToTheSectionsOwnId()
    {
        var path = WriteSectionCardWithComment("s-0030", "S-0030", "thread-1", CardOwner.Reviewer);
        var raisedPath = Path.Combine(_registerDirectory, "q-0200.md");

        var outcome = CardStore.PromoteComment(
            _root, path, "thread-1", raisedPath, CardKind.Question, "Should we ship X?", CardOwner.Reviewer,
            CardOwner.ProductOwner, "Raised while resolving a thread.", ChangeName, PromotedAt, TimeSpan.FromSeconds(5));

        var promoted = AssertPromoted(outcome);
        Assert.Equal("S-0030", promoted.RaisedCard.Frontmatter.Section);
    }

    [Fact]
    public void PromoteComment_ToDecision_Promotes_OwnedByTheActingRole_NoOwedByNeeded()
    {
        var path = WriteCardWithComment("b-0002", "B-0002", "thread-1", CardOwner.Architect);
        var raisedPath = Path.Combine(_decisionsDirectory, "d-0100.md");

        var outcome = CardStore.PromoteComment(
            _root, path, "thread-1", raisedPath, CardKind.Decision, "Ship X now.", CardOwner.Architect,
            owedByRole: null, "Raised while resolving a thread.", ChangeName, PromotedAt, TimeSpan.FromSeconds(5));

        var promoted = AssertPromoted(outcome);
        Assert.Equal(CardKind.Decision, promoted.RaisedCard.Frontmatter.Kind);
        Assert.Equal(CardOwner.Architect, promoted.RaisedCard.Frontmatter.Owner);
        Assert.Equal(CardScope.Capability, promoted.RaisedCard.Frontmatter.Scope);
    }

    [Fact]
    public void PromoteComment_CommentDoesNotExist_Refuses_AndRecordsTheRefusal_AndWritesNoRaisedCard()
    {
        var path = WriteCardWithComment("b-0003", "B-0003", "thread-1", CardOwner.Reviewer);
        var raisedPath = Path.Combine(_registerDirectory, "q-0101.md");

        var outcome = CardStore.PromoteComment(
            _root, path, "no-such-thread", raisedPath, CardKind.Question, "Title.", CardOwner.Reviewer,
            CardOwner.ProductOwner, "Body.", ChangeName, PromotedAt, TimeSpan.FromSeconds(5));

        var refusal = Assert.IsType<CardCommentPromoteOutcome.CommentNotFound>(outcome);
        Assert.Equal("no-such-thread", refusal.CommentId);
        Assert.False(File.Exists(raisedPath));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Reviewer, recorded.By);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    [Fact]
    public void PromoteComment_RoleNeitherAddresseeNorCardOwner_Refuses_AndRecordsTheRefusal_AndWritesNoRaisedCard()
    {
        var path = WriteCardWithComment("b-0010", "B-0010", "thread-1", CardOwner.Reviewer);
        var raisedPath = Path.Combine(_registerDirectory, "q-0105.md");

        var outcome = CardStore.PromoteComment(
            _root, path, "thread-1", raisedPath, CardKind.Question, "Title.", CardOwner.Architect,
            CardOwner.ProductOwner, "Body.", ChangeName, PromotedAt, TimeSpan.FromSeconds(5));

        var refusal = Assert.IsType<CardCommentPromoteOutcome.RoleNotPermitted>(outcome);
        Assert.Equal(CardOwner.Architect, refusal.AttemptedRole);
        Assert.Equal(CardOwner.Worker, refusal.CardOwnerRole);
        Assert.Equal(CardOwner.Reviewer, refusal.AddressedTo);
        Assert.False(File.Exists(raisedPath));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.False(CardCommentRouting.IsResolved(read.Comments, 0));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Architect, recorded.By);
        Assert.False(string.IsNullOrWhiteSpace(recorded.Rule));
        Assert.False(string.IsNullOrWhiteSpace(recorded.Remedy));
    }

    /// <summary>Deliberate consequence of the Product Owner ruling (§10 block D), same as <see
    /// cref="CardCommentResolveTests.ResolveComment_ByCardOwner_ThreadAddressedToAnotherRole_Resolves"/>:
    /// the card's owner may promote a thread addressed to a different role.</summary>
    [Fact]
    public void PromoteComment_ByCardOwner_ThreadAddressedToAnotherRole_Promotes()
    {
        var path = WriteCardWithComment("b-0011", "B-0011", "thread-1", CardOwner.ProductOwner);
        var raisedPath = Path.Combine(_registerDirectory, "q-0106.md");

        var outcome = CardStore.PromoteComment(
            _root, path, "thread-1", raisedPath, CardKind.Question, "Title.", CardOwner.Worker,
            CardOwner.ProductOwner, "Body.", ChangeName, PromotedAt, TimeSpan.FromSeconds(5));

        AssertPromoted(outcome);
    }

    [Fact]
    public void PromoteComment_AlreadyResolved_Refuses_AndRecordsTheRefusal_AndWritesNoRaisedCard()
    {
        var path = WriteCardWithComment("b-0004", "B-0004", "thread-1", CardOwner.Reviewer);
        var firstRaised = Path.Combine(_registerDirectory, "q-0102.md");
        var first = CardStore.PromoteComment(
            _root, path, "thread-1", firstRaised, CardKind.Question, "Title.", CardOwner.Reviewer,
            CardOwner.ProductOwner, "Body.", ChangeName, PromotedAt, TimeSpan.FromSeconds(5));
        AssertPromoted(first);

        var secondRaised = Path.Combine(_registerDirectory, "q-0103.md");
        var outcome = CardStore.PromoteComment(
            _root, path, "thread-1", secondRaised, CardKind.Question, "Title again.", CardOwner.Reviewer,
            CardOwner.ProductOwner, "Body again.", ChangeName, PromotedAt.AddMinutes(5), TimeSpan.FromSeconds(5));

        var refusal = Assert.IsType<CardCommentPromoteOutcome.AlreadyResolved>(outcome);
        Assert.Equal("thread-1", refusal.CommentId);
        Assert.False(File.Exists(secondRaised));

        var read = AssertParseSuccess(CardStore.ReadCard(path));
        var recorded = Assert.Single(read.Refusals);
        Assert.Equal(CardOwner.Reviewer, recorded.By);
    }

    [Fact]
    public void PromoteComment_RaisedCardTargetAlreadyExists_Refuses_WithoutRecording_AndWithoutResolving()
    {
        var path = WriteCardWithComment("b-0005", "B-0005", "thread-1", CardOwner.Reviewer);

        // A readable, unrelated card at the target path — not garbage text (§13: the identity
        // allocator now confirms, against the whole record, that the id it is about to issue is
        // not already borne; an unparseable file at this exact path would report Unreadable rather
        // than "confirmed unclaimed", masking the AlreadyExists case this test targets under a
        // ToolFailure instead). Its own id ("Q-9999") deliberately does not collide with the
        // "Q-0001" the fresh counter is about to issue — this fixture is about the *path*
        // colliding, not the id.
        var raisedPath = Path.Combine(_registerDirectory, "q-0104.md");
        var unrelatedFrontmatter = new CardFrontmatter(
            "Q-9999", CardKind.Question, "Unrelated", "open", CardOwner.Architect, CardScope.Repository, string.Empty, Created, Created);
        File.WriteAllText(
            raisedPath, CardFileWriter.Serialize(new CardFile(unrelatedFrontmatter, "Unrelated.", [], [])),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var outcome = CardStore.PromoteComment(
            _root, path, "thread-1", raisedPath, CardKind.Question, "Title.", CardOwner.Reviewer,
            CardOwner.ProductOwner, "Body.", ChangeName, PromotedAt, TimeSpan.FromSeconds(5));

        var refusal = Assert.IsType<CardCommentPromoteOutcome.RaisedCardAlreadyExists>(outcome);
        Assert.Equal(raisedPath, refusal.FilePath);

        // Never card-addressed (§9 block A3 ruling): no existing card was resolved at the raised
        // path to record against, and the original card is untouched — the thread is not resolved.
        var read = AssertParseSuccess(CardStore.ReadCard(path));
        Assert.Empty(read.Refusals);
        Assert.False(CardCommentRouting.IsResolved(read.Comments, 0));
    }

    private string WriteCardWithComment(string fileStem, string id, string commentId, CardOwner addressedTo)
    {
        var path = Path.Combine(_changeDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Block, "A block card", "in-review", CardOwner.Worker, CardScope.Change, "S-0001", Created, Created);
        var comment = new CardComment(
            Id: commentId, Author: CardOwner.Architect, Timestamp: Created.AddHours(1), Body: "Original comment.",
            ReplyTo: null, To: addressedTo, Resolves: null, UnknownHeaderFields: []);
        var card = new CardFile(frontmatter, "Body.", [comment], []);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private string WriteSectionCardWithComment(string fileStem, string id, string commentId, CardOwner addressedTo)
    {
        var path = Path.Combine(_changeDirectory, fileStem + ".md");
        var frontmatter = new CardFrontmatter(
            id, CardKind.Section, "A section card", "open", CardOwner.Architect, CardScope.Change, string.Empty, Created, Created);
        var comment = new CardComment(
            Id: commentId, Author: CardOwner.Architect, Timestamp: Created.AddHours(1), Body: "Original comment.",
            ReplyTo: null, To: addressedTo, Resolves: null, UnknownHeaderFields: []);
        var card = new CardFile(frontmatter, "Body.", [comment], [], [], BlockCardFields.Empty, [], SectionCardFields.Empty);
        File.WriteAllText(path, CardFileWriter.Serialize(card), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static CardCommentPromoteOutcome.Promoted AssertPromoted(CardCommentPromoteOutcome outcome) =>
        outcome.Match(
            onPromoted: static promoted => promoted,
            onCommentNotFound: static notFound => throw new Xunit.Sdk.XunitException($"expected Promoted, got CommentNotFound: '{notFound.CommentId}'"),
            onRoleNotPermitted: static roleNotPermitted => throw new Xunit.Sdk.XunitException($"expected Promoted, got RoleNotPermitted: '{roleNotPermitted.AttemptedRole.ToWireString()}'"),
            onAlreadyResolved: static already => throw new Xunit.Sdk.XunitException($"expected Promoted, got AlreadyResolved: '{already.CommentId}'"),
            onRaisedCardAlreadyExists: static alreadyExists => throw new Xunit.Sdk.XunitException($"expected Promoted, got RaisedCardAlreadyExists: '{alreadyExists.FilePath}'"),
            onRaisedCardLayoutMismatch: static layoutMismatch => throw new Xunit.Sdk.XunitException($"expected Promoted, got RaisedCardLayoutMismatch: {layoutMismatch.Reason}"),
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
