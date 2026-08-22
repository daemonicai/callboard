namespace Callboard.Cards;

/// <summary>
/// One entry in a <c>block</c> card's append-only flow-transition history (work-lifecycle: "Every
/// transition SHALL record the acting role and the time it occurred", §5 block C). Modelled the
/// same way card-model already modelled ownership (<see cref="CardHandover"/>), because the two
/// spec sentences are the same shape: "every ownership change SHALL record the acting role and
/// the time it occurred" was found (card-model reviewer round 1, finding 3) not to be satisfiable
/// by two overwritable scalars, since "every" means the whole sequence stays recoverable, not just
/// the latest change. The same reasoning applies unchanged to "every transition SHALL record" —
/// so this is its own append-only sequence rather than a pair of scalars on
/// <see cref="CardFrontmatter"/>, and rather than folding into <see cref="CardHandover"/> itself
/// (whose <c>To</c> is a <see cref="CardOwner"/>, not a <see cref="BlockFlowState"/> — the two
/// events are not interchangeable data even though both are "who did this and when").
/// </summary>
/// <param name="By">The role that performed this transition.</param>
/// <param name="Name">The transition's wire name (<see cref="BlockFlowTransition.Name"/>). Kept
/// alongside <paramref name="From"/>/<paramref name="To"/> rather than derived from them on read:
/// in this section's table the (<paramref name="From"/>, <paramref name="To"/>) pair happens to
/// determine the name uniquely, but an entry should name the edge it recorded directly rather than
/// depend on that happening to remain true of a table a later section might extend.</param>
/// <param name="From">The state the block moved out of.</param>
/// <param name="To">The state the block moved into.</param>
/// <param name="Timestamp">When this transition occurred.</param>
/// <param name="UnknownFields">Transition-line fields this build's parser does not recognise,
/// captured verbatim (raw key, raw value) in the order they were read, and re-emitted the same way
/// on write — the same extensibility rule <see cref="CardHandover.UnknownFields"/> and
/// <see cref="CardComment.UnknownHeaderFields"/> apply. Empty for every entry this build itself
/// constructs.</param>
internal sealed record CardBlockTransitionEntry(
    CardOwner By,
    string Name,
    BlockFlowState From,
    BlockFlowState To,
    DateTimeOffset Timestamp,
    IReadOnlyList<(string Key, string RawValue)> UnknownFields)
{
    // Same reason as CardHandover's own override: the compiler-generated equality compares
    // UnknownFields by reference, which would make a freshly-constructed entry never equal to the
    // same entry after a parse round trip even when every field genuinely matches.
    public bool Equals(CardBlockTransitionEntry? other) =>
        other is not null
        && By == other.By
        && Name == other.Name
        && From == other.From
        && To == other.To
        && Timestamp == other.Timestamp
        && UnknownFields.SequenceEqual(other.UnknownFields);

    public override int GetHashCode() =>
        HashCode.Combine(By, Name, From, To, Timestamp, UnknownFields.Count);
}
