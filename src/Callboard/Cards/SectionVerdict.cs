namespace Callboard.Cards;

/// <summary>
/// The two verdicts a supervisor records against a section's commit range (work-lifecycle:
/// "Sections are entities" — "the verdict, the range and the acting role are recorded against that
/// section entity", §5 block E): <c>approve</c> or <c>request-changes</c>, matching the vocabulary
/// the OpenSpec Apply Workflow's own supervisor review already uses (<c>CLAUDE.md</c> §3c). Modelled
/// the same way as <see cref="SectionFlowState"/> — a private constructor and two sealed nested
/// cases close the hierarchy to this file, and <see cref="Match{TResult}"/> is the only way to
/// consume a value.
/// </summary>
internal abstract record SectionVerdict
{
    private SectionVerdict()
    {
    }

    internal abstract TResult Match<TResult>(
        Func<TResult> onApprove,
        Func<TResult> onRequestChanges);

    internal static readonly SectionVerdict Approve = new ApproveCase();
    internal static readonly SectionVerdict RequestChanges = new RequestChangesCase();

    private sealed record ApproveCase : SectionVerdict
    {
        internal override TResult Match<TResult>(Func<TResult> onApprove, Func<TResult> onRequestChanges) => onApprove();
    }

    private sealed record RequestChangesCase : SectionVerdict
    {
        internal override TResult Match<TResult>(Func<TResult> onApprove, Func<TResult> onRequestChanges) => onRequestChanges();
    }
}

/// <summary>
/// The wire form of <see cref="SectionVerdict"/> — the text a verdict entry's <c>verdict</c> field
/// carries — and the parse path back. Ordinal comparison throughout, same reason as
/// <see cref="CardKindWireFormat"/>.
/// </summary>
internal static class SectionVerdictWireFormat
{
    private static readonly IReadOnlyDictionary<string, SectionVerdict> ByWireValue =
        new Dictionary<string, SectionVerdict>(StringComparer.Ordinal)
        {
            ["approve"] = SectionVerdict.Approve,
            ["request-changes"] = SectionVerdict.RequestChanges,
        };

    internal static string ToWireString(this SectionVerdict verdict) => verdict.Match(
        onApprove: static () => "approve",
        onRequestChanges: static () => "request-changes");

    /// <summary>The recognised wire values, in the order this type's spec text lists them.</summary>
    internal static string RecognisedValues => string.Join(", ", ByWireValue.Keys);

    internal static bool TryParse(string value, out SectionVerdict verdict)
    {
        var found = ByWireValue.TryGetValue(value, out var match);
        // Every value stored in ByWireValue is a non-null SectionVerdict singleton, so `match` is
        // non-null whenever `found` is true; the fallback to Approve on failure is discarded by
        // every caller, which always checks the returned bool first.
        verdict = found ? match! : SectionVerdict.Approve;
        return found;
    }
}
