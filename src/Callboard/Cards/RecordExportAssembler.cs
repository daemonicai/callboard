namespace Callboard.Cards;

/// <summary>
/// Gathers the cards a <c>section export</c> or <c>change export</c> renders (§11 block C,
/// record-retrieval: "The system SHALL render a section, or a whole change, as a single readable
/// document ... containing its cards, threads, verdicts and findings in reading order"). Reads the
/// primary record directly, through <see cref="CardLayout.ResolveRecordDirectories"/> — the same
/// method <see cref="CardIdentityResolver.Resolve"/> itself walks — never the derived index (D4 /
/// ADR-0004: "Derived state SHALL NOT be authoritative for anything"). Archived changes are in
/// scope deliberately: an export produced after the change that raised a card has archived must
/// still find it (record-retrieval: "Closed cards leave the working set without leaving the
/// repository").
///
/// <para>
/// <b>Skipped is not silent (§13.5).</b> Both methods return the cards they gathered
/// <em>and</em> every file they could not parse, as <see cref="UnreadableCard"/> — the shape every
/// other read reports through. An export that quietly omitted a corrupt card rendered a document
/// that reads as the whole section when it is not; the omission is now stated in the same
/// response that reports the card count.
/// </para>
///
/// <para>
/// <b>A card whose own file cannot be parsed is skipped, not refused
/// (record-retrieval: "Damage to any single card SHALL NOT compromise any other card").</b> Neither
/// method below fails a whole export because one sibling file in the searched directories is
/// corrupt — the same "isolate each file's outcome from every other's" discipline
/// <see cref="CardStore.ReadAllCards"/> already documents. This differs from <see cref="CardStore.
/// ArchiveChange"/>'s own stricter <c>CardsUnreadable</c> refusal, which guards a *write* (moving a
/// directory it has not fully read) rather than a read-only render.
/// </para>
/// </summary>
internal static class RecordExportAssembler
{
    /// <summary>
    /// The stated reading order (§11 block C brief: "Reading order must be stated, not incidental")
    /// — cards are sorted by their own <see cref="CardFrontmatter.Created"/> timestamp ascending,
    /// ties broken by <see cref="CardFrontmatter.Id"/> (ordinal), which is what makes exporting the
    /// same record twice byte-identical regardless of filesystem enumeration order. Each card's own
    /// comment thread is rendered in the order already recorded on the card — oldest first, exactly
    /// as <see cref="CardFile.Comments"/> stores it — never re-sorted a second way.
    /// </summary>
    internal const string ReadingOrderDescription =
        "cards sorted by their own 'created' timestamp ascending, ties broken by id (ordinal); each " +
        "card's own comment thread is rendered in the order already recorded on the card.";

    /// <summary>
    /// Every card belonging to <paramref name="sectionCard"/>: the section card itself, plus every
    /// card anywhere in the record whose <see cref="CardFrontmatter.Section"/> names it — the
    /// generic field every kind carries (a rule promoted out of a change, a question raised within
    /// it, a block or finding scoped to it), not merely the cards physically filed under the same
    /// change directory.
    /// </summary>
    internal static (IReadOnlyList<(string FilePath, CardFile Card)> Cards, IReadOnlyList<UnreadableCard> Unreadable) CardsForSection(
        string cardsRoot, CardFile sectionCard)
    {
        var sectionId = sectionCard.Frontmatter.Id;
        var results = new List<(string FilePath, CardFile Card)>();
        var unreadable = new List<UnreadableCard>();

        foreach (var directory in CardLayout.ResolveRecordDirectories(cardsRoot))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var (filePath, parseResult) in CardStore.ReadAllCards(directory))
            {
                var card = parseResult.CardOrRecordUnreadable(filePath, unreadable);
                if (card is null)
                {
                    continue;
                }

                if (string.Equals(card.Frontmatter.Id, sectionId, StringComparison.Ordinal)
                    || string.Equals(card.Frontmatter.Section, sectionId, StringComparison.Ordinal))
                {
                    results.Add((filePath, card));
                }
            }
        }

        return (SortReadingOrder(results), UnreadableCards.Ordered(unreadable));
    }

    /// <summary>
    /// Every card belonging to the change filed at <paramref name="changeDirectory"/>: every card
    /// whose <see cref="CardFrontmatter.Section"/> names one of that change's own <c>section</c>
    /// cards (found by first walking <paramref name="changeDirectory"/> alone, since a section is
    /// change-scoped and so is always filed there), plus every card physically filed in
    /// <paramref name="changeDirectory"/> that names no section at all — a block or finding
    /// created before it was tied to a section still belongs to the change and must not be silently
    /// dropped from a whole-change export.
    /// </summary>
    internal static (IReadOnlyList<(string FilePath, CardFile Card)> Cards, IReadOnlyList<UnreadableCard> Unreadable) CardsForChange(
        string cardsRoot, string changeDirectory)
    {
        var unreadable = new List<UnreadableCard>();
        var sectionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (sectionScanPath, parseResult) in CardStore.ReadAllCards(changeDirectory))
        {
            var card = parseResult.CardOrRecordUnreadable(sectionScanPath, unreadable);
            if (card is not null && CardStore.IsSectionCard(card))
            {
                sectionIds.Add(card.Frontmatter.Id);
            }
        }

        var results = new List<(string FilePath, CardFile Card)>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var directory in CardLayout.ResolveRecordDirectories(cardsRoot))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var (filePath, parseResult) in CardStore.ReadAllCards(directory))
            {
                var card = parseResult.CardOrRecordUnreadable(filePath, unreadable);
                if (card is null)
                {
                    continue;
                }

                var belongsToOneOfThisChangesSections =
                    sectionIds.Contains(card.Frontmatter.Id) || sectionIds.Contains(card.Frontmatter.Section);
                if (belongsToOneOfThisChangesSections && seenPaths.Add(filePath))
                {
                    results.Add((filePath, card));
                }
            }
        }

        foreach (var (filePath, parseResult) in CardStore.ReadAllCards(changeDirectory))
        {
            var card = parseResult.CardOrRecordUnreadable(filePath, unreadable);
            if (card is not null && card.Frontmatter.Section.Length == 0 && seenPaths.Add(filePath))
            {
                results.Add((filePath, card));
            }
        }

        return (SortReadingOrder(results), UnreadableCards.Ordered(unreadable));
    }

    private static IReadOnlyList<(string FilePath, CardFile Card)> SortReadingOrder(List<(string FilePath, CardFile Card)> cards)
    {
        cards.Sort(static (a, b) =>
        {
            var byCreated = a.Card.Frontmatter.Created.CompareTo(b.Card.Frontmatter.Created);
            return byCreated != 0 ? byCreated : string.CompareOrdinal(a.Card.Frontmatter.Id, b.Card.Frontmatter.Id);
        });

        return cards;
    }
}
