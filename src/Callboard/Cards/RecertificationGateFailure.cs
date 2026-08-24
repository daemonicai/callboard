namespace Callboard.Cards;

/// <summary>
/// One gate label <see cref="CardRecertificationOutcome.GatesNotGreen"/> names as recorded, at the
/// card's current round, with a non-zero exit code (review-certification: "Recertification is
/// bounded", §8 block D). Distinct from a label carrying no result at all this round — see
/// <see cref="CardRecertificationOutcome.GatesNotGreen.AbsentLabels"/> — the same "ran and failed"
/// vs. "never ran" distinction <see cref="GateStatus"/> already keeps apart.
/// </summary>
internal sealed record RecertificationGateFailure(string Label, int ExitCode);
