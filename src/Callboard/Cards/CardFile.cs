namespace Callboard.Cards;

/// <summary>
/// A card as read from, or destined for, one file: frontmatter, a Markdown body, and its
/// append-only comment thread in order. This is the in-memory shape <see cref="CardFileParser"/>
/// produces and <see cref="CardFileWriter"/> consumes — the ADR-0003 record, not yet touching how
/// it reaches disk (atomic rename and locking are 2.5–2.6, block B).
/// </summary>
/// <param name="UnknownFrontmatterFields">
/// Frontmatter lines this build's parser does not recognise, captured verbatim (raw key, raw —
/// still frontmatter-escaped — value) in the order they were read, and re-emitted the same way
/// (after the known fields, before the closing fence) on write. This is the extensibility rule §2
/// owns for the format: a read-modify-write cycle (<c>AppendComment</c>) must never silently
/// destroy a field it does not itself understand — a §5/§6 field on a card written by a newer
/// build, or a line a human hand-added, survives an unrelated append instead of vanishing on the
/// next write. Preserving rather than refusing is the better fit for degraded mode (ADR-0003:
/// "legible without the tool" pairs with a record humans are expected to hand-edit). Always empty
/// for a card this build itself constructs from scratch.
/// </param>
internal sealed record CardFile(
    CardFrontmatter Frontmatter,
    string Body,
    IReadOnlyList<CardComment> Comments,
    IReadOnlyList<(string Key, string RawValue)> UnknownFrontmatterFields);
