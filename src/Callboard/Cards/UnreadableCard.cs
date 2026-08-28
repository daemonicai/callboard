namespace Callboard.Cards;

/// <summary>
/// One card file a read found on disk and could not parse — <em>which</em> file, and <em>why</em>
/// it would not parse (§13.5). The single shape every read reports its unparseable cards through:
/// <c>state</c>, <c>context</c>, <c>view</c>, both exports, <c>rule review</c> and
/// <c>section status</c> all carry <see cref="IReadOnlyList{T}"/> of this type, so a caller learns
/// the same two facts in the same shape whichever read it made, rather than each surface inventing
/// its own (this type replaces <c>BoardViewUnreadableEntry</c>, which was <c>view</c>'s own local
/// version of exactly this).
///
/// <para>
/// <b>A read reports; it does not refuse (Product Owner ruling, §13's task wording).</b>
/// record-retrieval's "Damage is contained" scenario requires every other card to stay readable
/// when one card's record is corrupted, so a read returns its result and names what it excluded
/// from it. Refusing the whole read would let one corrupt card halt every query — the containment
/// requirement failing in the opposite direction. Nothing here is recovered: <see cref="Reason"/>
/// is <see cref="CardFileParseResult.Failure.Reason"/> verbatim, the value already in hand at the
/// read site and — until this type existed — thrown away there.
/// </para>
///
/// <para>
/// <b>A fact about the file, never about the index (D4 / ADR-0004).</b> Every producer of this
/// type reads the card files themselves; the derived index is never consulted to decide whether a
/// card is readable, and could not be — the index holds what parsed, so a card missing from it is
/// indistinguishable there from a card that was never written.
/// </para>
/// </summary>
/// <param name="FilePath">The file the read walked and could not parse.</param>
/// <param name="Reason">Why it would not parse — <see cref="CardFileParseResult.Failure.Reason"/>
/// as the parser stated it.</param>
internal sealed record UnreadableCard(string FilePath, string Reason);

/// <summary>
/// The two operations every read that collects <see cref="UnreadableCard"/>s needs, so no read
/// site restates either: turning one <see cref="CardFileParseResult"/> into a card or a recorded
/// failure, and ordering the collected set for a deterministic response.
/// </summary>
internal static class UnreadableCards
{
    /// <summary>
    /// The parsed card, or <see langword="null"/> having appended <paramref name="filePath"/> and
    /// the parse failure's own reason to <paramref name="unreadable"/>. Replaces the
    /// <c>onFailure: static _ =&gt; null</c> idiom at every read site: the shape of the call is the
    /// same ("give me the card, or nothing"), but the failure is now carried out rather than
    /// discarded, so a caller cannot silently narrow the record without saying it did.
    /// </summary>
    internal static CardFile? CardOrRecordUnreadable(
        this CardFileParseResult result, string filePath, List<UnreadableCard> unreadable) =>
        result.Match<CardFile?>(
            onSuccess: static success => success.Card,
            onFailure: failure =>
            {
                unreadable.Add(new UnreadableCard(filePath, failure.Reason));
                return null;
            });

    /// <summary>
    /// <paramref name="unreadable"/> in <see cref="StringComparer.Ordinal"/> file-path order, with
    /// one entry per file. A read that walks the same directory more than once (<see cref="
    /// RuleCitations.UncitedOpenRules"/> re-walks the record once per candidate rule) would
    /// otherwise report the same corrupt file once per pass, making the count a fact about the
    /// walk rather than about the record. The first reason recorded for a path wins — the same
    /// file cannot parse two different ways within one invocation.
    /// </summary>
    internal static IReadOnlyList<UnreadableCard> Ordered(IEnumerable<UnreadableCard> unreadable) =>
    [
        .. unreadable
            .GroupBy(static entry => entry.FilePath, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static entry => entry.FilePath, StringComparer.Ordinal)
    ];
}
