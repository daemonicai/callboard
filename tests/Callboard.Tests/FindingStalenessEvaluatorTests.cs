using Callboard.Cards;

namespace Callboard.Tests;

/// <summary>
/// §6 block C, domain layer: <see cref="FindingStalenessEvaluator"/> — findings' "Findings stale
/// when their extent moves" and "Findings that argue rather than measure are dispositioned
/// separately". CLI-level coverage (the JSON shape) lives in
/// <c>CommandDispatcherFindingStatusTests</c>; this file proves the computation itself.
/// </summary>
public sealed class FindingStalenessEvaluatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "callboard-staleness-evaluator-tests-" + Guid.NewGuid().ToString("N"));

    public FindingStalenessEvaluatorTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FindingCardFields Fields(FindingExtent extent, FindingExtentFingerprint? fingerprint, FindingDisposition? disposition = null, string? verifiedAt = "abc123") =>
        new(null, extent, verifiedAt, FindingBlindSpotDeclaration.None, fingerprint, disposition ?? FindingDisposition.Measured);

    [Fact]
    public void ExplicitExtent_ContentUnchanged_IsCurrent()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "unchanged");
        var extent = FindingExtent.Explicit(["a.cs"]);
        var fingerprint = FindingExtentFingerprint.Compute(extent, _root);

        var result = FindingStalenessEvaluator.Evaluate(Fields(extent, fingerprint), _root);

        Assert.Equal(FindingStalenessStatus.Current, result);
    }

    [Fact]
    public void ExplicitExtent_UnrelatedFileChangedOutsideTheExtent_RemainsCurrent()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "unchanged");
        File.WriteAllText(Path.Combine(_root, "b.cs"), "original");
        var extent = FindingExtent.Explicit(["a.cs"]);
        var fingerprint = FindingExtentFingerprint.Compute(extent, _root);

        // b.cs is outside the declared extent — changing it must not affect a.cs's staleness.
        File.WriteAllText(Path.Combine(_root, "b.cs"), "changed");

        var result = FindingStalenessEvaluator.Evaluate(Fields(extent, fingerprint), _root);

        Assert.Equal(FindingStalenessStatus.Current, result);
    }

    [Fact]
    public void ExplicitExtent_ContentChanged_IsStale_AndTheReasonCallsForReVerification_NeverRefutation()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "original");
        var extent = FindingExtent.Explicit(["a.cs"]);
        var fingerprint = FindingExtentFingerprint.Compute(extent, _root);

        File.WriteAllText(Path.Combine(_root, "a.cs"), "changed");

        var result = FindingStalenessEvaluator.Evaluate(Fields(extent, fingerprint), _root);

        var reason = result.Match(
            onCurrent: static () => throw new Xunit.Sdk.XunitException("expected Stale, got Current"),
            onStale: static reason => reason,
            onNotMeasurable: static reason => throw new Xunit.Sdk.XunitException($"expected Stale, got NotMeasurable: {reason}"),
            onNotApplicable: static reason => throw new Xunit.Sdk.XunitException($"expected Stale, got NotApplicable: {reason}"));

        Assert.Contains("re-verification", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a.cs", reason, StringComparison.Ordinal);
        // "does not mean the finding was wrong" is the framing the ruling calls for — a positive
        // denial of refutation, not an absence of the word. What must never appear is an assertion
        // *that* the finding was wrong, which "does not mean ... wrong" is not.
        Assert.Contains("does not mean", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("incorrect", reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invalid", reason, StringComparison.OrdinalIgnoreCase);
    }

    // findings' "never under-report" — over the raw path a whole file's content moved, even though
    // only what mattered is whether *a* change happened, not what changed inside it.
    [Fact]
    public void ExplicitExtent_FileDeletedAfterRecording_IsStale_NotAToolFailure()
    {
        var path = Path.Combine(_root, "a.cs");
        File.WriteAllText(path, "original");
        var extent = FindingExtent.Explicit(["a.cs"]);
        var fingerprint = FindingExtentFingerprint.Compute(extent, _root);

        File.Delete(path);

        var result = FindingStalenessEvaluator.Evaluate(Fields(extent, fingerprint), _root);

        Assert.Equal("stale", result.Match(
            onCurrent: static () => "current",
            onStale: static _ => "stale",
            onNotMeasurable: static _ => "not-measurable",
            onNotApplicable: static _ => "not-applicable"));
    }

    [Fact]
    public void ExplicitExtent_FileAbsentAtRecordTime_ThenCreated_IsStale()
    {
        // Recorded before the file existed (fingerprint sees ContentHash: null); now it exists.
        var extent = FindingExtent.Explicit(["a.cs"]);
        var fingerprint = FindingExtentFingerprint.Compute(extent, _root);
        Assert.Null(Assert.Single(fingerprint!.Files).ContentHash);

        File.WriteAllText(Path.Combine(_root, "a.cs"), "now it exists");

        var result = FindingStalenessEvaluator.Evaluate(Fields(extent, fingerprint), _root);

        Assert.Equal("stale", result.Match(
            onCurrent: static () => "current",
            onStale: static _ => "stale",
            onNotMeasurable: static _ => "not-measurable",
            onNotApplicable: static _ => "not-applicable"));
    }

    // §6 block B's own shipped writer recorded an Explicit extent before this field existed —
    // ExtentFingerprint is null on such a card. Never Current for "never measured".
    [Fact]
    public void ExplicitExtent_WithNoRecordedFingerprint_IsNotMeasurable_NeverCurrent()
    {
        var extent = FindingExtent.Explicit(["a.cs"]);

        var result = FindingStalenessEvaluator.Evaluate(Fields(extent, fingerprint: null), _root);

        Assert.Equal("not-measurable", result.Match(
            onCurrent: static () => "current",
            onStale: static _ => "stale",
            onNotMeasurable: static _ => "not-measurable",
            onNotApplicable: static _ => "not-applicable"));
    }

    [Fact]
    public void InstrumentExtent_IsNotMeasurable_AndNamesTheCommandToReRun()
    {
        var extent = FindingExtent.Instrument("make gates");

        var result = FindingStalenessEvaluator.Evaluate(Fields(extent, fingerprint: null), _root);

        var reason = result.Match(
            onCurrent: static () => throw new Xunit.Sdk.XunitException("expected NotMeasurable, got Current"),
            onStale: static reason => throw new Xunit.Sdk.XunitException($"expected NotMeasurable, got Stale: {reason}"),
            onNotMeasurable: static reason => reason,
            onNotApplicable: static reason => throw new Xunit.Sdk.XunitException($"expected NotMeasurable, got NotApplicable: {reason}"));

        Assert.Contains("make gates", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockScopeExtent_IsNotMeasurable_NeverCurrent()
    {
        var result = FindingStalenessEvaluator.Evaluate(Fields(FindingExtent.BlockScope, fingerprint: null), _root);

        Assert.Equal("not-measurable", result.Match(
            onCurrent: static () => "current",
            onStale: static _ => "stale",
            onNotMeasurable: static _ => "not-measurable",
            onNotApplicable: static _ => "not-applicable"));
    }

    // 6.6's structural demonstration: an ArguedClean finding whose Extent/ExtentFingerprint, if
    // they were consulted, would say Stale (the file changed) — but the answer is NotApplicable,
    // proving Evaluate's ArguedClean arm never reaches the Measured code path at all. What would
    // have to break for this to go red: FindingStalenessEvaluator.Evaluate falling through to
    // EvaluateMeasured for an ArguedClean finding instead of short-circuiting on Disposition first.
    [Fact]
    public void ArguedCleanDisposition_IsNotApplicable_EvenWhenTheExtentWouldOtherwiseReportStale()
    {
        File.WriteAllText(Path.Combine(_root, "a.cs"), "original");
        var extent = FindingExtent.Explicit(["a.cs"]);
        var fingerprint = FindingExtentFingerprint.Compute(extent, _root);
        File.WriteAllText(Path.Combine(_root, "a.cs"), "changed — would report Stale under Measured");

        var result = FindingStalenessEvaluator.Evaluate(
            Fields(extent, fingerprint, disposition: FindingDisposition.ArguedClean, verifiedAt: "state-9"), _root);

        var reason = result.Match(
            onCurrent: static () => throw new Xunit.Sdk.XunitException("expected NotApplicable, got Current"),
            onStale: static reason => throw new Xunit.Sdk.XunitException($"expected NotApplicable, got Stale: {reason}"),
            onNotMeasurable: static reason => throw new Xunit.Sdk.XunitException($"expected NotApplicable, got NotMeasurable: {reason}"),
            onNotApplicable: static reason => reason);

        Assert.Contains("state-9", reason, StringComparison.Ordinal);
        Assert.Contains("argued", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArguedCleanDisposition_WithNoVerifiedAtRecorded_StillReadsAsNotApplicable_AndSaysSo()
    {
        var result = FindingStalenessEvaluator.Evaluate(
            Fields(FindingExtent.BlockScope, fingerprint: null, disposition: FindingDisposition.ArguedClean, verifiedAt: null), _root);

        var reason = result.Match(
            onCurrent: static () => throw new Xunit.Sdk.XunitException("expected NotApplicable, got Current"),
            onStale: static reason => throw new Xunit.Sdk.XunitException($"expected NotApplicable, got Stale: {reason}"),
            onNotMeasurable: static reason => throw new Xunit.Sdk.XunitException($"expected NotApplicable, got NotMeasurable: {reason}"),
            onNotApplicable: static reason => reason);

        Assert.Contains("no verified_at", reason, StringComparison.OrdinalIgnoreCase);
    }
}
