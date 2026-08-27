using System.Linq;

namespace Callboard.Cards;

/// <summary>
/// One card in a role's queue (working-context, §10 block A) — identity enough to route on, never
/// its narrative. The card at index 0 of <see cref="WorkingContext.Queue"/> is the top item and is
/// what <see cref="WorkingContext.TopItem"/> expands into full detail; every other entry stays at
/// this shape (record-retrieval: "no narrative from cards outside its queue appears" — narrative on
/// a queue member that is not the top item is still narrative this response does not carry).
/// <see cref="ChangeName"/> is internal bookkeeping only, carried so the top item's own
/// <see cref="WorkingContextTopItem.BindingConstraints"/> can be computed without re-deriving it
/// from <see cref="FilePath"/> a second time — never surfaced on any other queue member's own CLI
/// shape.
/// </summary>
internal sealed record WorkingContextQueueEntry(string FilePath, CardFile Card, string? ChangeName);

/// <summary>
/// The top queue item, in full (working-context: "its body, base, referenced tasks, constraints,
/// unresolved threads addressed to the caller, and the previous round's verdict where one
/// exists"). <see cref="Card"/> and <see cref="FilePath"/> carry the body and, for a <c>block</c>
/// card, <c>base</c>/referenced tasks (<see cref="BlockCardFields.Tasks"/>) directly —
/// <see cref="BlockCardFields.Empty"/> for every other kind, the same "kind-specific fields default
/// to empty" convention every other reader of this type already follows.
/// </summary>
/// <param name="UnresolvedThreadIdsAddressedToCaller">The live thread ids on this card addressed
/// to the role that requested this context — <see cref="CardCommentRouting.
/// LiveThreadIdsAddressedTo"/>'s own result, carried here rather than recomputed by a
/// caller.</param>
/// <param name="BindingConstraints">"Constraints" (Product Owner ruling, §10 block A review): the
/// live rule and hazard cards whose scope covers this item — every repository-scoped one (they bind
/// every card), plus any change-scoped rule belonging to this item's own change. Not
/// <see cref="BlockCardFields.BlockedBy"/> — that field is untouched on the model; whether a
/// blocked-on relationship surfaces in this response at all is block C's ruling on halting, not this
/// one's. A subset of <see cref="WorkingContext.LiveRulesAndHazards"/>, in the same id order, never
/// a card outside it — a card-scoped view of part 1, not a fourth source of cards.</param>
/// <param name="PreviousRoundClaims">For a <c>block</c> card, the claims certified at
/// <see cref="BlockCardFields.Round"/> minus one — empty when the card is not a block, is at round
/// 1 or earlier, or no claim was certified that round. See <see cref="
/// WorkingContextAssembler.Build"/>'s own doc comment for the reading this follows.</param>
/// <param name="PreviousRoundLimits"><see cref="PreviousRoundClaims"/>'s sibling for limits.</param>
/// <param name="BlockedByIds"><see cref="BlockCardFields.BlockedBy"/> verbatim — what blocks the
/// top item, id-only (§10 remediation S4: blocked-ness is a property of the top item, and part 3
/// exists to deliver the top item in full). Empty for a non-block kind and for a block with no
/// entry, the same "kind-specific field, empty elsewhere" convention every other reader of
/// <see cref="BlockCardFields"/> already follows.</param>
/// <param name="Halted"><see langword="true"/> exactly when <see cref="CardStore.
/// FindBlockingOpenProductOwnerQuestion"/> resolves one of <paramref name="BlockedByIds"/> to a
/// live, Product-Owner-owned, open question — the same predicate <c>state</c>'s own
/// <see cref="DerivedStateBlockedCard.Halted"/> is computed from (§10 remediation S4: "reuse block
/// C's derivation ... or context and state will drift"), read here rather than re-derived a second
/// way.</param>
/// <param name="HaltedByQuestionId">The halting question's id, or <see langword="null"/> when
/// <paramref name="Halted"/> is <see langword="false"/>.</param>
/// <param name="HaltedByQuestionTitle">The halting question's title, or <see langword="null"/> when
/// <paramref name="Halted"/> is <see langword="false"/>.</param>
internal sealed record WorkingContextTopItem(
    string FilePath,
    CardFile Card,
    IReadOnlyList<string> UnresolvedThreadIdsAddressedToCaller,
    IReadOnlyList<(string FilePath, CardFile Card)> BindingConstraints,
    IReadOnlyList<CardApprovalClaim> PreviousRoundClaims,
    IReadOnlyList<CardApprovalLimit> PreviousRoundLimits,
    IReadOnlyList<string> BlockedByIds,
    bool Halted,
    string? HaltedByQuestionId,
    string? HaltedByQuestionTitle);

