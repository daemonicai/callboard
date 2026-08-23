namespace Callboard.Cards;

/// <summary>
/// Whether a <c>finding</c> was <see cref="Measured"/> — checked with an instrument or an
/// enumerable extent, and so eligible for staleness computation (findings: "Findings stale when
/// their extent moves") — or <see cref="ArguedClean"/>: reasoned over a claim, with no instrument
/// to replay, recorded clean as argued at a named state (<see cref="FindingCardFields.VerifiedAt"/>)
/// and never re-verifiable (findings: "Findings that argue rather than measure are dispositioned
/// separately"). <b>A recorded fact, not an inference</b> — the spec's own wording is "SHALL be
/// recorded with a distinct disposition", so this is a fourth closed-union field on
/// <see cref="FindingCardFields"/> beside <see cref="FindingExtent"/>, not a value derived from it:
/// nothing about <see cref="FindingExtent"/>'s three cases distinguishes "I measured this and it
/// happens to have no enumerable file set" (<see cref="FindingExtent.BlockScope"/> or
/// <see cref="FindingExtent.Instrument"/>, both still <see cref="Measured"/>, both still
/// <see cref="FindingStalenessStatus.NotMeasurable"/> rather than <see cref="FindingStalenessStatus.
/// NotApplicable"/>) from "I did not measure this at all" (<see cref="ArguedClean"/>).
///
/// <para>
/// <b>Structural exclusion, not a conditional (§6 block C brief: "demonstrate that structurally if
/// you can, not by a conditional that could be removed").</b> <see cref="FindingStalenessEvaluator.
/// Evaluate"/> matches on this type first: the <see cref="ArguedClean"/> arm returns
/// <see cref="FindingStalenessStatus.NotApplicable"/> directly and never calls the helper that
/// inspects <see cref="FindingExtent"/> or <see cref="FindingCardFields.ExtentFingerprint"/> at
/// all — there is no branch reachable from <see cref="ArguedClean"/> that touches the extent, so an
/// argued-clean finding cannot be staleness-computed by construction of the match, not by a guard
/// that happens to run first.
/// </para>
///
/// <para>
/// <b>Undeclared and <see cref="Measured"/> are the same wire state</b> — the same "narrowing is
/// explicit by construction, default emits nothing" convention <see cref="FindingExtent.
/// BlockScope"/>'s own doc comment states: the writer never emits <c>disposition: measured</c>, so
/// every finding any build before this one wrote (with no <c>disposition</c> key at all) reads back
/// as <see cref="Measured"/> — the correct answer, since none of them could have declared
/// <see cref="ArguedClean"/> before this field existed.
/// </para>
/// </summary>
internal abstract record FindingDisposition
{
    private FindingDisposition()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<TResult> onMeasured,
        Func<TResult> onArguedClean);

    internal static readonly FindingDisposition Measured = new MeasuredCase();

    internal static readonly FindingDisposition ArguedClean = new ArguedCleanCase();

    private sealed record MeasuredCase : FindingDisposition
    {
        internal override TResult Match<TResult>(Func<TResult> onMeasured, Func<TResult> onArguedClean) => onMeasured();
    }

    private sealed record ArguedCleanCase : FindingDisposition
    {
        internal override TResult Match<TResult>(Func<TResult> onMeasured, Func<TResult> onArguedClean) => onArguedClean();
    }
}
