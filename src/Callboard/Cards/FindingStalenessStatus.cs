namespace Callboard.Cards;

/// <summary>
/// What <see cref="FindingStalenessEvaluator.Evaluate"/> answers for one <c>finding</c> card — a
/// closed union of exactly four cases, the same discipline every other union in <c>Cards/</c>
/// follows (see <see cref="CardKind"/>'s own doc comment) and, in particular, the same shape
/// <see cref="GateStatus"/> already established for "absent is a different answer from passing":
/// <b>never collapse "cannot be measured" into "current"</b>. §6 block C ruling: "Staleness is only
/// measurable for an Explicit extent, and the other forms must say so rather than answer
/// 'current'... A finding whose staleness cannot be measured must never be reported as current —
/// that is §5's gate lesson exactly."
///
/// <list type="bullet">
/// <item><see cref="Current"/> — the extent's declared content is unchanged since <c>verified_at</c>.</item>
/// <item><see cref="Stale"/> — the extent's declared content has changed (including a file inside
/// it having been deleted or become unreadable — that is a change, not an error). Carries a
/// human-readable <see cref="StaleCase.Reason"/> naming what changed.</item>
/// <item><see cref="NotMeasurable"/> — the finding's extent has no fingerprintable file set at all
/// (an <see cref="FindingExtent.Instrument"/> or <see cref="FindingExtent.BlockScope"/> extent, or
/// an <see cref="FindingExtent.Explicit"/> extent recorded before this field existed and so has no
/// baseline to compare against). Carries a <see cref="NotMeasurableCase.Reason"/> naming why.</item>
/// <item><see cref="NotApplicable"/> — the finding is <see cref="FindingDisposition.ArguedClean"/>
/// (findings: "Findings that argue rather than measure are dispositioned separately... The system
/// SHALL NOT apply staleness computation to such a finding"). Carries a
/// <see cref="NotApplicableCase.Reason"/> stating that plainly.</item>
/// </list>
///
/// <para>
/// <b>"Stale is not wrong" is structural here, not prose (§6 block C ruling: "the type must carry
/// that... make the wrong thing unrepresentable rather than merely unwritten").</b> There is no
/// <c>Incorrect</c>/<c>Refuted</c>/<c>Wrong</c> case on this union, and <see cref="StaleCase.Reason"/>
/// is the only field a caller can read off <see cref="Stale"/> — nothing on this type, and nothing
/// on <see cref="FindingStalenessEvaluator"/>, can produce a value that says a stale finding was
/// wrong. Every reason string this type's producer builds is required, by that producer's own doc
/// comment, to describe re-verification rather than refutation — see
/// <see cref="FindingStalenessEvaluator"/>'s doc comment for where that convention is enforced.
/// </para>
/// </summary>
internal abstract record FindingStalenessStatus
{
    private FindingStalenessStatus()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<TResult> onCurrent,
        Func<string, TResult> onStale,
        Func<string, TResult> onNotMeasurable,
        Func<string, TResult> onNotApplicable);

    internal static readonly FindingStalenessStatus Current = new CurrentCase();

    internal static FindingStalenessStatus Stale(string reason) => new StaleCase(reason);

    internal static FindingStalenessStatus NotMeasurable(string reason) => new NotMeasurableCase(reason);

    internal static FindingStalenessStatus NotApplicable(string reason) => new NotApplicableCase(reason);

    private sealed record CurrentCase : FindingStalenessStatus
    {
        internal override TResult Match<TResult>(Func<TResult> onCurrent, Func<string, TResult> onStale, Func<string, TResult> onNotMeasurable, Func<string, TResult> onNotApplicable) =>
            onCurrent();
    }

    private sealed record StaleCase(string Reason) : FindingStalenessStatus
    {
        internal override TResult Match<TResult>(Func<TResult> onCurrent, Func<string, TResult> onStale, Func<string, TResult> onNotMeasurable, Func<string, TResult> onNotApplicable) =>
            onStale(Reason);
    }

    private sealed record NotMeasurableCase(string Reason) : FindingStalenessStatus
    {
        internal override TResult Match<TResult>(Func<TResult> onCurrent, Func<string, TResult> onStale, Func<string, TResult> onNotMeasurable, Func<string, TResult> onNotApplicable) =>
            onNotMeasurable(Reason);
    }

    private sealed record NotApplicableCase(string Reason) : FindingStalenessStatus
    {
        internal override TResult Match<TResult>(Func<TResult> onCurrent, Func<string, TResult> onStale, Func<string, TResult> onNotMeasurable, Func<string, TResult> onNotApplicable) =>
            onNotApplicable(Reason);
    }
}
