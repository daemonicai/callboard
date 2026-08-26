namespace Callboard.Cards;

/// <summary>
/// The routing semantics card-model's "Append-only addressed comment threads" requirement
/// describes, computed over a card's comment thread rather than stored on any one comment.
///
/// <para>
/// <b>A role's queue is not "cards it owns"</b> (the requirement's first scenario: a card whose
/// <c>owner</c> is another role still appears in <c>reviewer</c>'s queue once a comment addresses
/// it). <see cref="BelongsInQueue"/> is the union of ownership and a live addressed thread — two
/// independent sources of the same answer, not one collapsing into the other.
/// </para>
///
/// <para>
/// <b>Resolution is per-comment, not per-card</b> (the requirement's third scenario: "the card
/// ceases to appear in that role's queue <em>on account of that comment</em>"). Two comments
/// addressed to the same role, one resolved and one not, leave the card in the queue — a single
/// card-level boolean cannot express that, so this type never computes one; it always asks
/// "is *this* comment live" and only then folds the per-comment answers into one queue verdict.
/// </para>
///
/// <para>
/// Pure functions over <see cref="IReadOnlyList{CardComment}"/> — everything here is
/// reconstructible from the record alone (ADR-0004 / D4), so serving a role's queue from the
/// derived index is a caching decision, never a source of truth one.
/// </para>
/// </summary>
internal static class CardCommentRouting
{
    /// <summary>
    /// True when the comment at <paramref name="index"/> in <paramref name="comments"/> is
    /// resolved: some comment later in the same append order names it via
    /// <see cref="CardComment.Resolves"/>. "Later" means later in the thread's own append
    /// order (the list's own order, oldest first — record-retrieval: "the thread's order is
    /// preserved"), not by timestamp, so this never depends on trusting a caller-supplied clock
    /// value. A resolving comment can itself be resolved by a further comment; only its own
    /// direct resolvers matter here, one link at a time, exactly as <see cref="CardComment.ReplyTo"/>
    /// is one link at a time.
    /// </summary>
    internal static bool IsResolved(IReadOnlyList<CardComment> comments, int index)
    {
        var targetId = comments[index].Id;
        for (var i = index + 1; i < comments.Count; i++)
        {
            if (comments[i].Resolves == targetId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="comments"/> carries at least one live thread addressed to
    /// <paramref name="role"/> — a comment whose <see cref="CardComment.To"/> is
    /// <paramref name="role"/> and for which <see cref="IsResolved"/> is false. A role mention in
    /// prose never reaches this: only <see cref="CardComment.To"/>, the structural field, is
    /// read (requirement: "a role mention in body text SHALL NOT route anything").
    /// </summary>
    internal static bool HasLiveThreadAddressedTo(IReadOnlyList<CardComment> comments, CardOwner role)
    {
        for (var i = 0; i < comments.Count; i++)
        {
            if (comments[i].To == role && !IsResolved(comments, i))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A card's membership in <paramref name="role"/>'s queue: it owns the card, or the card
    /// carries a live thread addressed to it (or both). This is the union the requirement's first
    /// scenario names — a card owned by one role and addressed to another belongs in both queues
    /// at once.
    /// </summary>
    internal static bool BelongsInQueue(CardOwner owner, IReadOnlyList<CardComment> comments, CardOwner role) =>
        owner == role || HasLiveThreadAddressedTo(comments, role);

    /// <summary>
    /// True when <paramref name="actingRole"/> may dispose of a thread — <c>resolve</c>,
    /// <c>promote</c>, or <c>decline</c> — addressed to <paramref name="threadAddressedTo"/> on a
    /// card owned by <paramref name="cardOwner"/> (process-enforcement: "A thread is disposed of
    /// only by its addressee or the card's owner", Product Owner ruling, §10). Deliberately admits
    /// the card's owner disposing of a thread addressed to a different role — addressee-only was on
    /// the table and was not chosen — and, when <paramref name="threadAddressedTo"/> is <see
    /// langword="null"/>, admits only <paramref name="cardOwner"/>: there is no addressee for the
    /// first arm to satisfy. See <see cref="CardCommentResolveOutcome.RoleNotPermitted"/>'s own doc
    /// comment for the ruling's deliberate consequences, written down.
    /// </summary>
    internal static bool IsPermittedToDisposeThread(CardOwner cardOwner, CardOwner? threadAddressedTo, CardOwner actingRole) =>
        actingRole == cardOwner || actingRole == threadAddressedTo;

    /// <summary>
    /// The ids of every live thread addressed to <paramref name="role"/> — <see cref="
    /// HasLiveThreadAddressedTo"/>'s own sibling, naming the set rather than a bare boolean, the
    /// same "the refusal names the set, never a bare count" shape <see cref="LiveUndispositionedNitIds"/>
    /// already establishes (process-enforcement: "A verdict cannot leave threads unanswered" — "the
    /// system refuses and lists the unresolved threads"). Only <see cref="CardComment.To"/> is read,
    /// not a role mention in body text, for the same reason <see cref="HasLiveThreadAddressedTo"/>
    /// gives.
    /// </summary>
    internal static IReadOnlyList<string> LiveThreadIdsAddressedTo(IReadOnlyList<CardComment> comments, CardOwner role)
    {
        var ids = new List<string>();
        for (var i = 0; i < comments.Count; i++)
        {
            if (comments[i].To == role && !IsResolved(comments, i))
            {
                ids.Add(comments[i].Id);
            }
        }

        return ids;
    }

    /// <summary>
    /// True when the nit at <paramref name="index"/> has received a disposition (review-certification:
    /// "Nits carry a disposition", §8 block B) — some comment later in the same append order both
    /// <see cref="CardComment.Resolves"/> it and itself carries a <see cref="CardComment.Disposition"/>.
    /// Requiring both, rather than reusing bare <see cref="IsResolved"/>, is deliberate: a nit is a
    /// closed union of exactly three outcomes (spec: "A nit SHALL cease to be live only through one
    /// of these three dispositions"), not merely "some later comment happened to reply to it" — an
    /// ordinary reply that also names <see cref="CardComment.Resolves"/> for some other reason must
    /// not silently count as a disposition. <see cref="CardComment.IsNit"/> at <paramref name="index"/>
    /// is not itself checked here — a caller asking this question about a non-nit comment gets a
    /// vacuous but harmless answer (no later comment both resolves it and carries a disposition,
    /// since nothing in this codebase ever sets <see cref="CardComment.Disposition"/> on a comment
    /// resolving a non-nit) rather than a defensive throw, matching <see cref="IsResolved"/>'s own
    /// unconditional shape.
    /// </summary>
    internal static bool IsNitDispositioned(IReadOnlyList<CardComment> comments, int index)
    {
        var targetId = comments[index].Id;
        for (var i = index + 1; i < comments.Count; i++)
        {
            if (comments[i].Resolves == targetId && comments[i].Disposition is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Every nit in <paramref name="comments"/> that is still live — raised (<see cref="CardComment.
    /// IsNit"/>) and not yet dispositioned (<see cref="IsNitDispositioned"/>) — in append order. This
    /// is what review-certification's "Undispositioned nits block the verdict" scenario reads: the
    /// refusal it names states exactly this set, never a bare count (§8 block B brief: "The refusal
    /// names the undispositioned nits").
    /// </summary>
    internal static IReadOnlyList<string> LiveUndispositionedNitIds(IReadOnlyList<CardComment> comments)
    {
        var ids = new List<string>();
        for (var i = 0; i < comments.Count; i++)
        {
            if (comments[i].IsNit && !IsNitDispositioned(comments, i))
            {
                ids.Add(comments[i].Id);
            }
        }

        return ids;
    }

    /// <summary>
    /// True when any comment in <paramref name="comments"/> carries a
    /// <see cref="NitDisposition.FixBeforeLand"/> disposition. <see cref="LiveUndispositionedNitIds"/>'s
    /// sibling for review-certification's fix-before-land edge (§8 block B remediation): whether
    /// the edge applies turns on whether <em>the round</em>, taken as a whole, carries a
    /// fix-before-land nit — not on whether the call that happens to empty the live set is itself
    /// the fix-before-land one. A property of the whole slice handed to it, computed fresh each
    /// time, never stored on one comment — the same idiom this type's own class doc comment
    /// records and <see cref="LiveUndispositionedNitIds"/> already follows. The caller narrows
    /// <paramref name="comments"/> to the round in question (comments have no round of their own)
    /// before calling this, the same way a caller narrows any other input to a query in this type
    /// rather than this type filtering by round itself.
    /// </summary>
    internal static bool HasFixBeforeLandDisposition(IReadOnlyList<CardComment> comments)
    {
        for (var i = 0; i < comments.Count; i++)
        {
            if (comments[i].Disposition == NitDisposition.FixBeforeLand)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The ids of every live thread addressed to <em>any</em> role — <see cref="
    /// LiveThreadIdsAddressedTo"/>'s role-agnostic sibling (process-enforcement: "Section close
    /// settles its addressed threads", §9 block E). A section close is not acting as any one role,
    /// so the check it needs is "is anyone still owed a resolution", not "is a specific actor still
    /// owed one" — the same <see cref="CardComment.To"/>-only, body-text-never-routes discipline
    /// every other reader in this type already follows, just without narrowing by <paramref
    /// name="role"/>.
    /// </summary>
    internal static IReadOnlyList<string> LiveAddressedThreadIds(IReadOnlyList<CardComment> comments)
    {
        var ids = new List<string>();
        for (var i = 0; i < comments.Count; i++)
        {
            if (comments[i].To is not null && !IsResolved(comments, i))
            {
                ids.Add(comments[i].Id);
            }
        }

        return ids;
    }

    /// <summary>
    /// The ids of every live addressed thread on <paramref name="comments"/> that has survived at
    /// least one round boundary on its own block — process-enforcement: "the system SHALL surface
    /// addressed comments left unresolved for longer than one round, as a prompt rather than a
    /// constraint" (§9 block E). "Round" belongs to a block card, not a section (work-lifecycle:
    /// <c>round</c> lives on <see cref="BlockCardFields"/> and increments only on the three
    /// transitions <see cref="BlockFlowTransitions.RoundIncrementingTransitionNames"/> names) — so
    /// this reads a block's own <paramref name="transitions"/>, never a section-wide notion of
    /// round that does not exist. A live addressed comment counts as aged exactly when
    /// <paramref name="transitions"/> carries a round-incrementing entry timestamped after the
    /// comment's own <see cref="CardComment.Timestamp"/>: the round the comment was raised in has
    /// since closed out from under it while the thread itself is still open. A comment raised in
    /// the block's <em>current</em> round — no round-incrementing transition has happened since —
    /// is never aged by this reading, no matter how long ago it was posted; elapsed wall-clock time
    /// is not what "a round" means here.
    /// </summary>
    internal static IReadOnlyList<string> AgeingAddressedThreadIds(
        IReadOnlyList<CardComment> comments, IReadOnlyList<CardBlockTransitionEntry> transitions)
    {
        var ids = new List<string>();
        for (var i = 0; i < comments.Count; i++)
        {
            var comment = comments[i];
            if (comment.To is null || IsResolved(comments, i))
            {
                continue;
            }

            var agedPastItsOwnRound = transitions.Any(transition =>
                BlockFlowTransitions.RoundIncrementingTransitionNames.Contains(transition.Name, StringComparer.Ordinal)
                && transition.Timestamp > comment.Timestamp);
            if (agedPastItsOwnRound)
            {
                ids.Add(comment.Id);
            }
        }

        return ids;
    }
}
