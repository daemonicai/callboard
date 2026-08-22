namespace Callboard.Cards;

/// <summary>
/// One entry in a card's append-only ownership-handover sequence (card-model: "Ownership names
/// whose turn it is" — "**Every** ownership change SHALL record the acting role and the time it
/// occurred"). Its own delimited block, distinct from <see cref="CardComment"/> (reviewer round 1,
/// finding 3; architect's direction): a handover has no author writing prose and no addressee —
/// routing reads a comment's <c>to</c> field, and a handover landing in the comment thread would
/// put it in a role's queue as though someone had asked that role a question. It also does not take
/// a dependency on block C's still-unbuilt comment semantics (structural addressing, replies,
/// resolution). Nothing in this type, or anywhere that constructs it, offers a way to mutate or
/// remove a handover once appended — see <see cref="CardStore.TransferOwnershipUnderExistingLock"/>,
/// the only production writer, which only ever appends.
/// </summary>
/// <param name="By">The role that performed this handover — not necessarily <paramref name="To"/>'s
/// previous or new value, since the acting role can be a third party (an architect reassigning
/// worker's card to reviewer is the ordinary case, not an edge case).</param>
/// <param name="To">The owner this handover made current. <see cref="CardFrontmatter.Owner"/> is
/// always exactly the <see cref="To"/> of the most recently appended entry — see that field's own
/// doc comment for how the two are kept from disagreeing.</param>
/// <param name="Timestamp">When this handover occurred.</param>
/// <param name="UnknownFields">Handover-line fields this build's parser does not recognise,
/// captured verbatim (raw key, raw value) in the order they were read, and re-emitted the same way
/// on write — the same extensibility rule <see cref="CardComment.UnknownHeaderFields"/> and
/// <see cref="CardFrontmatter"/>'s own unknown fields apply. Empty for every handover this build
/// itself constructs.</param>
internal sealed record CardHandover(
    CardOwner By,
    CardOwner To,
    DateTimeOffset Timestamp,
    IReadOnlyList<(string Key, string RawValue)> UnknownFields)
{
    // Same reason as CardComment's override: the compiler-generated equality compares
    // UnknownFields by reference, which would make a freshly-constructed handover never equal to
    // the same handover after a parse round trip even when every field genuinely matches.
    public bool Equals(CardHandover? other) =>
        other is not null
        && By == other.By
        && To == other.To
        && Timestamp == other.Timestamp
        && UnknownFields.SequenceEqual(other.UnknownFields);

    public override int GetHashCode() =>
        HashCode.Combine(By, To, Timestamp, UnknownFields.Count);
}
