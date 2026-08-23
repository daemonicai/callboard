namespace Callboard.Cards;

/// <summary>
/// What <see cref="FindingDegradationEvaluator.Evaluate"/> answers once it has resolved (at most)
/// one section card to check — a closed union of exactly three cases, the same discipline every
/// other union in <c>Cards/</c> follows (see <see cref="CardKind"/>'s own doc comment). This is
/// orthogonal to <see cref="FindingStalenessStatus"/> (§6 block D ruling): degradation is about
/// <em>liveness</em> — whether the finding is still offered as live work — and staleness is about
/// whether a measured result still holds. A degraded finding still has a staleness answer, and a
/// <see cref="FindingDisposition.ArguedClean"/> finding still degrades; collapsing the two into one
/// status field would make them indistinguishable to a caller who needs to tell re-verification from
/// expiry apart, so they stay two separate types with two separate producers.
///
/// <list type="bullet">
/// <item><see cref="Live"/> — a matching, readable <c>section</c> card was found and is still open,
/// or the finding's directory holds no <c>section</c> card at all — no card of any label, readable
/// or not (an unresolvable section reads as <see cref="Live"/> rather than <see cref="Degraded"/> —
/// the record cannot prove closure, so it does not claim it).</item>
/// <item><see cref="Degraded"/> — a matching, readable <c>section</c> card was found and has closed
/// (findings: "the finding is no longer offered as live and remains retrievable by identity"). The
/// finding card itself is never rewritten to reach this state — see
/// <see cref="FindingDegradationEvaluator"/>.</item>
/// <item><see cref="Unreadable"/> — no <c>section</c> card matching this finding's label could be
/// confirmed, but the directory holds at least one candidate that cannot be ruled out: a card that
/// failed to parse (could be the finding's own section card gone corrupt), or a readable
/// <c>section</c> card carrying a different label (§6 remediation, B3 — <c>--section</c> is
/// unvalidated free text with no section-creation verb, so a differently-spelled label cannot be
/// told apart from a typo of this one). This is deliberately a different answer from
/// <see cref="Live"/>'s "no section card exists at all" — the same "absent is a different answer
/// from failed" convention §3 established for the derived index and <c>GateStatus</c> (reviewer
/// blocker, §6 block D remediation; widened to the zero-match case in the §6 section remediation).
/// Carries a <see cref="UnreadableCase.Reason"/> naming what could not be confirmed.</item>
/// </list>
/// </summary>
internal abstract record FindingDegradationStatus
{
    private FindingDegradationStatus()
    {
    }

    internal abstract TResult Match<TResult>(Func<TResult> onLive, Func<TResult> onDegraded, Func<string, TResult> onUnreadable);

    internal static readonly FindingDegradationStatus Live = new LiveCase();

    internal static readonly FindingDegradationStatus Degraded = new DegradedCase();

    internal static FindingDegradationStatus Unreadable(string reason) => new UnreadableCase(reason);

    private sealed record LiveCase : FindingDegradationStatus
    {
        internal override TResult Match<TResult>(Func<TResult> onLive, Func<TResult> onDegraded, Func<string, TResult> onUnreadable) => onLive();
    }

    private sealed record DegradedCase : FindingDegradationStatus
    {
        internal override TResult Match<TResult>(Func<TResult> onLive, Func<TResult> onDegraded, Func<string, TResult> onUnreadable) => onDegraded();
    }

    private sealed record UnreadableCase(string Reason) : FindingDegradationStatus
    {
        internal override TResult Match<TResult>(Func<TResult> onLive, Func<TResult> onDegraded, Func<string, TResult> onUnreadable) => onUnreadable(Reason);
    }
}
