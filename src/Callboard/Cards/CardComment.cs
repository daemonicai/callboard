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
/// <param name="IsNit">&#160;<see langword="true"/> only for the comment that raised a nit
/// (review-certification: "A nit SHALL be raised as an addressed comment, not as a card", §8 block
/// B). <see langword="false"/> for every ordinary comment and for a disposition comment — a
/// disposition names the nit it dispositions via <paramref name="Resolves"/>, it does not carry
/// this flag itself.</param>
/// <param name="Required">The reviewer's optional marking that this nit is required — advisory
/// only (review-certification: "that marking SHALL NOT bind the architect's disposition"). Only
/// ever <see langword="true"/> when <paramref name="IsNit"/> is; meaningless, and always
/// <see langword="false"/>, otherwise.</param>
/// <param name="Sites">The sites the nit names, in the order recorded — repeatable
/// <c>--site</c> at raise time (Architect ruling: "record sites now … even though nothing in this
/// block reads them back", so a later block does not need to retrofit the wire format). Only ever
/// non-empty when <paramref name="IsNit"/> is.</param>
/// <param name="Disposition">The disposition this comment records, or <see langword="null"/> for
/// every comment that is not a disposition — including the nit-raising comment itself. A
/// disposition is a <em>later</em> comment naming the nit it dispositions via
/// <paramref name="Resolves"/>, never a mutation of the nit comment — <see cref="CardComment"/>
/// offers no mutation path by construction (this type's own class doc comment), the same idiom
/// <paramref name="Resolves"/> already established for a reply resolving an earlier comment.</param>
internal sealed record CardComment(
    string Id,
    CardOwner Author,
    DateTimeOffset Timestamp,
    string Body,
    string? ReplyTo,
    CardOwner? To,
    string? Resolves,
    IReadOnlyList<(string Key, string RawValue)> UnknownHeaderFields,
    bool IsNit = false,
    bool Required = false,
    IReadOnlyList<string>? Sites = null,
    NitDisposition? Disposition = null)
{
    /// <summary>Normalises the constructor's <see langword="null"/> <see cref="Sites"/> default to
    /// an empty list, once, here — the same reason <see cref="CardFile.Handovers"/> normalises its
    /// own <see langword="null"/> default, so every reader can treat "no sites" as an empty list
    /// rather than guarding against <see langword="null"/> separately.</summary>
    public IReadOnlyList<string> Sites { get; init; } = Sites ?? [];

    // The compiler-generated record equality compares UnknownHeaderFields/Sites by reference
    // (neither List<T> nor T[]/string[] gives sequence equality across different concrete list
    // types), which would make a freshly-constructed comment never equal to the same comment after
    // a parse round trip even when every field genuinely matches. Overridden here to compare by
    // sequence instead, everything else left at the compiler-generated meaning.
    public bool Equals(CardComment? other) =>
        other is not null
        && Id == other.Id
        && Author == other.Author
        && Timestamp == other.Timestamp
        && Body == other.Body
        && ReplyTo == other.ReplyTo
        && EqualityComparer<CardOwner?>.Default.Equals(To, other.To)
        && Resolves == other.Resolves
        && UnknownHeaderFields.SequenceEqual(other.UnknownHeaderFields)
        && IsNit == other.IsNit
        && Required == other.Required
        && Sites.SequenceEqual(other.Sites)
        && EqualityComparer<NitDisposition?>.Default.Equals(Disposition, other.Disposition);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        hash.Add(Id);
        hash.Add(Author);
        hash.Add(Timestamp);
        hash.Add(Body);
        hash.Add(ReplyTo);
        hash.Add(To);
        hash.Add(Resolves);
        hash.Add(UnknownHeaderFields.Count);
        hash.Add(IsNit);
        hash.Add(Required);
        hash.Add(Sites.Count);
        hash.Add(Disposition);
        return hash.ToHashCode();
    }
}
