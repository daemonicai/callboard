namespace Callboard.Cards;

/// <summary>
/// Computes findings' "Findings stale when their extent moves" and "Findings that argue rather than
/// measure are dispositioned separately" for one already-read <c>finding</c> card's
/// <see cref="FindingCardFields"/> — read-only, no lock, the same shape
/// <see cref="Callboard.Cli.CommandDispatcher.RunSectionStatus"/> already established for a
/// read verb (§5 block E precedent).
///
/// <para>
/// <b>Presented as calling for re-verification, never as refutation (§6 block C ruling, findings'
/// third scenario).</b> Every <see cref="FindingStalenessStatus.StaleCase.Reason"/> this method
/// builds says what changed and that it calls for re-verification; none says the finding was wrong,
/// incorrect, or invalid — that vocabulary constraint is enforced by this being the <em>only</em>
/// producer of <see cref="FindingStalenessStatus.Stale"/> in this codebase, so a reason string that
/// violated it would have exactly one place to look.
/// </para>
/// </summary>
internal static class FindingStalenessEvaluator
{
    /// <summary>
    /// <paramref name="repoRoot"/> is only ever consulted for an <see cref="FindingExtent.Explicit"/>
    /// extent under <see cref="FindingDisposition.Measured"/> — every other branch returns without
    /// touching the filesystem at all.
    /// </summary>
    internal static FindingStalenessStatus Evaluate(FindingCardFields fields, string repoRoot) =>
        fields.Disposition.Match(
            onMeasured: () => EvaluateMeasured(fields, repoRoot),
            onArguedClean: () => FindingStalenessStatus.NotApplicable(
                fields.VerifiedAt is { } verifiedAt
                    ? $"recorded as clean as argued at '{verifiedAt}' — reasoned over a claim, not measured, and not re-verifiable."
                    : "recorded as clean as argued — reasoned over a claim, not measured, and not re-verifiable; no verified_at state was recorded."));

    /// <summary>The <see cref="FindingDisposition.Measured"/> half — never reached for an
    /// <see cref="FindingDisposition.ArguedClean"/> finding; see <see cref="Evaluate"/>.</summary>
    private static FindingStalenessStatus EvaluateMeasured(FindingCardFields fields, string repoRoot) =>
        fields.Extent.Match(
            onInstrument: command => FindingStalenessStatus.NotMeasurable(
                $"an instrument-declared extent has no file set to fingerprint — re-verification requires re-running '{command}'."),
            onExplicit: items => EvaluateExplicit(fields.ExtentFingerprint, items, repoRoot),
            onBlockScope: static () => FindingStalenessStatus.NotMeasurable(
                "a block-scope extent has no enumerable file set to fingerprint — re-verification means re-checking the block's work as a whole."));

    private static FindingStalenessStatus EvaluateExplicit(
        FindingExtentFingerprint? recorded, System.Collections.Immutable.ImmutableArray<string> items, string repoRoot)
    {
        if (recorded is null)
        {
            // Covers §6 block B's own shipped writer, which recorded an Explicit extent before
            // this field existed — there is no baseline to compare against, and reporting Current
            // for "never actually fingerprinted" would be exactly the under-reporting §6 block C's
            // brief forbids.
            return FindingStalenessStatus.NotMeasurable(
                "no fingerprint was recorded for this extent — there is no baseline to compare against.");
        }

        var current = FindingExtentFingerprint.Compute(FindingExtent.Explicit(items), repoRoot)!;

        var recordedByPath = recorded.Files.ToDictionary(static file => file.RelativePath, StringComparer.Ordinal);
        var currentByPath = current.Files.ToDictionary(static file => file.RelativePath, StringComparer.Ordinal);

        var changedPaths = new List<string>();
        foreach (var path in recordedByPath.Keys.Union(currentByPath.Keys, StringComparer.Ordinal))
        {
            var recordedHash = recordedByPath.TryGetValue(path, out var recordedFile) ? recordedFile.ContentHash : null;
            var currentHash = currentByPath.TryGetValue(path, out var currentFile) ? currentFile.ContentHash : null;

            if (!string.Equals(recordedHash, currentHash, StringComparison.Ordinal))
            {
                changedPaths.Add(path);
            }
        }

        if (changedPaths.Count == 0)
        {
            return FindingStalenessStatus.Current;
        }

        changedPaths.Sort(StringComparer.Ordinal);
        return FindingStalenessStatus.Stale(
            $"the extent's declared content has changed since verified_at and calls for re-verification " +
            $"(this does not mean the finding was wrong) — affected path(s): {string.Join(", ", changedPaths)}.");
    }
}
