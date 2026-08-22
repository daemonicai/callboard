namespace Callboard.Cards;

/// <summary>
/// The common frontmatter fields every card carries (card-model: "Single card entity with a kind
/// discriminator" plus "Scope determines lifetime"). Kind-specific fields — §5's <c>base</c>,
/// <c>reviewed_state</c>, <c>tasks</c>, <c>round</c>, <c>blocked_by</c>; §6's finding fields — are
/// not modelled here; this type covers only what every card, regardless of kind, has.
/// </summary>
/// <param name="Id">The card's stable, kind-prefixed identity (e.g. <c>B-0042</c>). Allocation is
/// 4.2's job; this type only carries the value.</param>
/// <param name="Owner">The role whose turn it is to act on this card right now — the
/// <em>current</em> state, kept from disagreeing with the ownership history
/// (<see cref="CardFile.Handovers"/>) by every code path that can set either: a brand-new card is
/// created through <see cref="CardStore.WriteCard"/> from a <see cref="NewCardFile"/>, which carries
/// no <see cref="CardFile.Handovers"/> at all, and <see cref="CardStore.TransferOwnershipUnderExistingLock"/>
/// sets this, in the same write, to exactly the incoming owner of the <see cref="CardHandover"/> it
/// appends (§4 remediation R3 — a prior version of this type let <see cref="CardStore.WriteCard"/>
/// take a caller-built <see cref="CardFile"/> with a non-empty <c>Handovers</c> that disagreed with
/// this field; that shape is no longer reachable). Every prior handover's attribution lives in the
/// append-only sequence, not here (card-model: "Every ownership change SHALL record the acting role
/// and the time it occurred" — reviewer round 1, finding 3: two overwritable scalars cannot satisfy
/// "every").</param>
/// <param name="Section">The section a card was raised within, or <see cref="string.Empty"/> when
/// the card is not tied to one.</param>
/// <remarks>
/// A field this build's parser does not recognise — a §5/§6 field on a card written by a newer
/// build, or a line a human hand-added — is <b>not</b> modelled here: it is carried on
/// <see cref="CardFile.UnknownFrontmatterFields"/> instead, verbatim, and re-emitted on the next
/// write rather than silently dropped. Keeping it off this type (rather than, say, a catch-all
/// dictionary here) keeps <see cref="CardFrontmatter"/> equality meaningful for known fields only —
/// exactly what block A's tests already compare it by.
/// </remarks>
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
