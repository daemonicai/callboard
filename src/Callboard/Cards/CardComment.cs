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
/// <param name="Resolved">Whether this comment's thread has been marked resolved. Resolution
/// <em>semantics</em> — who may resolve, what resolving does to queue routing — is 4.6/4.7's
/// business; this only carries the flag.</param>
internal sealed record CardComment(
    string Id,
    CardOwner Author,
    DateTimeOffset Timestamp,
    string Body,
    string? ReplyTo,
    CardOwner? To,
    bool Resolved);
