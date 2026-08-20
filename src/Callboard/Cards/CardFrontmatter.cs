namespace Callboard.Cards;

/// <summary>
/// The common frontmatter fields every card carries (card-model: "Single card entity with a kind
/// discriminator" plus "Scope determines lifetime"). Kind-specific fields — §5's <c>base</c>,
/// <c>reviewed_state</c>, <c>tasks</c>, <c>round</c>, <c>blocked_by</c>; §6's finding fields — are
/// not modelled here; this type covers only what every card, regardless of kind, has.
/// </summary>
/// <param name="Id">The card's stable, kind-prefixed identity (e.g. <c>B-0042</c>). Allocation is
/// 4.2's job; this type only carries the value.</param>
/// <param name="Section">The section a card was raised within, or <see cref="string.Empty"/> when
/// the card is not tied to one.</param>
internal sealed record CardFrontmatter(
    string Id,
    CardKind Kind,
    string Title,
    string Status,
    CardOwner Owner,
    CardScope Scope,
    string Section,
    DateTimeOffset Created,
    DateTimeOffset Updated);
