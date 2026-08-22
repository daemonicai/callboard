using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// 4.6/4.7 — the queue routing semantics card-model's "Append-only addressed comment threads"
/// requirement describes, and the architect's ruling on how resolution is recorded (DEVLOG §4
/// block C). <see cref="CardCommentRouting"/> is the surface under test; nothing here goes
/// through <see cref="CardStore"/> — these are pure functions over a comment list, deliberately
/// exercised without any file I/O so the routing question is answered independently of how the
/// comments got there.
/// </summary>
public sealed class CardCommentRoutingTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);

    // -----------------------------------------------------------------------------------------
    // Scenario: Addressed comment routes to its target — "that card appears in the reviewer
    // queue even though the card's owner is another role". A role's queue is therefore not
    // "cards it owns"; it is the union of ownership and a live addressed thread.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void BelongsInQueue_TrueForTheOwner_EvenWithNoComments()
    {
        Assert.True(CardCommentRouting.BelongsInQueue(CardOwner.Worker, [], CardOwner.Worker));
    }

    [Fact]
    public void BelongsInQueue_FalseForANonOwnerWithNoAddressedThread()
    {
        Assert.False(CardCommentRouting.BelongsInQueue(CardOwner.Worker, [], CardOwner.Reviewer));
    }

    [Fact]
    public void BelongsInQueue_TrueForAnAddresseeThatDoesNotOwnTheCard()
    {
        var comment = Comment("C-0001", CardOwner.Architect, to: CardOwner.Reviewer);

        // The spec's own words: the card's owner is worker, but the comment addresses reviewer.
        Assert.True(CardCommentRouting.BelongsInQueue(CardOwner.Worker, [comment], CardOwner.Reviewer));
    }

    // -----------------------------------------------------------------------------------------
    // Scenario: Role mention in prose does not route — addressing is structural (`To`), never a
    // read of `Body`.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void HasLiveThreadAddressedTo_IgnoresARoleNamedOnlyInProse()
    {
        var comment = new CardComment(
            "C-0001", CardOwner.Architect, Timestamp, "reviewer should look at this", null, To: null, null, []);

        Assert.False(CardCommentRouting.HasLiveThreadAddressedTo([comment], CardOwner.Reviewer));
        Assert.False(CardCommentRouting.BelongsInQueue(CardOwner.Worker, [comment], CardOwner.Reviewer));
    }

    // -----------------------------------------------------------------------------------------
    // Scenario: Resolved thread leaves the queue — "on account of that comment". Resolution is
    // an appended comment naming what it resolves (the ruling), and it is per-comment: a second,
    // still-live addressed comment keeps the card in the queue regardless of the first.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void HasLiveThreadAddressedTo_FalseOnceALaterCommentResolvesTheOnlyAddressedOne()
    {
        var question = Comment("C-0001", CardOwner.Architect, to: CardOwner.Reviewer);
        var resolution = Comment("C-0002", CardOwner.Reviewer, resolves: "C-0001");

        Assert.False(CardCommentRouting.HasLiveThreadAddressedTo([question, resolution], CardOwner.Reviewer));
    }

    [Fact]
    public void HasLiveThreadAddressedTo_TrueWhileAnotherAddressedCommentToTheSameRoleStaysUnresolved()
    {
        // Two independent threads addressed to reviewer, one resolved and one not — a card-level
        // boolean could not express this; per-comment resolution can.
        var first = Comment("C-0001", CardOwner.Architect, to: CardOwner.Reviewer);
        var firstResolved = Comment("C-0002", CardOwner.Reviewer, resolves: "C-0001");
        var second = Comment("C-0003", CardOwner.Architect, to: CardOwner.Reviewer);

        var thread = new[] { first, firstResolved, second };

        Assert.True(CardCommentRouting.HasLiveThreadAddressedTo(thread, CardOwner.Reviewer));
        Assert.True(CardCommentRouting.IsResolved(thread, 0));
        Assert.False(CardCommentRouting.IsResolved(thread, 2));
    }

    [Fact]
    public void IsResolved_TrueOnlyForTheCommentALaterOneNames()
    {
        var first = Comment("C-0001", CardOwner.Architect, to: CardOwner.Reviewer);
        var second = Comment("C-0002", CardOwner.Architect, to: CardOwner.Reviewer);
        var resolvesFirst = Comment("C-0003", CardOwner.Reviewer, resolves: "C-0001");

        var thread = new[] { first, second, resolvesFirst };

        Assert.True(CardCommentRouting.IsResolved(thread, 0));
        Assert.False(CardCommentRouting.IsResolved(thread, 1));
    }

    [Fact]
    public void IsResolved_LooksOnlyForwardInAppendOrder_AnEarlierResolvesFieldCannotResolveALaterComment()
    {
        // A comment can only name something already appended before it (ReplyTo has the same
        // shape) — resolving "forward" is not a thing this model can even express, but the
        // scanner itself only ever looks forward from a given index regardless, which this test
        // pins down directly.
        var resolvesNothingYet = Comment("C-0001", CardOwner.Reviewer, resolves: "C-0002");
        var second = Comment("C-0002", CardOwner.Architect, to: CardOwner.Reviewer);

        var thread = new[] { resolvesNothingYet, second };

        Assert.False(CardCommentRouting.IsResolved(thread, 1));
    }

    [Fact]
    public void BelongsInQueue_StaysTrueForTheOwnerAfterItsOwnAddressedThreadResolves()
    {
        var addressedToSelf = Comment("C-0001", CardOwner.Architect, to: CardOwner.Worker);
        var resolution = Comment("C-0002", CardOwner.Worker, resolves: "C-0001");

        Assert.True(CardCommentRouting.BelongsInQueue(CardOwner.Worker, [addressedToSelf, resolution], CardOwner.Worker));
    }

    private static CardComment Comment(string id, CardOwner author, CardOwner? to = null, string? resolves = null) =>
        new(id, author, Timestamp, "Body.", ReplyTo: null, To: to, Resolves: resolves, UnknownHeaderFields: []);
}
