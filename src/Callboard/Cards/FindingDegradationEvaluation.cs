namespace Callboard.Cards;

/// <summary>
/// The outer result of <see cref="FindingDegradationEvaluator.Evaluate"/> — a closed union
/// distinguishing "here is the finding's degradation status" from "the finding's own directory
/// carries more than one <c>section</c> card matching its label, and picking one would be a guess,
/// not a derivation" (reviewer blocker, §6 block D remediation). <see cref="CardLayout.DirectoryFor"/>
/// resolves <see cref="CardScope.Section"/> and <see cref="CardScope.Change"/> to the same directory,
/// so more than one <c>section</c> card can carry the same free-text <c>Section</c> label with
/// nothing in this codebase refusing it at write time (no section-creation verb exists yet to guard
/// the invariant) — <see cref="Ambiguous"/> is this evaluator's way of failing closed on that
/// condition rather than silently picking whichever file <see
/// cref="Callboard.Cards.CardStore.ReadAllCards"/> happened to enumerate first.
/// </summary>
internal abstract record FindingDegradationEvaluation
{
    private FindingDegradationEvaluation()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<FindingDegradationStatus, TResult> onResolved,
        Func<string, IReadOnlyList<string>, TResult> onAmbiguous);

    internal static FindingDegradationEvaluation Resolved(FindingDegradationStatus status) => new ResolvedCase(status);

    internal static FindingDegradationEvaluation Ambiguous(string label, IReadOnlyList<string> filePaths) => new AmbiguousCase(label, filePaths);

    private sealed record ResolvedCase(FindingDegradationStatus Status) : FindingDegradationEvaluation
    {
        internal override TResult Match<TResult>(Func<FindingDegradationStatus, TResult> onResolved, Func<string, IReadOnlyList<string>, TResult> onAmbiguous) =>
            onResolved(Status);
    }

    /// <param name="Label">The finding's own <see cref="CardFrontmatter.Section"/> label that more
    /// than one <c>section</c> card in the directory carries.</param>
    /// <param name="FilePaths">Every conflicting <c>section</c> card's file path, ordered
    /// <see cref="StringComparer.Ordinal"/> for a deterministic message.</param>
    private sealed record AmbiguousCase(string Label, IReadOnlyList<string> FilePaths) : FindingDegradationEvaluation
    {
        internal override TResult Match<TResult>(Func<FindingDegradationStatus, TResult> onResolved, Func<string, IReadOnlyList<string>, TResult> onAmbiguous) =>
            onAmbiguous(Label, FilePaths);
    }
}
