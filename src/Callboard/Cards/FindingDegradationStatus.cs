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
/// <item><see cref="Live"/> — the id this finding's own <c>section</c> field names resolves to a
/// <c>section</c> card that is still open, or no card anywhere in the record carries that id at all
/// (§7 block B: <see cref="CardIdentityResolution.NotFound"/> reads as <see cref="Live"/> — the
/// resolver has exhaustively searched the whole record, every file it touched parsed cleanly, and
/// none of them claims the id, so the record cannot prove closure and does not claim it).</item>
/// <item><see cref="Degraded"/> — the id resolves to a <c>section</c> card that has closed
/// (findings: "the finding is no longer offered as live and remains retrievable by identity"). The
/// finding card itself is never rewritten to reach this state — see
/// <see cref="FindingDegradationEvaluator"/>.</item>
/// <item><see cref="Unreadable"/> — closure could not be confirmed or ruled out: either the id
/// resolves to a card that is readable but not a <c>section</c> card, or
/// <see cref="CardIdentityResolver"/> reports <see cref="CardIdentityResolution.Unreadable"/> —
/// some file elsewhere in the record could not be read, so it might be the section card this
/// finding actually names (§6 remediation B3, re-applied by the resolver itself). This is
/// deliberately a different answer from <see cref="Live"/>'s "confirmed absent" — the same "absent
/// is a different answer from failed" convention §3 established for the derived index and
/// <c>GateStatus</c>. Carries a <see cref="UnreadableCase.Reason"/> naming what could not be
/// confirmed.</item>
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
