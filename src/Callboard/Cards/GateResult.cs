namespace Callboard.Cards;

/// <summary>
/// One recorded gate outcome on a <c>block</c> card (work-lifecycle: "A gate result SHALL be
/// recorded on the card as a label paired with the exit code the gate returned", §5 block D). The
/// exit code is the only thing this type carries about the run — no captured stdout/stderr, no
/// free-text verdict — because the exit code is the only accepted evidence a gate passed
/// (work-lifecycle: "gate output prose SHALL NOT be accepted as evidence"). There is nowhere on
/// this type, or anywhere upstream of it, for a caller to say "trust me, it passed" without an
/// exit code attached: <see cref="CardStore.RecordGateResult"/> is the only production path that
/// constructs one, and it takes an <c>int exitCode</c> parameter, not a narrative string.
/// </summary>
/// <param name="Label">The gate's name (e.g. <c>build</c>, <c>test</c>). Never empty or
/// whitespace-only — see <see cref="IsValidLabel"/>, the same discipline
/// <see cref="BlockCardFields.IsValidListItem"/> already applies to <c>tasks</c>/<c>blocked_by</c>
/// items. Never contains <c>=</c> or <c>,</c>: both are structural on the wire (<c>=</c> separates
/// label from exit code, <c>,</c> separates one gate's entry from the next) and a label containing
/// either would be ambiguous to read back, not merely inconvenient to escape.</param>
/// <param name="ExitCode">The exit code the gate actually returned. <c>0</c> means passed; any
/// other value means it ran and failed — see <see cref="GateStatus"/> for why "ran and failed" and
/// "never ran" are kept as distinct answers rather than collapsed into one boolean.</param>
internal sealed record GateResult(string Label, int ExitCode)
{
    internal static bool IsValidLabel(string label) =>
        !string.IsNullOrWhiteSpace(label) && !label.Contains('=', StringComparison.Ordinal) && !label.Contains(',', StringComparison.Ordinal);
}
