namespace Callboard.Index;

/// <summary>
/// What <see cref="IndexPopulator.Populate"/> did: how many cards it indexed, and every card file
/// that failed to parse along the way — file path and reason, not silently dropped
/// (record-retrieval: a corrupt card degrades the rebuild, it does not abort it or vanish from the
/// report). Block B surfaces <see cref="Failures"/> in the <c>index rebuild</c> verb's JSON; this
/// type only carries the data.
/// </summary>
internal sealed record IndexPopulationResult(
    int IndexedCardCount,
    IReadOnlyList<(string FilePath, string Reason)> Failures);
