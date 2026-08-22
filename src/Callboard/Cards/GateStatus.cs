namespace Callboard.Cards;

/// <summary>
/// What a card reports for one gate label (work-lifecycle: "Gate results are recorded as exit
/// codes", §5 block D): <see cref="Absent"/> — no exit code has been recorded for this label,
/// regardless of what any comment on the card claims — or <see cref="Recorded"/>, carrying the
/// exit code actually written. <see cref="BlockCardFields.GateStatusOf"/> is the only way to
/// obtain one, and it reads exclusively from <see cref="BlockCardFields.GateResults"/> — a
/// structural fact, not a convention: nothing on this type or its one producer ever looks at
/// <see cref="CardFile.Comments"/>, so a narrative claim in a comment body has no path to this
/// answer, however it is phrased.
///
/// <para>
/// <b>Absent and failed are kept apart deliberately</b> (work-lifecycle: "the card shows that
/// gate as absent" — not "as failed"). Collapsing the two into one <c>bool Passed</c> would make
/// "nobody ran this gate yet" indistinguishable from "the gate ran and returned a non-zero exit
/// code" — the same conflation §5 block C's first remediation round found and fixed for
/// refusal/tool-failure/corrupt (<see cref="CardBlockTransitionOutcome"/>'s own doc comment).
/// <see cref="Passed"/> below still exists as the one place both cases collapse to a boolean, for
/// a caller that only needs "does this gate block a transition right now" — but it is a named,
/// visible collapse at the point of use, not a representation choice baked into the type.
/// </para>
/// </summary>
internal abstract record GateStatus
{
    private GateStatus()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<TResult> onAbsent,
        Func<int, TResult> onRecorded);

    internal static readonly GateStatus Absent = new AbsentCase();

    internal static GateStatus Recorded(int exitCode) => new RecordedCase(exitCode);

    /// <summary>
    /// Absent counts as not-passed, the same collapse work-lifecycle's own scenario describes
    /// ("the card shows that gate as absent, and transitions requiring it treat it as not
    /// passed") — a caller that only needs a boolean uses this rather than re-deriving it, but the
    /// distinction stays visible on <see cref="GateStatus"/> itself for a caller (like a rendered
    /// working-context view) that needs to say "never run" rather than "failed".
    /// </summary>
    internal bool Passed => Match(onAbsent: static () => false, onRecorded: static exitCode => exitCode == 0);

    private sealed record AbsentCase : GateStatus
    {
        internal override TResult Match<TResult>(Func<TResult> onAbsent, Func<int, TResult> onRecorded) => onAbsent();
    }

    private sealed record RecordedCase(int ExitCode) : GateStatus
    {
        internal override TResult Match<TResult>(Func<TResult> onAbsent, Func<int, TResult> onRecorded) => onRecorded(ExitCode);
    }
}
