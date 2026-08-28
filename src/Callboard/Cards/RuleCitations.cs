using System.Linq;

namespace Callboard.Cards;

/// <summary>
/// Citation counting (§7 block G, 7.10, register: "Register size triggers review, never
/// eviction") — Product Owner ruling: "a citation is a reference from another card. Counting walks
/// the record for cards naming a rule's id, through the same resolver block B built. No new verb,
/// no new state." <see cref="CountCitations"/> is that walk: every card file under every directory
/// <see cref="CardLayout.ResolveRecordDirectories"/> names (the same set <see cref="
/// CardIdentityResolver"/> already searches, live changes and archived ones alike — a citation in
/// an archived change still counts, the same "identity SHALL remain valid and resolvable after
/// archive" reach that resolver already gives every other reference) is read once, and the target
/// rule's id is looked for in that card's own narrative — its body and every comment's body, the
/// text an agent actually writes when it leans on a rule. A <em>distinct card</em> mentioning the id
/// at least once counts once, never once per mention within that card's own thread (the Product
/// Owner's accepted consequence, stated explicitly so it is not mistaken for an oversight).
///
/// <para>
/// <b>Derived, not stored (ADR-0004).</b> Nothing here is written back to any card or to the index;
/// every call recomputes from the record as it stands at the moment it is asked. The index may
/// cache this count later, but this type itself has no notion of a cache — it is a pure read.
/// </para>
///
/// <para>
/// <b>A token match, not a bare substring search.</b> A rule id (<c>rule-0001</c>) is checked with a
/// boundary on both sides — the character immediately before and after the match, if any, must not
/// itself be part of an identity token (letter, digit, <c>-</c> or <c>_</c>) — so a citation of
/// <c>rule-0001</c> is never mistaken for one of some longer token that merely contains it as a
/// substring.
/// </para>
///
/// <para>
/// <b>Best-effort over an unreadable file — and it says so (§13.5).</b> A card elsewhere in the
/// record that fails to parse is skipped rather than failing the whole count, and is returned
/// alongside it as an <see cref="UnreadableCard"/>. That matters more here than anywhere else a
/// read reports one: a citation count is the evidence <c>rule review</c> queues a rule for
/// retirement on, and a rule whose only citation lives in the one file that would not parse counts
/// zero. The count still stands (it is a trigger for a human review, never an automatic
/// retirement), but the caller can now see that it was taken over an incomplete record instead of
/// reading a bare <c>0</c> as "nothing cites this".
/// </para>
/// </summary>
internal static class RuleCitations
{
    /// <summary>
    /// The number of distinct cards, anywhere in the record, whose own body or comment thread
    /// names <paramref name="ruleId"/> at least once. <paramref name="ruleFilePath"/> — the rule's
    /// own card — is never counted as a citation of itself.
    /// </summary>
    internal static (int CitingCards, IReadOnlyList<UnreadableCard> Unreadable) CountCitations(
        string cardsRoot, string ruleId, string ruleFilePath)
    {
        var citingCards = 0;
        var unreadable = new List<UnreadableCard>();

        foreach (var directory in CardLayout.ResolveRecordDirectories(cardsRoot))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var (filePath, result) in CardStore.ReadAllCards(directory))
            {
                if (string.Equals(filePath, ruleFilePath, StringComparison.Ordinal))
                {
                    continue;
                }

                var card = result.CardOrRecordUnreadable(filePath, unreadable);

                if (card is null)
                {
                    continue;
                }

                if (NamesRuleId(card, ruleId))
                {
                    citingCards++;
                }
            }
        }

        return (citingCards, UnreadableCards.Ordered(unreadable));
    }

    private static bool NamesRuleId(CardFile card, string ruleId)
    {
        if (ContainsCitation(card.Body, ruleId))
        {
            return true;
        }

        return card.Comments.Any(comment => ContainsCitation(comment.Body, ruleId));
    }

    private static bool ContainsCitation(string text, string ruleId)
    {
        var searchFrom = 0;
        int index;
        while ((index = text.IndexOf(ruleId, searchFrom, StringComparison.Ordinal)) >= 0)
        {
            var precededByBoundary = index == 0 || !IsIdentityTokenChar(text[index - 1]);
            var followedByBoundary = index + ruleId.Length >= text.Length || !IsIdentityTokenChar(text[index + ruleId.Length]);
            if (precededByBoundary && followedByBoundary)
            {
                return true;
            }

            searchFrom = index + 1;
        }

        return false;
    }

    private static bool IsIdentityTokenChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_';

    /// <summary>
    /// Register: "The system SHALL count how often each rule is cited and SHALL surface a stated
    /// size ceiling as a trigger for a compaction review. The ceiling SHALL NOT act as a hard cap."
    /// A predicate, nothing more — it names the fact that the live rule set has passed
    /// <paramref name="ceiling"/>, and does not itself raise a review, retire a rule, or gate any
    /// other call in this build: §10 (not this block) is where a working-context response reads
    /// this and surfaces it as a trigger. <paramref name="ceiling"/> is a caller-supplied value, not
    /// a constant here — "a stated size ceiling" is stated by whoever calls this, not fixed by this
    /// type, so nothing here can itself act as the hard cap the requirement explicitly forbids.
    /// </summary>
    internal static bool CeilingPassed(int liveRuleCount, int ceiling) => liveRuleCount > ceiling;

    /// <summary>
    /// The number of live (<see cref="RegisterLifecycleState.Open"/>) <c>rule</c> cards anywhere
    /// under <see cref="CardLayout.ResolveLiveRecordDirectories"/> — deliberately the same
    /// population <see cref="UncitedOpenRules"/> walks (same directories, same open-rule
    /// predicate), so the ceiling and the queue are always measured against one identical set, and
    /// excluding the archive for the exact same reason <see cref="UncitedOpenRules"/>'s own doc
    /// comment gives. A pure count, not a decision: it names how many rules are live, nothing about
    /// whether that is too many — <see cref="CeilingPassed"/>'s <c>liveRuleCount</c> parameter is
    /// this figure, stated against a caller-supplied ceiling by <c>rule review</c> (§10 block E).
    /// </summary>
    internal static (int Count, IReadOnlyList<UnreadableCard> Unreadable) CountLiveOpenRules(string cardsRoot)
    {
        var count = 0;
        var unreadable = new List<UnreadableCard>();

        foreach (var directory in CardLayout.ResolveLiveRecordDirectories(cardsRoot))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var (filePath, result) in CardStore.ReadAllCards(directory))
            {
                var card = result.CardOrRecordUnreadable(filePath, unreadable);

                if (card is null || !CardStore.IsRuleCard(card))
                {
                    continue;
                }

                // TryParse failing here is unreachable: §12 block A's parse door
                // (CardFileParser.ValidateStatus) never hands back a rule-kind CardFile whose status
                // does not parse against RegisterLifecycleStateWireFormat. Kept rather than asserted,
                // so a rule read some other way still fails closed instead of being counted wrongly.
                if (RegisterLifecycleStateWireFormat.TryParse(card.Frontmatter.Status, out var state) &&
                    ReferenceEquals(state, RegisterLifecycleState.Open))
                {
                    count++;
                }
            }
        }

        return (count, UnreadableCards.Ordered(unreadable));
    }

    /// <summary>
    /// Register: "A rule that is never cited SHALL be placed in a review queue for a human and
    /// SHALL NOT be retired automatically." The queue is this call's return value, computed fresh
    /// every time — never a persisted list a write path could forget to clear — over every
    /// <c>rule</c> card whose <c>status</c> is <see cref="RegisterLifecycleState.Open"/> and whose
    /// <see cref="CountCitations"/> is zero, walking only <see cref="CardLayout.
    /// ResolveLiveRecordDirectories"/> — never <see cref="CardLayout.ResolveRecordDirectories"/>'s
    /// archived changes (§7 remediation, blocker 2: <c>ResolveRecordDirectories</c> deliberately
    /// reaches into the archive for identity resolution and citation reach, but a never-promoted
    /// change-scoped rule left <c>open</c> when its change archived is not part of the live
    /// register, and counting it here would grow this queue and <see cref="CeilingPassed"/>'s input
    /// monotonically with every archived change, forever). A discharged rule is never queued: it
    /// has already left the live set by some other, human-driven act, and this queue only ever
    /// names rules still standing <em>and still live</em>. Nothing in this method discharges,
    /// hides, or otherwise touches any card it names — it only names them.
    /// </summary>
    internal static (IReadOnlyList<(string FilePath, CardFile Card)> Rules, IReadOnlyList<UnreadableCard> Unreadable) UncitedOpenRules(
        string cardsRoot)
    {
        var uncited = new List<(string FilePath, CardFile Card)>();
        var unreadable = new List<UnreadableCard>();

        foreach (var directory in CardLayout.ResolveLiveRecordDirectories(cardsRoot))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var (filePath, result) in CardStore.ReadAllCards(directory))
            {
                var card = result.CardOrRecordUnreadable(filePath, unreadable);

                if (card is null || !CardStore.IsRuleCard(card))
                {
                    continue;
                }

                // The !TryParse half of this condition is unreachable: §12 block A's parse door
                // (CardFileParser.ValidateStatus) never hands back a rule-kind CardFile whose status
                // does not parse against RegisterLifecycleStateWireFormat. Kept rather than asserted,
                // so a rule read some other way is still excluded from the queue instead of throwing.
                if (!RegisterLifecycleStateWireFormat.TryParse(card.Frontmatter.Status, out var state) ||
                    !ReferenceEquals(state, RegisterLifecycleState.Open))
                {
                    continue;
                }

                var (citingCards, citationUnreadable) = CountCitations(cardsRoot, card.Frontmatter.Id, filePath);
                unreadable.AddRange(citationUnreadable);
                if (citingCards == 0)
                {
                    uncited.Add((filePath, card));
                }
            }
        }

        return (uncited, UnreadableCards.Ordered(unreadable));
    }
}
