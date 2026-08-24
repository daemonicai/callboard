namespace Callboard.Cards;

/// <summary>
/// One enumerated claim of an approval (review-certification: "Certification enumerates its
/// claims", §8 block A) — one entry in a <c>block</c> card's append-only claim sequence
/// (<see cref="CardFile.Claims"/>). Carries its own stable <see cref="Id"/> (Architect ruling: "each
/// claim carrying its own id") — a reader discussing one claim from an enumerated set needs a
/// handle to name it, and claim text is prose that cannot itself serve as that handle (two claims
/// can read identically; an id never does). Modelled as its own append-only entry rather than a
/// comma-joined frontmatter list for the same reason <see cref="BlockCardFields"/>'s own doc comment
/// gives for <see cref="BlockCardFields.Tasks"/>: claim text is free-form prose that will contain
/// commas, and a comma-joined scalar has no room for a per-item id anyway.
/// </summary>
/// <param name="Id">This claim's stable identity — never recycled, never reused by a later claim,
/// generated once when the claim is first recorded.</param>
/// <param name="Round">The block's remediation round this claim was certified in — the same scoping
/// <see cref="GateResult.Round"/> already established for "only the current round's evidence is
/// evidence"; a claim from an earlier round stays on the card (never destroyed) but is not what a
/// reader asking "what does the current approval claim" sees.</param>
/// <param name="Text">The claim itself, free-form prose — written to be actionable by a reviewer who
/// did not author it (review-certification: "Certification text SHALL be written to be actionable by
/// a reviewer who did not author it").</param>
/// <param name="UnknownFields">Claim-line fields this build's parser does not recognise, captured
/// verbatim (raw key, raw value) in the order they were read, and re-emitted the same way on write —
/// the same extensibility rule <see cref="CardBlockTransitionEntry.UnknownFields"/> applies. Empty
/// for every entry this build itself constructs.</param>
internal sealed record CardApprovalClaim(
    string Id,
    int Round,
    string Text,
    IReadOnlyList<(string Key, string RawValue)> UnknownFields)
{
    /// <summary>
    /// <see cref="Text"/> is never empty or whitespace-only — the same "an empty item is
    /// unrepresentable" discipline <see cref="BlockCardFields.IsValidListItem"/> already applies to
    /// its own wire values. Checked by <see cref="Callboard.Cli.CommandParser"/>'s <c>block approve</c>
    /// parse arm before a claim ever reaches <see cref="CardStore"/>, and reacted to identically by
    /// <see cref="CardFileParser"/>'s own pre-construction check on a value read straight off the
    /// wire — the same "one predicate, two doors" discipline <see cref="SectionVerdictEntry.
    /// IsValidRangeValue"/>'s own doc comment describes for exactly this reason.
    /// </summary>
    internal static bool IsValidText(string text) => !string.IsNullOrWhiteSpace(text);

    // Same reason as CardBlockTransitionEntry's own override: the compiler-generated equality
    // compares UnknownFields by reference, which would make a freshly-constructed entry never equal
    // to the same entry after a parse round trip even when every field genuinely matches.
    public bool Equals(CardApprovalClaim? other) =>
        other is not null
        && Id == other.Id
        && Round == other.Round
        && Text == other.Text
        && UnknownFields.SequenceEqual(other.UnknownFields);

    public override int GetHashCode() =>
        HashCode.Combine(Id, Round, Text, UnknownFields.Count);
}
