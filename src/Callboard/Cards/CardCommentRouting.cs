namespace Callboard.Cards;

/// <summary>
/// The routing semantics card-model's "Append-only addressed comment threads" requirement
/// describes, computed over a card's comment thread rather than stored on any one comment.
///
/// <para>
/// <b>A role's queue is not "cards it owns"</b> (the requirement's first scenario: a card whose
/// <c>owner</c> is another role still appears in <c>reviewer</c>'s queue once a comment addresses
/// it). <see cref="BelongsInQueue"/> is the union of ownership and a live addressed thread — two
/// independent sources of the same answer, not one collapsing into the other.
/// </para>
///
/// <para>
/// <b>Resolution is per-comment, not per-card</b> (the requirement's third scenario: "the card
/// ceases to appear in that role's queue <em>on account of that comment</em>"). Two comments
/// addressed to the same role, one resolved and one not, leave the card in the queue — a single
/// card-level boolean cannot express that, so this type never computes one; it always asks
/// "is *this* comment live" and only then folds the per-comment answers into one queue verdict.
/// </para>
///
/// <para>
/// Pure functions over <see cref="IReadOnlyList{CardComment}"/> — everything here is
/// reconstructible from the record alone (ADR-0004 / D4), so serving a role's queue from the
/// derived index is a caching decision, never a source of truth one.
/// </para>
/// </summary>
internal static class CardCommentRouting
{
    /// <summary>
    /// True when the comment at <paramref name="index"/> in <paramref name="comments"/> is
    /// resolved: some comment later in the same append order names it via
    /// <see cref="CardComment.Resolves"/>. "Later" means later in the thread's own append
    /// order (the list's own order, oldest first — record-retrieval: "the thread's order is
    /// preserved"), not by timestamp, so this never depends on trusting a caller-supplied clock
    /// value. A resolving comment can itself be resolved by a further comment; only its own
    /// direct resolvers matter here, one link at a time, exactly as <see cref="CardComment.ReplyTo"/>
    /// is one link at a time.
    /// </summary>
    internal static bool IsResolved(IReadOnlyList<CardComment> comments, int index)
    {
        var targetId = comments[index].Id;
        for (var i = index + 1; i < comments.Count; i++)
        {
            if (comments[i].Resolves == targetId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="comments"/> carries at least one live thread addressed to
    /// <paramref name="role"/> — a comment whose <see cref="CardComment.To"/> is
    /// <paramref name="role"/> and for which <see cref="IsResolved"/> is false. A role mention in
    /// prose never reaches this: only <see cref="CardComment.To"/>, the structural field, is
    /// read (requirement: "a role mention in body text SHALL NOT route anything").
    /// </summary>
    internal static bool HasLiveThreadAddressedTo(IReadOnlyList<CardComment> comments, CardOwner role)
    {
        for (var i = 0; i < comments.Count; i++)
        {
            if (comments[i].To == role && !IsResolved(comments, i))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A card's membership in <paramref name="role"/>'s queue: it owns the card, or the card
    /// carries a live thread addressed to it (or both). This is the union the requirement's first
    /// scenario names — a card owned by one role and addressed to another belongs in both queues
    /// at once.
    /// </summary>
    internal static bool BelongsInQueue(CardOwner owner, IReadOnlyList<CardComment> comments, CardOwner role) =>
        owner == role || HasLiveThreadAddressedTo(comments, role);
}