/// <summary>
/// A role's complete working context (working-context: "given a role, the system SHALL return that
/// role's complete working context, composed of exactly" four parts) — everything <see cref="
/// WorkingContextAssembler.Build"/> produces, assembled in the priority order block B's budget
/// measurement will walk (D6: "register, then brief, then unresolved threads ... then narrative"):
/// <see cref="LiveRulesAndHazards"/> first, then <see cref="Queue"/>, then <see cref="TopItem"/>.
/// This type carries no budget, truncation flag, or measurement of its own — that is block B's
/// addition to the shape, not this one's.
/// </summary>
/// <param name="LiveRulesAndHazards">Every live (<see cref="RegisterLifecycleState.Open"/>)
/// <c>rule</c> and <c>hazard</c> card anywhere in the live record, in card-id order — part 1,
/// delivered first and unconditionally, register: "the complete current set of live rule and
/// hazard cards". A <c>rule</c>/<c>hazard</c> card never also appears in <see cref="Queue"/>, even
/// when it is owned by the requesting role or carries an unresolved addressed thread (§10 block A
/// review, blocker 1: the spec composes the response of exactly three parts, and the same card
/// occupying two of them is that requirement failing) — this is its one and only home.</param>
/// <param name="Queue">The role's queue in full — part 2, see <see cref="WorkingContextAssembler.
/// QueueOrderDescription"/> for the stated ordering rule this response reports
/// alongside it.</param>
/// <param name="TopItem"><see cref="Queue"/>'s first element, expanded to the detail part 3
/// requires, or <see langword="null"/> when the queue is empty — there is no "top" of an empty
/// queue.</param>
internal sealed record WorkingContext(
    IReadOnlyList<(string FilePath, CardFile Card)> LiveRulesAndHazards,
    IReadOnlyList<WorkingContextQueueEntry> Queue,
    WorkingContextTopItem? TopItem);

/// <summary>
/// Assembles <see cref="WorkingContext"/> for a role (working-context, §10 block A). Reads the
/// primary record directly, via <see cref="CardLayout.ResolveLiveRecordDirectories"/> — never the
/// derived index, and never <see cref="CardLayout.ResolveRecordDirectories"/>'s archived changes
/// (Product Owner ruling, §10 opening: "correctness never rests on the index ... satisfied by
/// construction rather than by reconciliation"; carried item C). Every live card directory is
/// walked exactly once; <see cref="RuleCitations.CountCitations"/>'s O(rules × cards) walk is never
/// called from this path (carried item D — this is a per-brief path).
/// </summary>
internal static class WorkingContextAssembler
{
    /// <summary>
    /// The ordering rule <see cref="WorkingContext.Queue"/> is built under, stated in prose so the
    /// CLI response can carry it verbatim (working-context: "in a <em>stated</em> order" — the
    /// response says what the rule is, not merely that one exists).
    /// </summary>
    internal const string QueueOrderDescription =
        "cards the role owns, oldest 'updated' first; then cards it does not own but which carry an " +
        "unresolved comment addressed to it, oldest such comment first; ties broken by card id ascending.";

    /// <summary>
    /// The binding rule <see cref="WorkingContextTopItem.BindingConstraints"/> is computed under,
    /// stated in prose for the same reason <see cref="QueueOrderDescription"/> is (Product Owner
    /// ruling, §10 block A review).
    /// </summary>
    internal const string ConstraintsRuleDescription =
        "the live rule and hazard cards whose scope covers the top item: every repository-scoped one, " +
        "plus any change-scoped rule belonging to the top item's own change.";

