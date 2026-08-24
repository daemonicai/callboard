namespace Callboard.Cards;

/// <summary>
/// One stated limit of an approval — what the certification does NOT establish
/// (review-certification: "An approval SHALL enumerate the claims it makes and state what it does
/// not establish", §8 block A). One entry in a <c>block</c> card's append-only limit sequence
/// (<see cref="CardFile.Limits"/>). Unlike <see cref="CardApprovalClaim"/>, carries no id: a limit is
/// never individually asserted or refused (8.8's <c>recertify</c>, out of this block's scope,
/// re-asserts <em>claims</em> one at a time — the requirement text and its scenarios never mention
/// asserting a limit), so it needs no handle of its own, only the <see cref="Round"/> it was
/// certified in (Architect ruling: "Limits are part of the same certification record; they are never
/// individually asserted, so they need no ids").
/// </summary>
/// <param name="Round">The block's remediation round this limit was certified in — the same scoping
/// <see cref="CardApprovalClaim.Round"/> uses, for the same reason.</param>
/// <param name="Text">The limit itself, free-form prose — what a later reviewer reading this
/// certification cold must not assume it covers.</param>
/// <param name="UnknownFields">Limit-line fields this build's parser does not recognise, captured
/// verbatim (raw key, raw value) in the order they were read, and re-emitted the same way on write —
/// the same extensibility rule <see cref="CardApprovalClaim.UnknownFields"/> applies. Empty for
/// every entry this build itself constructs.</param>
internal sealed record CardApprovalLimit(
    int Round,
    string Text,
    IReadOnlyList<(string Key, string RawValue)> UnknownFields)
{
    /// <summary><see cref="Text"/> is never empty or whitespace-only — see <see cref="CardApprovalClaim.
    /// IsValidText"/>'s own doc comment; the same discipline applies here.</summary>
    internal static bool IsValidText(string text) => !string.IsNullOrWhiteSpace(text);

    // Same reason as CardApprovalClaim's own override: the compiler-generated equality compares
    // UnknownFields by reference, which would make a freshly-constructed entry never equal to the
    // same entry after a parse round trip even when every field genuinely matches.
    public bool Equals(CardApprovalLimit? other) =>
        other is not null
        && Round == other.Round
        && Text == other.Text
        && UnknownFields.SequenceEqual(other.UnknownFields);

    public override int GetHashCode() =>
        HashCode.Combine(Round, Text, UnknownFields.Count);
}
