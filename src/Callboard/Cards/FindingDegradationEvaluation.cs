namespace Callboard.Cards;

/// <summary>
/// The outer result of <see cref="FindingDegradationEvaluator.Evaluate"/> — a closed union
/// distinguishing "here is the finding's degradation status" from "more than one card file claims
/// the id this finding's own <c>section</c> field names, and picking one would be a guess, not a
/// derivation" (§7 block B: the evaluator is now rewired onto <see cref="CardIdentityResolver"/>,
/// so this case is reached via <see cref="CardIdentityResolution.Duplicate"/> rather than two
/// <c>section</c> cards sharing a free-text label — the same fail-closed shape, a different
/// underlying mechanism). <see cref="Ambiguous"/> is this evaluator's way of failing closed on that
/// condition rather than silently picking whichever file the resolver's walk happened to read
/// first.
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

    internal static FindingDegradationEvaluation Ambiguous(string id, IReadOnlyList<string> filePaths) => new AmbiguousCase(id, filePaths);

    private sealed record ResolvedCase(FindingDegradationStatus Status) : FindingDegradationEvaluation
    {
        internal override TResult Match<TResult>(Func<FindingDegradationStatus, TResult> onResolved, Func<string, IReadOnlyList<string>, TResult> onAmbiguous) =>
            onResolved(Status);
    }

    /// <param name="Id">The id this finding's own <see cref="CardFrontmatter.Section"/> field
    /// names, that more than one card file in the record claims.</param>
    /// <param name="FilePaths">Every conflicting file's path, ordered
    /// <see cref="StringComparer.Ordinal"/> for a deterministic message.</param>
    private sealed record AmbiguousCase(string Id, IReadOnlyList<string> FilePaths) : FindingDegradationEvaluation
    {
        internal override TResult Match<TResult>(Func<FindingDegradationStatus, TResult> onResolved, Func<string, IReadOnlyList<string>, TResult> onAmbiguous) =>
            onAmbiguous(Id, FilePaths);
    }
}
