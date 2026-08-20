namespace Callboard.Cards;

/// <summary>
/// A card as read from, or destined for, one file: frontmatter, a Markdown body, and its
/// append-only comment thread in order. This is the in-memory shape <see cref="CardFileParser"/>
/// produces and <see cref="CardFileWriter"/> consumes — the ADR-0003 record, not yet touching how
/// it reaches disk (atomic rename and locking are 2.5–2.6, block B).
/// </summary>
internal sealed record CardFile(
    CardFrontmatter Frontmatter,
    string Body,
    IReadOnlyList<CardComment> Comments);
