namespace Callboard.Cards;

/// <summary>
/// One entry in a card's append-only thread (card-model: "Append-only addressed comment
/// threads"). <see cref="To"/> is the structural addressing the spec requires — routing reads
/// this field, never the prose in <see cref="Body"/>. Nothing in this type, or anywhere that
/// constructs it, offers a way to mutate or remove a comment once appended; a correction is a
/// further <see cref="CardComment"/>.
/// </summary>
/// <param name="ReplyTo">The identity of the comment this one replies to, or <c>null</c> when it
/// does not reply to anything.</param>
/// <param name="To">The role this comment is addressed to, or <c>null</c> when it addresses no
/// one in particular.</param>
/// <param name="Resolves">The identity of the comment this one resolves, or <c>null</c> when it
/// resolves nothing. This is how resolution is recorded (architect's ruling, DEVLOG §4 block C):
/// the spec forbids editing or removing an existing comment, and a comment's own <c>Resolved</c>
/// flag would be exactly that field being flipped after the fact, so resolution is instead an
/// appended comment naming the comment it resolves — the same shape as <see cref="ReplyTo"/>. A
/// comment is live (unresolved) for queue-routing purposes exactly when no later comment in the
/// same thread <see cref="Resolves"/> it — see <c>CardCommentRouting</c>, which computes that over
/// a whole thread rather than this type carrying a settable "am I resolved" bit that a fresh
/// construction could set independently of the thread and disagree with it.</param>
/// <param name="UnknownHeaderFields">Header fields this build's parser does not recognise,
/// captured verbatim (raw key, raw — still comment-header-escaped — value) in the order they were
/// read, and re-emitted the same way on write. A future section's own field (or a hand-added one)
/// must survive an unrelated <c>AppendComment</c> intact rather than being silently dropped on the
/// next read-modify-write — see <see cref="CardFrontmatter"/>'s equivalent note. Empty for every
/// comment this section itself ever constructs.</param>
internal sealed record CardComment(
    string Id,
    CardOwner Author,
    DateTimeOffset Timestamp,
    string Body,
    string? ReplyTo,
    CardOwner? To,
    string? Resolves,
    IReadOnlyList<(string Key, string RawValue)> UnknownHeaderFields)
{
    // The compiler-generated record equality compares UnknownHeaderFields by reference (neither
    // List<T> nor T[] gives (string,string) tuples structural sequence equality across different
    // concrete list types), which would make a freshly-constructed comment never equal to the
    // same comment after a parse round trip even when every field genuinely matches. Overridden
    // here to compare by sequence instead, everything else left at the compiler-generated meaning.
    public bool Equals(CardComment? other) =>
        other is not null
        && Id == other.Id
        && Author == other.Author
        && Timestamp == other.Timestamp
        && Body == other.Body
        && ReplyTo == other.ReplyTo
        && EqualityComparer<CardOwner?>.Default.Equals(To, other.To)
        && Resolves == other.Resolves
        && UnknownHeaderFields.SequenceEqual(other.UnknownHeaderFields);

    public override int GetHashCode() =>
        HashCode.Combine(Id, Author, Timestamp, Body, ReplyTo, To, Resolves, UnknownHeaderFields.Count);
}