    internal static WorkingContext Build(string cardsRoot, CardOwner role)
    {
        var liveRegister = new List<(string FilePath, CardFile Card, string? ChangeName)>();
        var owned = new List<(string FilePath, CardFile Card, string? ChangeName)>();
        var addressedOnly = new List<(string FilePath, CardFile Card, string? ChangeName, DateTimeOffset OldestAddressedComment)>();

        foreach (var directory in CardLayout.ResolveLiveRecordDirectories(cardsRoot))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var changeName = ChangeNameForDirectory(cardsRoot, directory);

            foreach (var (filePath, result) in CardStore.ReadAllCards(directory))
            {
                var card = result.Match<CardFile?>(
                    onSuccess: static success => success.Card,
                    onFailure: static _ => null);

                if (card is null)
                {
                    continue;
                }

                // A rule/hazard card's one and only home in this response is part 1 — it is never
                // also a queue candidate, whether or not it happens to be owned by, or addressed
                // to, the requesting role (§10 block A review, blocker 1). Checked, and the card
                // skipped from queue eligibility either way, before the closed check below: a
                // discharged rule/hazard was already excluded from the queue by that check, but an
                // open one owned by the role was not, which is exactly what let it leak into both
                // parts.
                if (CardStore.IsRuleCard(card) || CardStore.IsHazardCard(card))
                {
                    if (RegisterLifecycleStateWireFormat.TryParse(card.Frontmatter.Status, out var registerState)
                        && ReferenceEquals(registerState, RegisterLifecycleState.Open))
                    {
                        liveRegister.Add((filePath, card, changeName));
                    }

                    continue;
                }

                // working-context: "SHALL NOT contain closed cards" — checked once, here, before
                // either queue bucket, so a closed card never reaches ownership or addressing
                // consideration at all (a closed card owned by the role, or still carrying an
                // unresolved addressed thread, is excluded either way).
                if (CardLifecycle.IsClosed(card))
                {
                    continue;
                }

                if (card.Frontmatter.Owner == role)
                {
                    owned.Add((filePath, card, changeName));
                    continue;
                }

                var oldestAddressed = CardCommentRouting.OldestLiveAddressedTimestamp(card.Comments, role);
                if (oldestAddressed is not null)
                {
                    addressedOnly.Add((filePath, card, changeName, oldestAddressed.Value));
                }
            }
        }

        owned.Sort((a, b) => CompareByKeyThenId(a.Card.Frontmatter.Updated, a.Card.Frontmatter.Id, b.Card.Frontmatter.Updated, b.Card.Frontmatter.Id));
        addressedOnly.Sort((a, b) => CompareByKeyThenId(a.OldestAddressedComment, a.Card.Frontmatter.Id, b.OldestAddressedComment, b.Card.Frontmatter.Id));
        liveRegister.Sort(static (a, b) => string.CompareOrdinal(a.Card.Frontmatter.Id, b.Card.Frontmatter.Id));

        var queue = new List<WorkingContextQueueEntry>(owned.Count + addressedOnly.Count);
        queue.AddRange(owned.Select(static entry => new WorkingContextQueueEntry(entry.FilePath, entry.Card, entry.ChangeName)));
        queue.AddRange(addressedOnly.Select(static entry => new WorkingContextQueueEntry(entry.FilePath, entry.Card, entry.ChangeName)));

        WorkingContextTopItem? topItem = null;
        if (queue.Count > 0)
        {
            var top = queue[0];
            var threadIds = CardCommentRouting.LiveThreadIdsAddressedTo(top.Card.Comments, role);
            var constraints = BindingConstraints(liveRegister, top.ChangeName);
            var (claims, limits) = PreviousRoundVerdict(top.Card);
            var blockedByIds = top.Card.BlockFields.BlockedBy;
            var haltingQuestion = CardStore.FindBlockingOpenProductOwnerQuestion(cardsRoot, top.Card);
            topItem = new WorkingContextTopItem(
                top.FilePath, top.Card, threadIds, constraints, claims, limits,
                [.. blockedByIds], haltingQuestion is not null, haltingQuestion?.QuestionId, haltingQuestion?.Title);
        }

