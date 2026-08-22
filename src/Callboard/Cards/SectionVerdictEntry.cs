namespace Callboard.Cards;

/// <summary>
/// One entry in a <c>section</c> card's append-only supervisor-verdict history (work-lifecycle:
/// "Sections are entities" — "the verdict, the range and the acting role are recorded against that
/// section entity", §5 block E). Modelled the same way §5 block C modelled
/// <see cref="CardBlockTransitionEntry"/>: a section can accumulate more than one verdict across
/// remediation rounds (<c>CLAUDE.md</c> §3c — "Request changes" then, after a remediation block, a
/// second "Approve"), so this is its own append-only sequence rather than a pair of overwritable
/// scalars — the same "every" reasoning <see cref="CardHandover"/>'s own doc comment already
/// applies, even though work-lifecycle's text here says "a supervisor records a verdict" singular
/// rather than "every": a second verdict recorded against a card that already carries one from an
/// earlier round must not silently discard the first, since that earlier round's finding is part of
/// the section's own audit trail.
/// </summary>
/// <param name="By">The role that recorded this verdict — always the supervisor in practice, but
/// recorded rather than assumed, the same reason <see cref="CardBlockTransitionEntry.By"/> is
/// recorded rather than assumed to be whichever role a transition's name implies.</param>
/// <param name="Verdict">The verdict recorded.</param>
/// <param name="RangeFrom">The commit range's start — the section's <c>base</c> at the time this
/// verdict was recorded, in the ordinary case, but not enforced equal to it: the range is the
/// supervisor's own stated <c>git diff</c> boundary, data on the entity, never re-derived from git
/// at read time (§5 block E brief).</param>
/// <param name="RangeTo">The commit range's end — <c>HEAD</c> at the time this verdict was
/// recorded.</param>
/// <param name="Timestamp">When this verdict was recorded.</param>
/// <param name="UnknownFields">Verdict-line fields this build's parser does not recognise, captured
/// verbatim (raw key, raw value) in the order they were read, and re-emitted the same way on
/// write — the same extensibility rule <see cref="CardBlockTransitionEntry.UnknownFields"/>
/// applies. Empty for every entry this build itself constructs.</param>
internal sealed record SectionVerdictEntry(
    CardOwner By,
    SectionVerdict Verdict,
    string RangeFrom,
    string RangeTo,
    DateTimeOffset Timestamp,
    IReadOnlyList<(string Key, string RawValue)> UnknownFields)
{
    /// <summary>
    /// A range endpoint (<see cref="RangeFrom"/> or <see cref="RangeTo"/>) is never empty or
    /// whitespace-only — the same discipline <see cref="GateResult.IsValidLabel"/> and
    /// <see cref="BlockCardFields.IsValidListItem"/> already apply to their own wire values.
    /// <b>The one predicate both doors into this value react to</b> (reviewer finding, §5 block E
    /// remediation): <see cref="Callboard.Cli.CommandParser"/>'s <c>section verdict</c> parse arm
    /// refuses an invalid value with <c>invalid-range</c> before <see cref="CardStore.
    /// RecordSectionVerdict"/> is ever called, and <see cref="CardFileParser"/>'s own
    /// pre-construction check on a value read straight off the wire (a hand-edited file) reacts to
    /// the same predicate — so a value the CLI would have refused to write can never be silently
    /// accepted as "present" by one door while being refused as "missing" by the other. That
    /// disagreement — the CLI accepting an empty <c>--range-from ""</c> while the file parser's own
    /// required-field check treated the empty value it wrote as absent — is exactly the defect this
    /// predicate closes: the tool's own write path produced a card the tool's own read path then
    /// refused as corrupt, on the happy path, with no hand-edit involved.
    /// </summary>
    internal static bool IsValidRangeValue(string value) => !string.IsNullOrWhiteSpace(value);

    // Same reason as CardBlockTransitionEntry's own override: the compiler-generated equality
    // compares UnknownFields by reference, which would make a freshly-constructed entry never equal
    // to the same entry after a parse round trip even when every field genuinely matches.
    public bool Equals(SectionVerdictEntry? other) =>
        other is not null
        && By == other.By
        && Verdict == other.Verdict
        && RangeFrom == other.RangeFrom
        && RangeTo == other.RangeTo
        && Timestamp == other.Timestamp
        && UnknownFields.SequenceEqual(other.UnknownFields);

    public override int GetHashCode() =>
        HashCode.Combine(By, Verdict, RangeFrom, RangeTo, Timestamp, UnknownFields.Count);
}
