using System.Runtime.CompilerServices;

namespace Callboard.Tests;

/// <summary>
/// §5 remediation (DEVLOG §5, supervisor finding B3): <c>reviewed_state</c> is modelled
/// (<see cref="Callboard.Cards.BlockCardFields.ReviewedState"/>), parsed off a hand-authored card
/// and re-emitted, but §5 deliberately built no producer for it. Wiring a flag onto <c>block
/// transition ... approve</c> would only be a lesser version of 8.2 ("Record
/// <c>reviewed_state</c> as the exact state certified, including uncommitted content") — a
/// genuinely larger job than stamping a commit at approval — and building that lesser version here
/// is precisely the half-version of §8 the architect ruled this section must not build.
///
/// <para>
/// This test <b>is</b> the recorded deferral, per this codebase's own standing rule that an
/// accepted trade-off is held in a must-be-inverted test, not a bullet in a file nobody re-reads.
/// It asserts today's actual absence of a producer — 8.2 <b>must invert it</b>, replacing this test
/// with one proving the real producer records the exact certified state, before that section can
/// proceed, rather than letting the deferral lapse silently.
/// </para>
/// </summary>
public sealed class ReviewedStateProducerTests
{
    /// <summary>
    /// True today: no file under <c>src/Callboard</c> other than the model/round-trip layer
    /// (<see cref="Callboard.Cards.BlockCardFields"/> itself, which declares the field and assigns
    /// it in its constructor/<c>with</c>-lowered init accessor — the round trip's own plumbing, not
    /// a producer — and <c>CardFileWriter</c>, which re-emits whatever <c>CardFileParser</c> already
    /// read off the card's own text) ever references the token <c>ReviewedState</c>. So nothing in
    /// this build can set it to anything other than what a human hand-wrote on the card. 8.2 breaks
    /// this test the moment it adds a real producer (a <see cref="Callboard.Cards.CardStore"/>
    /// method, a CLI flag, or both) — which is the trigger for 8.2 to replace this test with one
    /// that proves the producer records the exact certified state, not merely that a producer
    /// exists.
    /// </summary>
    [Fact]
    public void NoProductionCodePath_SetsReviewedState_UntilSection8Point2()
    {
        var sourceRoot = SourceDirectory();
        Assert.True(Directory.Exists(sourceRoot), $"expected the Callboard source tree at '{sourceRoot}'.");

        var modelRoundTripFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "BlockCardFields.cs", // declares the field; the constructor / with-lowered init accessor is the round trip's own assignment, not a producer
            "CardFileWriter.cs",  // re-emits whatever CardFileParser already parsed off the card's own text; never invents a value
        };

        var offendingFiles = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !modelRoundTripFiles.Contains(Path.GetFileName(path)))
            .Where(path => File.ReadAllText(path).Contains("ReviewedState", StringComparison.Ordinal))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offendingFiles.Count == 0,
            "a production code path outside the model/round-trip layer now references ReviewedState " +
            $"({string.Join(", ", offendingFiles)}) — that is 8.2's producer landing; replace this " +
            "test with one proving it records the exact certified state, not merely that one exists.");
    }

    private static string SourceDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "src", "Callboard"));
}