        return new WorkingContext(
            [.. liveRegister.Select(static entry => (entry.FilePath, entry.Card))],
            queue,
            topItem);
    }

    /// <summary>
    /// "Constraints" (Product Owner ruling, §10 block A review): the subset of
    /// <paramref name="liveRegister"/> whose scope covers the top item — every
    /// <see cref="CardScope.Repository"/>-scoped entry (a repository-scoped rule or a <c>hazard</c>,
    /// which register requires to always be repository-scoped, binds every card), plus any
    /// <see cref="CardScope.Change"/>-scoped rule whose own change equals
    /// <paramref name="topItemChangeName"/>. A top item that is not itself part of any change
    /// (<paramref name="topItemChangeName"/> is <see langword="null"/> — a repository-scoped card
    /// such as a <c>question</c>) is bound only by repository-scoped entries, since there is no
    /// change for a change-scoped rule to match. Preserves <paramref name="liveRegister"/>'s own id
    /// order — a card-scoped view of part 1, not a re-sort of it.
    /// </summary>
    private static IReadOnlyList<(string FilePath, CardFile Card)> BindingConstraints(
        IReadOnlyList<(string FilePath, CardFile Card, string? ChangeName)> liveRegister, string? topItemChangeName)
    {
        var binding = new List<(string FilePath, CardFile Card)>();
        foreach (var entry in liveRegister)
        {
            var scopeCoversTopItem = entry.Card.Frontmatter.Scope == CardScope.Repository
                || (entry.Card.Frontmatter.Scope == CardScope.Change
                    && topItemChangeName is not null
                    && string.Equals(entry.ChangeName, topItemChangeName, StringComparison.Ordinal));

            if (scopeCoversTopItem)
            {
                binding.Add((entry.FilePath, entry.Card));
            }
        }

        return binding;
    }

    /// <summary>
    /// The name of the live change <paramref name="directory"/> belongs to, or
    /// <see langword="null"/> when <paramref name="directory"/> is <see cref="CardLayout.
    /// RegisterDirectory"/> or <see cref="CardLayout.DecisionsDirectory"/> — the two entries in
    /// <see cref="CardLayout.ResolveLiveRecordDirectories"/>'s own result that are not one specific
    /// change's directory. Every other directory that method returns is exactly one live change's
    /// own container, named by its own last path segment (the same name <see cref="CardLayout.
    /// ChangesDirectory"/> builds it from). Trimmed on both sides before comparing — the same
    /// "<see cref="Path.Combine(string, string)"/> keeps a trailing separator, <see cref="
    /// Directory.EnumerateDirectories(string)"/> never does" mismatch <see cref="CardLayout.
    /// ArchiveRootPath"/> already guards against.
    ///
    /// <para>Internal, not private (§10 block C): <see cref="DerivedStateAssembler"/> reuses this
    /// to name each live change once, rather than re-deriving the same mapping a second way.</para>
    /// </summary>
    internal static string? ChangeNameForDirectory(string cardsRoot, string directory)
    {
        var trimmedDirectory = Path.TrimEndingDirectorySeparator(directory);
        var registerDirectory = Path.TrimEndingDirectorySeparator(
            Path.Combine(cardsRoot, CardLayout.RegisterDirectory.Replace('/', Path.DirectorySeparatorChar)));
        var decisionsDirectory = Path.TrimEndingDirectorySeparator(
            Path.Combine(cardsRoot, CardLayout.DecisionsDirectory.Replace('/', Path.DirectorySeparatorChar)));

        if (string.Equals(trimmedDirectory, registerDirectory, StringComparison.Ordinal)
            || string.Equals(trimmedDirectory, decisionsDirectory, StringComparison.Ordinal))
        {
            return null;
        }

        return Path.GetFileName(trimmedDirectory);
    }

    /// <summary>
    /// "The previous round's verdict" (working-context: "the previous round's verdict where one
    /// exists") — Architect ruling, §10 block A brief: a <em>block</em>'s verdict is its own
    /// round-scoped certification record (review-certification, §8 block A), <see cref="CardFile.
    /// Claims"/> and <see cref="CardFile.Limits"/> each carrying the <see cref="CardApprovalClaim.
    /// Round"/>/<see cref="CardApprovalLimit.Round"/> they were certified in — not <see cref="
    /// SectionVerdictEntry"/>, which is the supervisor's own verdict on a <c>section</c> card. For a
    /// block at <see cref="BlockCardFields.Round"/> <c>n</c> (defaulting to 1 when unset, the same
    /// default <see cref="BlockCardFields.GateStatusOf"/> uses), this reads round <c>n - 1</c> —
    /// empty when <paramref name="card"/> is not a block, is at round 1 (there is no round 0), or no
    /// claim/limit was certified that round.
    /// </summary>
    private static (IReadOnlyList<CardApprovalClaim> Claims, IReadOnlyList<CardApprovalLimit> Limits) PreviousRoundVerdict(CardFile card)
    {
        if (!CardStore.IsBlockCard(card))
        {
            return ([], []);
        }

        var currentRound = card.BlockFields.Round ?? 1;
        var previousRound = currentRound - 1;
        if (previousRound < 1)
        {
            return ([], []);
        }

        var claims = card.Claims.Where(claim => claim.Round == previousRound).ToList();
        var limits = card.Limits.Where(limit => limit.Round == previousRound).ToList();
        return (claims, limits);
    }

    private static int CompareByKeyThenId(DateTimeOffset aKey, string aId, DateTimeOffset bKey, string bId)
    {
        var keyComparison = aKey.CompareTo(bKey);
        return keyComparison != 0 ? keyComparison : string.CompareOrdinal(aId, bId);
    }
}
